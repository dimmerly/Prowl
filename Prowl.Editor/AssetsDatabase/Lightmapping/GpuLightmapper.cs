// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Lightmapping;

/// <summary>What one bake traces, laid out as <see cref="LightmapperShader"/> reads it.</summary>
internal sealed class LightmapperInput
{
    public readonly List<Float3> Positions = [];   // three per triangle, world space
    public readonly List<Float3> Normals = [];     // three per triangle, world space
    public readonly List<Float4> UVs = [];         // three per triangle: first UV set, second UV set
    public readonly List<int> Materials = [];      // one per triangle
    public readonly List<float> MaterialData = []; // LightmapperShader.MaterialStride float4 per material
    public readonly List<uint> Maps = [];          // LightmapperShader.MapStride uint4 per material
    public readonly List<uint> Pool = [];          // RGBA8 texels of every map
    public readonly List<float> Lights = [];       // LightmapperShader.LightStride float4 per light
    public readonly List<float> Texels = [];       // LightmapperShader.TexelStride float4 per texel
    public float[]? Sky;                           // SkyWidth x SkyHeight float4, null for none
    public int Bounces;
    public bool Cull;
    public bool IgnoreAlbedo;

    public int TriangleCount => Materials.Count;
    public int LightCount => Lights.Count / (LightmapperShader.LightStride * 4);
    public int TexelCount => Texels.Count / (LightmapperShader.TexelStride * 4);
}

/// <summary>
/// A <see cref="LightmapperInput"/> turned into the arrays the GPU buffers hold, BVH included. Pure CPU work, so it is
/// built on a worker thread and only uploaded on the main one.
/// </summary>
internal sealed class LightmapperScene
{
    public required float[] Bounds, Triangles, Normals, UVs, MaterialData, Lights, Texels;
    public required float[]? Sky;
    public required uint[] Links, TriangleMaterials, Maps, Pool;
    public required int Bounces, LightCount, TexelCount, TriangleCount;
    public required bool Cull, IgnoreAlbedo;
    public required float Extent;

    public static LightmapperScene Build(LightmapperInput input)
    {
        LightmapBvh bvh = LightmapBvh.Build(input.Positions);

        // Triangles in leaf order: first corner, then the two edges
        int count = input.TriangleCount;
        var triangles = new float[count * 12];
        var normals = new float[count * 12];
        var uvs = new float[count * 12];
        var materials = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int src = bvh.Order[i];
            Float3 a = input.Positions[src * 3], b = input.Positions[src * 3 + 1], c = input.Positions[src * 3 + 2];
            Put(triangles, i * 12, a);
            Put(triangles, i * 12 + 4, b - a);
            Put(triangles, i * 12 + 8, c - a);
            for (int k = 0; k < 3; k++)
            {
                Put(normals, i * 12 + k * 4, input.Normals[src * 3 + k]);
                Float4 uv = input.UVs[src * 3 + k];
                uvs[i * 12 + k * 4] = uv.X; uvs[i * 12 + k * 4 + 1] = uv.Y; uvs[i * 12 + k * 4 + 2] = uv.Z; uvs[i * 12 + k * 4 + 3] = uv.W;
            }
            materials[i] = (uint)input.Materials[src];
        }

        // A scene's size sets how far rays go and how far they start off their surface
        Float3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Float3 p in input.Positions)
        {
            min = new Float3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
            max = new Float3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
        }

        return new LightmapperScene
        {
            Bounds = bvh.Bounds, Links = bvh.Links, Triangles = triangles, Normals = normals, UVs = uvs,
            TriangleMaterials = materials, MaterialData = [.. input.MaterialData], Maps = [.. input.Maps], Pool = [.. input.Pool],
            Lights = [.. input.Lights], Texels = [.. input.Texels], Sky = input.Sky,
            Bounces = input.Bounces, LightCount = input.LightCount, TexelCount = input.TexelCount, TriangleCount = count,
            Cull = input.Cull, IgnoreAlbedo = input.IgnoreAlbedo, Extent = Float3.Length(max - min),
        };
    }

    private static void Put(float[] into, int at, Float3 v)
    {
        into[at] = v.X; into[at + 1] = v.Y; into[at + 2] = v.Z;
    }
}

/// <summary>Every texel's light so far, read back from the GPU.</summary>
internal sealed class LightmapperResult
{
    public float[] Direct = [];     // three per texel
    public float[] Indirect = [];   // three per texel
    public float[] Variance = [];   // per texel, of the indirect light's luminance across its samples
    public float[] Direction = [];  // three per texel: the luminance weighted mean arrival direction, as long as light is directional
    public float[] Texels = [];     // the texels as validated: LightmapperShader.TexelStride float4 each
    public int Samples;
}

/// <summary>
/// Progressive lightmapping on the GPU, never waiting on it. Each <see cref="Advance"/> retires the dispatches the GPU
/// has finished and queues more, keeping two in flight so the GPU always has the next one waiting. A dispatch is sized
/// from the measured trace rate to take about <see cref="TargetDispatchSeconds"/>: long enough that submission overhead
/// doesn't matter, short enough that the editor's own frames get the GPU in between and the driver never times out.
/// Headless the buffers stay empty, everything completes at once and every texel reads black.
/// </summary>
internal sealed class GpuLightmapper : IDisposable
{
    private const int GroupSize = 64;
    private const int MaxGroups = 65535;
    private const int MaxTexelsPerDispatch = GroupSize * MaxGroups;
    private const int MaxSamplesPerDispatch = 64;
    private const int MaxInFlight = 2;
    private const double TargetDispatchSeconds = 0.03;
    private const long MinPathsPerDispatch = 1 << 12;
    private const long StartPathsPerDispatch = 1 << 15;

    // One compiled shader for the editor's lifetime: compiling it is the slow part, and buffers are rebound per bake
    private static ComputeShader? s_shader;
    private static int s_validate, s_trace, s_probe;

    private readonly List<ComputeBuffer> _buffers = [];
    private readonly ComputeBuffer _texels, _direct, _indirect, _direction;
    private readonly int _texelCount;
    private readonly int _targetSamples;

    private readonly struct Submission(ulong id, long paths, double submittedAt, int samplesDoneAfter)
    {
        public readonly ulong Id = id;
        public readonly long Paths = paths;
        public readonly double SubmittedAt = submittedAt;
        public readonly int SamplesDoneAfter = samplesDoneAfter;   // every texel has this many once this one is done
    }

    private readonly Queue<Submission> _inFlight = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastRetiredAt;
    private double _pathsPerSecond;   // smoothed, 0 until measured
    private long _budget = StartPathsPerDispatch;
    private int _issuedSamples;       // every texel has had this many queued
    private int _issueOffset;         // the next texel of a sweep split over several dispatches

    // Probes, once BeginProbes has run
    private ComputeBuffer? _probeSH;
    private int _probeCount, _probeSamples, _probeIssued, _probeBatch;

    /// <summary>Paths every texel has, counted only once the GPU has finished them.</summary>
    public int SamplesDone { get; private set; }
    public int TargetSamples => _targetSamples;
    public int TexelCount => _texelCount;
    public bool TraceDone => SamplesDone >= _targetSamples;

    /// <summary>Paths a second as measured, 0 until a dispatch has been timed.</summary>
    public double PathsPerSecond => _pathsPerSecond;

    /// <summary>Starts compiling the kernels on a worker thread. The first dispatch waits for it if it hasn't finished.</summary>
    public static Task CompileAsync()
    {
        EnsureShader();
        return s_shader!.CompileAsync();
    }

    private static void EnsureShader()
    {
        if (s_shader.IsValid()) return;
        s_shader = ComputeShader.FromSource("Lightmapper", LightmapperShader.Source);
        s_validate = s_shader.FindKernel("Validate");
        s_trace = s_shader.FindKernel("Trace");
        s_probe = s_shader.FindKernel("Probe");
    }

    public GpuLightmapper(LightmapperScene scene, int samples)
    {
        EnsureShader();
        ComputeShader shader = s_shader!;
        _texelCount = scene.TexelCount;
        _targetSamples = Math.Max(1, samples);

        shader.SetInt("_LightCount", scene.LightCount);
        shader.SetInt("_Bounces", Math.Max(0, scene.Bounces));
        shader.SetInt("_Cull", scene.Cull ? 1 : 0);
        shader.SetInt("_IgnoreAlbedo", scene.IgnoreAlbedo ? 1 : 0);
        shader.SetInt("_Sky", scene.Sky != null ? 1 : 0);
        shader.SetFloat("_Bias", MathF.Max(1e-3f, scene.Extent * 2e-6f));
        shader.SetFloat("_MaxDistance", MathF.Max(1e4f, scene.Extent * 4f));

        Shared("Lightmap_Bounds", Upload(scene.Bounds, 4));
        Shared("Lightmap_Links", Upload(scene.Links, 2));
        Shared("Lightmap_Triangles", Upload(scene.Triangles, 4));
        Shared("Lightmap_TriangleMaterials", Upload(scene.TriangleMaterials, 1));
        Shared("Lightmap_Normals", Upload(scene.Normals, 4));
        Shared("Lightmap_UVs", Upload(scene.UVs, 4));
        Shared("Lightmap_Materials", Upload(scene.MaterialData, 4));
        Shared("Lightmap_Maps", Upload(scene.Maps, 4));
        Shared("Lightmap_Pool", Upload(scene.Pool, 1));
        Shared("Lightmap_Lights", Upload(scene.Lights, 4));
        Shared("Lightmap_Sky", Upload(scene.Sky ?? [], 4));

        _texels = Upload(scene.Texels, 4);
        _direct = Upload(new float[Math.Max(1, _texelCount) * 4], 4);
        _indirect = Upload(new float[Math.Max(1, _texelCount) * 4], 4);
        _direction = Upload(new float[Math.Max(1, _texelCount) * 4], 4);
        shader.SetBuffer(s_validate, "Lightmap_Texels", _texels);
        shader.SetBuffer(s_trace, "Lightmap_Texels", _texels);
        shader.SetBuffer(s_trace, "Lightmap_Direct", _direct);
        shader.SetBuffer(s_trace, "Lightmap_Indirect", _indirect);
        shader.SetBuffer(s_trace, "Lightmap_Direction", _direction);
    }

    private void Shared(string name, ComputeBuffer buffer)
    {
        foreach (int kernel in (ReadOnlySpan<int>)[s_validate, s_trace, s_probe])
            s_shader!.SetBuffer(kernel, name, buffer);
    }

    /// <summary>Queues the pass that finds texels starting inside geometry and pushes them out or drops them. Once, first.</summary>
    public void Validate()
    {
        ulong last = 0;
        for (int offset = 0; offset < _texelCount; offset += MaxTexelsPerDispatch)
        {
            int count = Math.Min(MaxTexelsPerDispatch, _texelCount - offset);
            s_shader!.SetInt("_Offset", offset);
            s_shader.SetInt("_Count", count);
            last = s_shader.Dispatch(s_validate, (count + GroupSize - 1) / GroupSize, 1, 1);
        }
        _inFlight.Enqueue(new Submission(last, 0, _clock.Elapsed.TotalSeconds, 0));
    }

    /// <summary>Retires finished dispatches and queues more paths. Returns at once.</summary>
    public void Advance()
    {
        Retire();
        while (_inFlight.Count < MaxInFlight && _issuedSamples < _targetSamples && _texelCount > 0)
        {
            double now = _clock.Elapsed.TotalSeconds;
            long budget = Math.Max(MinPathsPerDispatch, _budget);
            int offset, count, samples;
            if (_issueOffset == 0 && _texelCount <= budget)
            {
                // Every texel in one dispatch, as many paths each as the budget allows
                offset = 0;
                count = _texelCount;
                samples = (int)Math.Clamp(budget / _texelCount, 1, Math.Min(MaxSamplesPerDispatch, _targetSamples - _issuedSamples));
            }
            else
            {
                // Too many texels for one: one path each over a run of them, the sweep finishing over several dispatches
                offset = _issueOffset;
                count = (int)Math.Min(Math.Min(budget, MaxTexelsPerDispatch), _texelCount - offset);
                samples = 1;
            }

            s_shader!.SetInt("_Offset", offset);
            s_shader.SetInt("_Count", count);
            s_shader.SetInt("_Iteration", _issuedSamples);
            s_shader.SetInt("_Samples", samples);
            ulong id = s_shader.Dispatch(s_trace, (count + GroupSize - 1) / GroupSize, 1, 1);

            if (offset + count >= _texelCount)
            {
                _issueOffset = 0;
                _issuedSamples += samples;
            }
            else _issueOffset = offset + count;
            // A sweep's last dispatch completes its samples for every texel; earlier ones leave the count where it was
            _inFlight.Enqueue(new Submission(id, (long)count * samples, now, _issuedSamples));
        }
    }

    private void Retire()
    {
        while (_inFlight.Count > 0 && ComputeShader.IsComplete(_inFlight.Peek().Id))
        {
            Submission done = _inFlight.Dequeue();
            double now = _clock.Elapsed.TotalSeconds;
            SamplesDone = Math.Max(SamplesDone, done.SamplesDoneAfter);

            // The GPU worked on this one from when it was queued, or when the one before it finished if that was later
            double seconds = now - Math.Max(done.SubmittedAt, _lastRetiredAt);
            _lastRetiredAt = now;
            if (done.Paths <= 0 || seconds <= 1e-4) continue;
            double rate = done.Paths / seconds;
            _pathsPerSecond = _pathsPerSecond <= 0 ? rate : _pathsPerSecond * 0.7 + rate * 0.3;
            long next = (long)(_pathsPerSecond * TargetDispatchSeconds);
            _budget = Math.Clamp(next, MinPathsPerDispatch, Math.Max(MinPathsPerDispatch, _budget * 2));
        }
    }

    /// <summary>Whether everything queued so far has finished on the GPU.</summary>
    public bool Idle
    {
        get { Retire(); return _inFlight.Count == 0; }
    }

    /// <summary>Each texel's averages so far. Black for a texel nothing reached. Waits for the GPU.</summary>
    public LightmapperResult Read()
    {
        int n = Math.Max(1, _texelCount);
        var direct = new float[n * 4];
        var indirect = new float[n * 4];
        var texels = new float[n * LightmapperShader.TexelStride * 4];
        var arrivals = new float[n * 4];
        _direct.GetData(direct);
        _indirect.GetData(indirect);
        _texels.GetData(texels);
        _direction.GetData(arrivals);

        var result = new LightmapperResult
        {
            Direct = new float[_texelCount * 3],
            Indirect = new float[_texelCount * 3],
            Variance = new float[_texelCount],
            Direction = new float[_texelCount * 3],
            Texels = texels,
            Samples = SamplesDone,
        };
        for (int i = 0; i < _texelCount; i++)
        {
            float samples = direct[i * 4 + 3];
            if (samples <= 0f) continue;
            float inv = 1f / samples;
            for (int c = 0; c < 3; c++)
            {
                result.Direct[i * 3 + c] = direct[i * 4 + c] * inv;
                result.Indirect[i * 3 + c] = indirect[i * 4 + c] * inv;
            }
            float mean = 0.2126f * result.Indirect[i * 3] + 0.7152f * result.Indirect[i * 3 + 1] + 0.0722f * result.Indirect[i * 3 + 2];
            result.Variance[i] = MathF.Max(0f, indirect[i * 4 + 3] * inv - mean * mean);
            float weight = arrivals[i * 4 + 3];
            if (weight > 0f)
                for (int c = 0; c < 3; c++) result.Direction[i * 3 + c] = arrivals[i * 4 + c] / weight;
        }
        return result;
    }

    // ---- probes ----

    /// <summary>Sets up baking the radiance around each position, from <paramref name="samples"/> rays a probe.</summary>
    public void BeginProbes(IReadOnlyList<Float3> positions, int samples)
    {
        _probeCount = positions.Count;
        _probeSamples = Math.Max(1, samples);
        _probeIssued = 0;
        _probeBatch = 0;
        if (_probeCount == 0) return;

        var packed = new float[_probeCount * 4];
        for (int i = 0; i < _probeCount; i++)
        {
            packed[i * 4] = positions[i].X; packed[i * 4 + 1] = positions[i].Y; packed[i * 4 + 2] = positions[i].Z;
        }
        ComputeBuffer probes = Upload(packed, 4);
        _probeSH = Upload(new float[_probeCount * 9 * 4], 4);
        s_shader!.SetBuffer(s_probe, "Lightmap_Probes", probes);
        s_shader.SetBuffer(s_probe, "Lightmap_ProbeSH", _probeSH);
    }

    /// <summary>Retires and queues probe rays like <see cref="Advance"/>. True once every ray has finished.</summary>
    public bool AdvanceProbes()
    {
        Retire();
        if (_probeCount == 0) return true;
        while (_inFlight.Count < MaxInFlight && _probeIssued < _probeSamples)
        {
            // A probe ray costs about what a texel path does
            int rays = (int)Math.Clamp(Math.Max(MinPathsPerDispatch, _budget) / _probeCount, 1, Math.Min(MaxSamplesPerDispatch, _probeSamples - _probeIssued));
            s_shader!.SetInt("_Count", _probeCount);
            s_shader.SetInt("_Iteration", _probeBatch++);
            s_shader.SetInt("_ProbeRays", rays);
            // Each batch adds its share of the direct lights' samples too, so they average over the whole bake
            s_shader.SetFloat("_ProbeDirectWeight", rays);
            ulong id = s_shader.Dispatch(s_probe, (_probeCount + GroupSize - 1) / GroupSize, 1, 1);
            _probeIssued += rays;
            _inFlight.Enqueue(new Submission(id, (long)rays * _probeCount, _clock.Elapsed.TotalSeconds, SamplesDone));
        }
        return _probeIssued >= _probeSamples && _inFlight.Count == 0;
    }

    /// <summary>The baked probes as order 2 spherical harmonics. Waits for the GPU.</summary>
    public SphericalHarmonicsL2[] ReadProbes()
    {
        if (_probeCount == 0 || _probeSH == null) return [];
        var raw = new float[_probeCount * 9 * 4];
        _probeSH.GetData(raw);
        var result = new SphericalHarmonicsL2[_probeCount];
        float scale = 1f / _probeSamples;
        for (int i = 0; i < _probeCount; i++)
        {
            Float3 C(int k) => new Float3(raw[(i * 9 + k) * 4], raw[(i * 9 + k) * 4 + 1], raw[(i * 9 + k) * 4 + 2]) * scale;
            result[i] = new SphericalHarmonicsL2 { C0 = C(0), C1 = C(1), C2 = C(2), C3 = C(3), C4 = C(4), C5 = C(5), C6 = C(6), C7 = C(7), C8 = C(8) };
        }
        return result;
    }

    // A buffer of elements of a few 32 bit words, with one empty element when there is nothing, since a buffer can't be empty
    private ComputeBuffer Upload<T>(T[] data, int wordsPerElement) where T : unmanaged
    {
        int elements = Math.Max(1, data.Length / wordsPerElement);
        var buffer = new ComputeBuffer(elements, wordsPerElement * 4);
        if (data.Length > 0) buffer.SetData(data);
        _buffers.Add(buffer);
        return buffer;
    }

    /// <summary>Frees the buffers once the GPU is done with them. The shader stays compiled for the next bake.</summary>
    public void Dispose()
    {
        foreach (ComputeBuffer buffer in _buffers) buffer.Dispose();
        _buffers.Clear();
        _inFlight.Clear();
    }
}
