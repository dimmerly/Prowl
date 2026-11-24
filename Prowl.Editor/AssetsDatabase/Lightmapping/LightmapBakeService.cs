// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Prowl.Aperture;
using Prowl.Echo;
using Prowl.Editor.Importers;

using Prowl.Editor.GUI.Panels;
using Prowl.Editor.GUI.SceneView;
using Prowl.Editor.Projects;
using Prowl.Runtime;
using Prowl.Runtime.CSG;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using S = Prowl.Editor.Lightmapping.LightmapperShader;

namespace Prowl.Editor.Lightmapping;

/// <summary>
/// Drives one progressive GPU lightmap bake over every loaded scene of the edit world, so shadows and bounced light
/// cross between scenes. Each scene gets its own half float EXR pages in a SceneName_lightmaps folder beside its file,
/// with a direction page beside each for a directional bake, its own placements and its own light probes, and is marked
/// dirty. A bake never saves a scene.
/// <para>
/// The editor never waits on it. <see cref="Start"/> only reads the scene; packing, rasterizing, the BVH, finding
/// seams, decoding maps and compiling the kernels run on worker threads. Tracing queues GPU work without blocking and
/// sizes each dispatch from the measured rate. While it traces, the running result is shown in the scene as a preview,
/// refreshed every second or so. Denoising, stitching and encoding the pages run on a worker again, and only the import
/// is on the main thread.
/// </para>
/// <para>
/// <see cref="ShowOverlay"/> runs the same preparation without tracing and shows charts, buried texels, overlapping UVs
/// or seams on the pages instead of light, in the Lightmap Texels debug view.
/// </para>
/// </summary>
public sealed class LightmapBakeService
{
    /// <summary>The editor's bake, polled every editor frame.</summary>
    public static LightmapBakeService Instance { get; } = new();

    private enum Phase { Preparing, Tracing, Probes, Composing, Applying, Overlay }

    // One loaded scene's share of a bake: its charts, its pages and its probes.
    private sealed class SceneBake(OpenScene scene, SceneSection section)
    {
        public readonly OpenScene Scene = scene;
        public readonly SceneSection Section = section;
        public readonly List<LightmapChart> Charts = [];
        public readonly List<Component> Renderers = [];   // parallel to Charts
        public readonly List<string> Names = [];          // parallel to Charts, read on the main thread
        public readonly List<Float3> Probes = [];
        public readonly List<LightmapPage> Pages = [];
        public List<SeamEdge> Seams = [];
        public readonly List<Texture2D> PreviewPages = [];
        public readonly List<Texture2D> PreviewDirections = [];
        public List<string> ComposedFiles = [];
        public List<string> ComposedDirectionFiles = [];
    }

    // A static renderer as read on the main thread. Its arrays are only read after, on a worker.
    private sealed class MeshSource
    {
        public required SceneBake Share;
        public required Component Renderer;
        public required string Name;
        public required Float3[] Vertices;
        public required Float3[]? Normals;
        public required Float2[]? UV0, UV1, ChartUVs;
        public required uint[] Indices;
        public required SubMeshDescriptor[] SubMeshes;
        public required (int Material, bool Transparent)[] Materials;   // per submesh
        public required Float4x4 Transform;
        public required bool Occludes;
    }

    // A map the tracer reads: decoded on a worker from its file, or read from the GPU up front when it has none.
    private sealed class MapSource
    {
        public string? File;
        public byte[]? Pixels;
        public int Width, Height;
        public bool Srgb;
    }

    // Everything one bake owns, so a cancelled bake's worker can finish into nothing while the next one starts.
    private sealed class Job
    {
        public readonly CancellationTokenSource Cancel = new();
        public required Scene World;
        public required LightmapBakeSettings Settings;
        public LightmapOverlay Overlay;   // None for a bake
        public readonly List<SceneBake> Scenes = [];
        public readonly LightmapperInput Input = new();
        public readonly List<MeshSource> Meshes = [];
        public readonly List<MapSource> MapSources = [];
        public readonly List<(int Source, int UVSet)> MapSlots = [];   // two per material: base, emission
        public readonly Dictionary<Texture2D, int> MapIndex = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Material, (int Index, bool Transparent)> Materials = new(ReferenceEqualityComparer.Instance);
        public string? TempFolder;
        public readonly List<string> PrepareReport = [];   // warnings found on the worker, logged on the main thread

        public Phase Phase;
        public Task<LightmapperScene>? Prepare;
        public Task? Compile;
        public GpuLightmapper? Gpu;
        public LightmapperResult? Result;
        public SphericalHarmonicsL2[] ProbeSH = [];
        public Task? Compose;
        public Task<List<List<float[]>>>? OverlayCompose;

        // The preview: a read waits for the GPU to go idle so it never stalls, then composes on a worker
        public bool PreviewWanted;
        public Task<List<ComposedScene>>? PreviewCompose;
        public double NextPreviewAt;
        public double PreviewStartedAt;
    }

    private const int Padding = 2;
    private const int MaxMapSize = 512;   // albedo and emission maps are traced at most this large
    private const double FirstPreviewSeconds = 0.5;
    private const double MinPreviewInterval = 1.0;
    private const int PreviewDenoisePasses = 2;

    private Job? _job;
    private readonly Stopwatch _clock = new();

    // The overlay on show: the scenes it was set on, and its pages, until ClearOverlay
    private readonly List<SceneBake> _overlayScenes = [];

    /// <summary>Whether the scene shows the bake as it goes. On by default; off traces a little faster.</summary>
    public bool LivePreview { get; set; } = true;

    /// <summary>The overlay on show, None when the scene shows its light.</summary>
    public LightmapOverlay Overlay { get; private set; }

    public bool IsBaking => _job != null;
    public float Progress { get; private set; }
    public string Status { get; private set; } = "Idle";

    /// <summary>How long the running bake has taken so far.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Estimated time left for the running bake's paths, null until the rate is measured. Probes and writing come after.</summary>
    public TimeSpan? Remaining { get; private set; }

    /// <summary>Why the last <see cref="Start"/> or <see cref="ShowOverlay"/> refused, for the UI to show. Null after one that began.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The world the in-flight bake belongs to, or null when idle. Only one bake runs at a time.</summary>
    public Scene? TargetScene => _job?.World;

    /// <summary>The loaded scenes of <paramref name="world"/> a bake covers, in order. Empty outside the edit world.</summary>
    public static List<OpenScene> BakedScenes(Scene world)
        => [.. EditorSceneManager.OpenScenes.Where(o => o.Section is { IsLoaded: true } s && ReferenceEquals(s.Instance, world))];

    /// <summary>A loaded scene with no file yet, which blocks a bake since its pages would have nowhere to go.</summary>
    public static OpenScene? UntitledScene(Scene world) => BakedScenes(world).FirstOrDefault(o => o.Path == null);

    private bool Refuse(string reason)
    {
        FailureReason = reason;
        Runtime.Debug.LogWarning($"[Lightmap] {reason}");
        return false;
    }

    /// <summary>
    /// Begin one bake of every loaded scene of <paramref name="world"/> with the active scene's
    /// <paramref name="settings"/>. Returns false, with <see cref="FailureReason"/>, when there is nothing to bake or a
    /// scene is untitled. Only reads the scene here; the heavy preparation runs on worker threads.
    /// </summary>
    public bool Start(Scene world, LightmapBakeSettings settings) => Begin(world, settings, LightmapOverlay.None);

    /// <summary>
    /// Shows <paramref name="overlay"/> on the pages a bake with <paramref name="settings"/> would make, in the Lightmap
    /// Texels debug view, until <see cref="ClearOverlay"/> or the next bake. Prepares like a bake but traces nothing
    /// beyond finding the buried texels. None clears it.
    /// </summary>
    public bool ShowOverlay(Scene world, LightmapBakeSettings settings, LightmapOverlay overlay)
    {
        if (overlay == LightmapOverlay.None)
        {
            ClearOverlay();
            return true;
        }
        return Begin(world, settings, overlay);
    }

    private bool Begin(Scene world, LightmapBakeSettings settings, LightmapOverlay overlay)
    {
        FailureReason = null;
        if (IsBaking) return Refuse("A bake is already running.");
        if (Application.IsPlaying) return Refuse("Leave play mode before baking.");
        List<OpenScene> baked = BakedScenes(world);
        if (baked.Count == 0) return Refuse("Only the scenes open in the editor can be baked.");
        if (overlay == LightmapOverlay.None && baked.FirstOrDefault(o => o.Path == null) is { } untitled)
            return Refuse($"Save '{untitled.Name}' before baking, so its lightmaps have a folder next to its file.");

        var job = new Job { World = world, Settings = settings, Overlay = overlay };
        var bySection = new Dictionary<SceneSection, SceneBake>(ReferenceEqualityComparer.Instance);
        foreach (OpenScene open in baked)
        {
            var share = new SceneBake(open, open.Section!);
            job.Scenes.Add(share);
            bySection[share.Section] = share;
        }

        LightmapperInput input = job.Input;
        input.Bounces = settings.Bounces;
        input.Cull = settings.DoBackfaceCull;
        input.IgnoreAlbedo = settings.IgnoreAlbedo;
        input.Sky = settings.BakeSkyLighting ? SkyTable(world) : null;

        PrepareCsgModels(world, bySection);

        // Static renderers of every loaded scene. Each one that casts shadows goes into the traced scene, each one drawn
        // with lightmap UVs gets a chart in its own scene's pages.
        var noLightmapUVs = new List<string>();
        bool anyChart = false, anyTraced = false;
        foreach (GameObject go in world.Objects)
        {
            if (!go.IsStatic || !go.EnabledInHierarchy || go.Section is not { } section || !bySection.TryGetValue(section, out SceneBake? share)) continue;

            Mesh? mesh;
            IReadOnlyList<Material> mats;
            Component renderer;
            ShadowCasting casting;
            if (go.GetComponent<MeshRenderer>() is { } mr) { mesh = mr.Mesh; mats = mr.Materials; renderer = mr; casting = mr.ShadowCasting; }
            else if (go.GetComponent<SkinnedMeshRenderer>() is { } smr) { mesh = smr.SharedMesh; mats = smr.Materials; renderer = smr; casting = smr.ShadowCasting; }
            else continue;
            if (mesh.IsNotValid() || mesh.VertexCount == 0) continue;

            Float2[]? lightmapUVs = LightmapUVs(mesh);
            bool receives = casting != ShadowCasting.ShadowsOnly;
            if (receives && lightmapUVs == null) noLightmapUVs.Add(go.Name);

            var subMeshes = new SubMeshDescriptor[mesh.SubMeshCount];
            var materials = new (int, bool)[subMeshes.Length];
            bool hasTriangles = false, tracedTriangles = false;
            for (int s = 0; s < subMeshes.Length; s++)
            {
                subMeshes[s] = mesh.GetSubMesh(s);
                Material? material = s < mats.Count ? mats[s] : mats.Count > 0 ? mats[^1] : default;
                materials[s] = MaterialOf(job, material);
                if (subMeshes[s].IndexCount < 3) continue;
                hasTriangles = true;
                tracedTriangles |= !materials[s].Item2;
            }

            var source = new MeshSource
            {
                Share = share, Renderer = renderer, Name = go.Name,
                Vertices = mesh.Vertices, Normals = mesh.HasNormals ? mesh.Normals : null,
                UV0 = mesh.HasUV ? mesh.UV : null, UV1 = mesh.HasUV2 ? mesh.UV2 : null,
                ChartUVs = receives ? lightmapUVs : null,
                Indices = mesh.Indices, SubMeshes = subMeshes, Materials = materials,
                Transform = go.Transform.LocalToWorldMatrix, Occludes = casting != ShadowCasting.Off,
            };
            job.Meshes.Add(source);
            anyChart |= hasTriangles && source.ChartUVs != null;
            anyTraced |= tracedTriangles && source.Occludes;
        }
        if (noLightmapUVs.Count > 0)
            Runtime.Debug.LogWarning($"[Lightmap] {noLightmapUVs.Count} static renderer(s) have no lightmap UVs, so they only cast shadows and bounce light: "
                + string.Join(", ", noLightmapUVs.Take(10)) + (noLightmapUVs.Count > 10 ? ", ..." : "") + ". Turn on Generate Lightmap UVs in their model's import settings.");

        if (!anyChart)
            return Refuse("Nothing to bake: no Static renderers with lightmap UVs. Mark the level geometry Static in the Inspector.");
        if (!anyTraced)
            return Refuse("Nothing to bake: every static renderer has shadow casting off, so there is no geometry to trace.");

        foreach (GameObject go in world.Objects)
        {
            Light? light = go.GetComponent<Light>();
            if (light.IsNotValid() || !light.EnabledInHierarchy || light.BakeMode == LightBakeMode.Realtime) continue;
            if (go.Section is not { } section || !bySection.ContainsKey(section)) continue;
            AddLight(input.Lights, light);
        }

        foreach (GameObject go in world.Objects)
        {
            LightProbeGroup? grp = go.GetComponent<LightProbeGroup>();
            if (grp.IsNotValid() || !grp.EnabledInHierarchy || go.Section is not { } section || !bySection.TryGetValue(section, out SceneBake? share)) continue;
            share.Probes.AddRange(grp.GetWorldPositions());
        }

        if (!ComputeShader.IsSupported && overlay == LightmapOverlay.None)
            Runtime.Debug.LogWarning("[Lightmap] There is no graphics device to trace on, so this bake comes out black.");

        // A bake or a new overlay replaces whatever overlay is on show
        ClearOverlay();

        CancellationToken token = job.Cancel.Token;
        job.Prepare = Task.Run(() => PrepareScene(job, token), token);
        job.Compile = GpuLightmapper.CompileAsync();
        job.Phase = Phase.Preparing;
        job.PreviewWanted = overlay == LightmapOverlay.None && LivePreview && ComputeShader.IsSupported;

        _job = job;
        _clock.Restart();
        Remaining = null;
        Progress = 0f;
        Status = "Preparing scene";
        return true;
    }

    /// <summary>Call once per editor frame while <see cref="IsBaking"/>. Never waits on the GPU or a worker.</summary>
    public void Poll()
    {
        // An overlay's colours would light the scene in any other view, so leaving the view takes it off
        if (Overlay != LightmapOverlay.None && SceneViewPanel.ActiveCamera is { } camera && camera.DebugView != DebugViewMode.LightmapTexels)
            ClearOverlay();

        if (_job is not { } job) return;
        try
        {
            switch (job.Phase)
            {
                case Phase.Preparing: PollPreparing(job); break;
                case Phase.Tracing: PollTracing(job); break;
                case Phase.Probes: PollProbes(job); break;
                case Phase.Composing: PollComposing(job); break;
                case Phase.Applying: PollApplying(job); break;
                case Phase.Overlay: PollOverlay(job); break;
            }
        }
        catch (Exception e)
        {
            Runtime.Debug.LogException(job.Overlay == LightmapOverlay.None ? "[Lightmap] Bake failed" : "[Lightmap] The overlay failed", e);
            DeleteStaging(job);
            Cleanup();
            Status = "Failed";
        }
    }

    private void PollPreparing(Job job)
    {
        if (!job.Prepare!.IsCompleted) { Status = "Preparing scene"; return; }
        LightmapperScene scene = job.Prepare.GetAwaiter().GetResult();   // rethrows what failed on the worker
        if (!job.Compile!.IsCompleted) { Status = "Compiling the tracer"; return; }

        foreach (string warning in job.PrepareReport) Runtime.Debug.LogWarning($"[Lightmap] {warning}");
        job.PrepareReport.Clear();

        job.Gpu = new GpuLightmapper(scene, job.Settings.Samples);
        job.Gpu.Validate();
        if (job.Overlay != LightmapOverlay.None)
        {
            job.Phase = Phase.Overlay;
            Status = "Checking texels";
            return;
        }

        job.Phase = Phase.Tracing;
        job.NextPreviewAt = _clock.Elapsed.TotalSeconds + FirstPreviewSeconds;
        Runtime.Debug.Log($"[Lightmap] Tracing {scene.TexelCount} texel(s) on {job.Scenes.Sum(s => s.Pages.Count)} page(s) against "
            + $"{scene.TriangleCount} triangle(s), {scene.LightCount} light(s) and {scene.Pool.Length / 1024}K map texel(s), "
            + $"{job.Scenes.Sum(s => s.Seams.Count)} seam edge(s), prepared in {_clock.Elapsed.TotalSeconds:F1}s.");
    }

    private void PollTracing(Job job)
    {
        GpuLightmapper gpu = job.Gpu!;
        job.PreviewWanted = LivePreview && ComputeShader.IsSupported;
        PollPreview(job);
        if (!job.PreviewWanted && job.Scenes.Any(share => share.PreviewPages.Count > 0)) DropPreview(job.Scenes);

        // A preview read waits for the GPU to empty, so nothing new is queued until it has run
        bool holding = job.PreviewWanted && job.PreviewCompose == null && _clock.Elapsed.TotalSeconds >= job.NextPreviewAt && !gpu.TraceDone;
        if (holding && gpu.Idle) StartPreview(job);
        if (!holding || job.PreviewCompose != null) gpu.Advance();

        int done = gpu.SamplesDone, target = gpu.TargetSamples;
        Progress = 0.85f * Math.Clamp(done / (float)target, 0f, 1f);
        Status = $"Baking {done}/{target}";
        if (gpu.PathsPerSecond > 0)
            Remaining = TimeSpan.FromSeconds((double)(target - done) * gpu.TexelCount / gpu.PathsPerSecond);
        if (!gpu.TraceDone) return;

        Remaining = null;
        gpu.BeginProbes([.. job.Scenes.SelectMany(s => s.Probes)], job.Settings.ProbeSamples);
        job.Phase = Phase.Probes;
    }

    private void PollProbes(Job job)
    {
        Status = "Baking probes";
        Progress = 0.85f;
        if (!job.Gpu!.AdvanceProbes()) return;

        // The GPU is idle by now, so neither read waits
        job.ProbeSH = job.Gpu.ReadProbes();
        job.Result = job.Gpu.Read();
        job.Gpu.Dispose();
        job.Gpu = null;

        job.TempFolder = Path.Combine(Path.GetTempPath(), "ProwlLightmaps", Guid.NewGuid().ToString("N"));
        CancellationToken token = job.Cancel.Token;
        LightmapperResult result = job.Result;
        job.Compose = Task.Run(() => ComposeFinal(job, result, token), token);
        job.Phase = Phase.Composing;
    }

    private void PollComposing(Job job)
    {
        Status = "Denoising and writing pages";
        Progress = 0.9f;
        if (!job.Compose!.IsCompleted) return;
        job.Compose.GetAwaiter().GetResult();
        job.Phase = Phase.Applying;
        Progress = 1f;
    }

    private void PollApplying(Job job)
    {
        // The result marks the scenes it changed, which play mode never does, so it waits for play to end.
        if (Application.IsPlaying)
        {
            Status = "Waiting for play mode to end";
            return;
        }
        Status = "Importing pages";
        FinalizeBake(job);
    }

    /// <summary> Cancels the current bake, if any, and resets the service to idle. The scenes keep their baked pages. </summary>
    public void Cancel()
    {
        if (_job == null) return;
        Cleanup();
        Status = "Cancelled";
    }

    // ---- preview ----

    private void StartPreview(Job job)
    {
        LightmapperResult result = job.Gpu!.Read();   // idle, so this doesn't wait
        if (result.Samples <= 0) { job.NextPreviewAt = _clock.Elapsed.TotalSeconds + FirstPreviewSeconds; return; }
        CancellationToken token = job.Cancel.Token;
        LightmapBakeSettings settings = job.Settings;
        job.PreviewStartedAt = _clock.Elapsed.TotalSeconds;
        job.PreviewCompose = Task.Run(() => job.Scenes.Select(share => LightmapComposer.Compose(share.Pages, result, PreviewDenoisePasses,
            settings.DilatePixels, settings.Directional, settings.StitchSeams ? share.Seams : null, token)).ToList(), token);
    }

    private void PollPreview(Job job)
    {
        if (job.PreviewCompose is not { IsCompleted: true } compose) return;
        job.PreviewCompose = null;
        double now = _clock.Elapsed.TotalSeconds;
        // Refresh no more often than about ten times what composing costs, so the worker stays mostly idle
        job.NextPreviewAt = now + Math.Max(MinPreviewInterval, (now - job.PreviewStartedAt) * 10);
        if (!compose.IsCompletedSuccessfully || !job.PreviewWanted) return;

        List<ComposedScene> composed = compose.Result;
        for (int s = 0; s < job.Scenes.Count; s++)
        {
            SceneBake share = job.Scenes[s];
            ShowPages(share, composed[s].Light, composed[s].Direction);
        }
    }

    // Uploads one scene's pages as its preview, made the first time and refilled after
    private static void ShowPages(SceneBake share, List<float[]> light, List<float[]> direction)
    {
        if (!share.Section.IsLoaded || share.Pages.Count == 0) return;
        for (int p = 0; p < share.Pages.Count; p++)
        {
            int size = share.Pages[p].Size;
            if (share.PreviewPages.Count <= p)
                share.PreviewPages.Add(new Texture2D((uint)size, (uint)size, Runtime.TextureFormat.RGBA16Float, mipmaps: false, srgb: false) { Name = $"Lightmap Preview {p}" });
            share.PreviewPages[p].SetMipChain([LightmapComposer.LightHalves(light[p], size)]);
            if (p < direction.Count)
            {
                if (share.PreviewDirections.Count <= p)
                    share.PreviewDirections.Add(new Texture2D((uint)size, (uint)size, Runtime.TextureFormat.RGBA8, mipmaps: false, srgb: false) { Name = $"Lightmap Direction Preview {p}" });
                share.PreviewDirections[p].SetMipChain([LightmapComposer.DirectionBytes(direction[p], size)]);
            }
        }
        if (share.Section.Lighting.Preview == null)
            share.Section.Lighting.SetPreview(new LightmapPreview(share.PreviewPages, share.PreviewDirections, Placements(share)));
    }

    private static Dictionary<Guid, LightmapPlacement> Placements(SceneBake share)
    {
        var placements = new Dictionary<Guid, LightmapPlacement>();
        for (int i = 0; i < share.Renderers.Count; i++)
        {
            Component renderer = share.Renderers[i];
            if (renderer.IsNotValid() || renderer.GameObject.IsNotValid()) continue;
            LightmapChart chart = share.Charts[i];
            placements[renderer.GameObject.Identifier] = new LightmapPlacement
            {
                Index = chart.Page,
                ScaleOffset = chart.ScaleOffset(share.Pages[chart.Page].Size),
            };
        }
        return placements;
    }

    private static void DropPreview(IEnumerable<SceneBake> scenes)
    {
        foreach (SceneBake share in scenes)
        {
            share.Section.Lighting.ClearPreview();
            foreach (Texture2D page in share.PreviewPages) page.Destroy();
            foreach (Texture2D page in share.PreviewDirections) page.Destroy();
            share.PreviewPages.Clear();
            share.PreviewDirections.Clear();
        }
    }

    // ---- overlays ----

    private void PollOverlay(Job job)
    {
        if (job.OverlayCompose == null)
        {
            if (!job.Gpu!.Idle) return;
            float[] texels = job.Gpu.Read().Texels;   // idle, so this doesn't wait
            job.Gpu.Dispose();
            job.Gpu = null;
            LightmapOverlay overlay = job.Overlay;
            job.OverlayCompose = Task.Run(() => job.Scenes.Select((share, i) => LightmapComposer.Overlay(overlay, share.Pages, texels, share.Seams, i)).ToList());
            Status = "Drawing the overlay";
            return;
        }
        if (!job.OverlayCompose.IsCompleted) return;

        List<List<float[]>> images = job.OverlayCompose.GetAwaiter().GetResult();
        for (int s = 0; s < job.Scenes.Count; s++)
            ShowPages(job.Scenes[s], images[s], []);
        _overlayScenes.AddRange(job.Scenes);
        Overlay = job.Overlay;
        if (SceneViewPanel.ActiveCamera is { } camera) camera.DebugView = DebugViewMode.LightmapTexels;

        job.Cancel.Cancel();
        _job = null;
        _clock.Stop();
        Progress = 0f;
        Status = $"Showing {job.Overlay}";
    }

    /// <summary>Takes the overlay off the scene, which shows its baked light again.</summary>
    public void ClearOverlay()
    {
        if (_overlayScenes.Count == 0 && Overlay == LightmapOverlay.None) return;
        DropPreview(_overlayScenes);
        _overlayScenes.Clear();
        Overlay = LightmapOverlay.None;
        if (SceneViewPanel.ActiveCamera is { DebugView: DebugViewMode.LightmapTexels } camera) camera.DebugView = DebugViewMode.Lit;
    }

    // ---- clearing ----

    /// <summary>Whether any loaded scene of the world has baked lightmap pages or probes to clear.</summary>
    public static bool HasBakedData(Scene world)
        => world.Sections.Any(s => !s.IsPersistent && (s.Lighting.HasLightmaps || s.Lighting.HasProbes));

    // The folder a scene's pages live in, beside its file.
    private static string LightmapFolderOf(string scenePath)
    {
        string sceneDir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "";
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        return (string.IsNullOrEmpty(sceneDir) ? "" : sceneDir + "/") + sceneName + "_lightmaps";
    }

    /// <summary>
    /// Removes every loaded scene's baked lighting: deletes each one's lightmaps folder and drops its pages, placements
    /// and probes, marking each dirty. Unloaded scenes keep theirs. Saves nothing.
    /// </summary>
    public void Clear(Scene world)
    {
        if (IsBaking) return;
        ClearOverlay();
        EditorAssetBackend? db = EditorAssetBackend.Instance;

        foreach (OpenScene open in BakedScenes(world))
        {
            if (open.Path is { } path && db != null && Project.Current != null && Directory.Exists(Path.Combine(Project.Current.AssetsPath, LightmapFolderOf(path))))
                db.DeleteAssetFolder(LightmapFolderOf(path));
            open.Section!.Lighting = new SceneLighting();
            EditorSceneManager.MarkDirty(open);
        }
        world.InvalidateProbeVolume();

        Status = "Cleared";
        Runtime.Debug.Log("[Lightmap] Cleared baked lighting.");
    }

    // ---- writing ----

    // Every page denoised, stitched and written to a temp folder outside the project, so the asset watcher never sees a
    // half written file. The main thread moves them in and imports them.
    private static void ComposeFinal(Job job, LightmapperResult result, CancellationToken token)
    {
        Directory.CreateDirectory(job.TempFolder!);
        LightmapBakeSettings settings = job.Settings;
        int n = 0;
        foreach (SceneBake share in job.Scenes)
        {
            ComposedScene composed = LightmapComposer.Compose(share.Pages, result, settings.DenoisePasses, settings.DilatePixels,
                settings.Directional, settings.StitchSeams ? share.Seams : null, token);
            var files = new List<string>();
            var directions = new List<string>();
            for (int i = 0; i < share.Pages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                int size = share.Pages[i].Size;
                string file = Path.Combine(job.TempFolder!, $"{n}.exr");
                WriteExr(file, composed.Light[i], size);
                files.Add(file);
                if (i < composed.Direction.Count)
                {
                    string dir = Path.Combine(job.TempFolder!, $"{n}-dir.png");
                    WriteDirection(dir, composed.Direction[i], size);
                    directions.Add(dir);
                }
                n++;
            }
            share.ComposedFiles = files;
            share.ComposedDirectionFiles = directions;
        }
    }

    private void FinalizeBake(Job job)
    {
        Scene world = job.World;
        LightmapperResult result = job.Result!;

        EditorAssetBackend? db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null)
        {
            Runtime.Debug.LogError("[Lightmap] Can't save baked data: no project is open.");
            Cleanup();
            return;
        }

        int texels = result.Direct.Length / 3, invalid = 0;
        for (int t = 0; t < texels; t++)
            if (result.Texels[t * S.TexelStride * 4 + 7] == S.TexelInvalid) invalid++;

        // Every scene's new pages go to a staging folder first, so the previous bake stays intact until all of them import.
        var written = new List<(SceneBake Share, string Folder, string Staging, List<Texture2D> Pages, List<Texture2D> Directions)>();
        foreach (SceneBake share in job.Scenes)
        {
            if (!share.Section.IsLoaded || share.Scene.Path is not { } path) continue;
            string folder = LightmapFolderOf(path);
            string staging = folder + "_baking";
            var pages = new List<Texture2D>();
            var directions = new List<Texture2D>();
            if (share.Pages.Count > 0)
            {
                string stagingAbs = Path.Combine(Project.Current.AssetsPath, staging);
                if (Directory.Exists(stagingAbs)) db.DeleteAssetFolder(staging);
                Directory.CreateDirectory(stagingAbs);

                bool directional = share.ComposedDirectionFiles.Count == share.Pages.Count;
                for (int i = 0; i < share.Pages.Count; i++)
                {
                    Texture2D? page = MoveIn(db, share.ComposedFiles[i], staging, $"Lightmap-{i}.exr", direction: false);
                    Texture2D? dir = directional ? MoveIn(db, share.ComposedDirectionFiles[i], staging, $"Lightmap-{i}-dir.png", direction: true) : null;
                    if (page.IsNotValid() || (directional && dir.IsNotValid()))
                    {
                        // Placements address pages by index, so a missing page cannot be skipped.
                        Runtime.Debug.LogError($"[Lightmap] Could not import page {i} of '{staging}', so the bake was not applied.");
                        foreach (var done in written) db.DeleteAssetFolder(done.Staging);
                        db.DeleteAssetFolder(staging);
                        Cleanup();
                        return;
                    }
                    pages.Add(page);
                    if (dir.IsValid()) directions.Add(dir);
                }
            }
            written.Add((share, folder, staging, pages, directions));
        }

        // Every probe baked in one pass against the whole traced scene, then each scene keeps and tetrahedralizes its own.
        int probeStart = 0;
        DropPreview(job.Scenes);
        foreach ((SceneBake share, string folder, string staging, List<Texture2D> pages, List<Texture2D> directions) in written)
        {
            if (Directory.Exists(Path.Combine(Project.Current.AssetsPath, folder))) db.DeleteAssetFolder(folder);
            if (pages.Count > 0 && !db.MoveFolder(staging, folder))
                Runtime.Debug.LogWarning($"[Lightmap] Could not move the new pages to '{folder}', they stay in '{staging}'.");

            SceneLighting lighting = share.Section.Lighting;
            lighting.Lightmaps = pages;
            lighting.Directions = directions;
            lighting.LightmapScale = 1f;   // the pages hold the light as it is

            // Where each baked surface landed, against the object it is on. A rebake replaces the whole set, so a renderer
            // that is no longer baked simply has no placement.
            lighting.Placements = Placements(share);
            lighting.MarkChanged();

            int count = share.Probes.Count;
            var probeSH = new SphericalHarmonicsL2[count];
            Array.Copy(job.ProbeSH, Math.Min(probeStart, job.ProbeSH.Length), probeSH, 0, Math.Min(count, Math.Max(0, job.ProbeSH.Length - probeStart)));
            probeStart += count;
            ProbeTetrahedralizer.Result tet = count > 0 ? ProbeTetrahedralizer.Build(share.Probes) : new ProbeTetrahedralizer.Result { Tetrahedra = [], Neighbours = [] };
            lighting.ProbePositions = [.. share.Probes];
            lighting.ProbeSH = probeSH;
            lighting.ProbeTetrahedra = tet.Tetrahedra;
            lighting.ProbeTetNeighbours = tet.Neighbours;

            EditorSceneManager.MarkDirty(share.Scene);
        }
        world.InvalidateProbeVolume();

        Runtime.Debug.Log($"[Lightmap] Baked {written.Count} scene(s) in {_clock.Elapsed.TotalSeconds:F1}s: {written.Sum(w => w.Pages.Count)} page(s)"
            + (written.Any(w => w.Directions.Count > 0) ? " with direction" : "") + ", "
            + $"{job.Scenes.Sum(share => share.Renderers.Count)} renderer(s), {texels} texel(s) ({invalid} inside geometry, filled from their neighbours), "
            + $"{result.Samples} path(s) each, {job.ProbeSH.Length} probe(s). Save the scenes to keep the result.");
        Cleanup();
        Status = "Done";
    }

    // Moves a composed page into the staging folder with its meta, then imports it. Null when it didn't import.
    private static Texture2D? MoveIn(EditorAssetBackend db, string from, string staging, string name, bool direction)
    {
        string abs = Path.Combine(Project.Current!.AssetsPath, staging, name);
        File.Move(from, abs, overwrite: true);
        WriteLightmapMeta(abs, direction);
        return AssetDatabase.Get<Texture2D>(db.ImportFile($"{staging}/{name}"));
    }

    // Drops the staging folders a bake that failed while writing left behind.
    private static void DeleteStaging(Job job)
    {
        if (job.Overlay != LightmapOverlay.None) return;
        EditorAssetBackend? db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return;
        foreach (SceneBake share in job.Scenes)
        {
            if (share.Scene.Path is not { } path) continue;
            string staging = LightmapFolderOf(path) + "_baking";
            try { if (Directory.Exists(Path.Combine(Project.Current.AssetsPath, staging))) db.DeleteAssetFolder(staging); }
            catch (Exception e) { Runtime.Debug.LogWarning($"[Lightmap] Could not remove '{staging}': {e.Message}"); }
        }
    }

    private void Cleanup()
    {
        if (_job is { } job)
        {
            job.Cancel.Cancel();
            job.Gpu?.Dispose();
            job.Gpu = null;
            DropPreview(job.Scenes);

            // A worker still writing finishes into a folder nobody reads; delete it once it's done
            if (job.TempFolder is { } temp)
            {
                Task pending = job.Compose ?? Task.CompletedTask;
                pending.ContinueWith(_ => { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }, TaskScheduler.Default);
            }
        }
        _job = null;
        Progress = 0f;
        _clock.Stop();
        Remaining = null;
    }

    // ---- scene gathering, main thread ----

    // A static CSG model's geometry only has lightmap UVs once unwrapped, so every one is unwrapped now if it isn't yet.
    private static void PrepareCsgModels(Scene world, Dictionary<SceneSection, SceneBake> bySection)
    {
        foreach (GameObject go in world.Objects)
        {
            if (!go.IsStatic || !go.EnabledInHierarchy || go.Section is not { } section || !bySection.ContainsKey(section)) continue;
            if (go.GetComponent<CSGModel>() is not { } model || model.IsNotValid() || model.GeneratedMesh.IsNotValid()) continue;
            if (model.GenerateLightmapUVs && !model.LightmapUVsStale && model.GeneratedMesh.HasUV2) continue;

            model.GenerateLightmapUVs = true;
            model.RebuildLightmapUVs();
            Runtime.Debug.Log($"[Lightmap] Unwrapped lightmap UVs for the CSG model '{go.Name}'.");
        }
    }

    // UV2, or the primary UVs when they lie in the unit square, the only way they can be a lightmap layout.
    private static Float2[]? LightmapUVs(Mesh mesh)
    {
        if (mesh.HasUV2) return mesh.UV2;
        if (!mesh.HasUV) return null;
        Float2[] uv = mesh.UV;
        const float Slack = 1e-3f;
        foreach (Float2 p in uv)
            if (p.X < -Slack || p.Y < -Slack || p.X > 1f + Slack || p.Y > 1f + Slack) return null;
        return uv;
    }

    /// <summary>
    /// A material as the tracer reads it: diffuse albedo, alpha cutout, emission and their maps. Read through the
    /// material's own getters, so variants and shader defaults resolve as they do when drawing. Maps are only noted
    /// here and decoded on a worker.
    /// </summary>
    private static (int Index, bool Transparent) MaterialOf(Job job, Material? material)
    {
        if (material.IsValid() && job.Materials.TryGetValue(material, out var known)) return known;

        LightmapperInput input = job.Input;
        int index = input.MaterialData.Count / (S.MaterialStride * 4);
        Float4 baseColor = new(0.7f, 0.7f, 0.7f, 1f), emission = default, baseST = new(1, 1, 0, 0), emissionST = new(1, 1, 0, 0);
        float cutoff = -1f;
        bool transparent = false;
        Texture2D? baseMap = null, emissionMap = null;
        int baseUVSet = 0, emissionUVSet = 0;

        if (material.IsValid())
        {
            string surface = material.GetKeyword("_SURFACE");
            transparent = surface == "TRANSPARENT";
            if (material.HasProperty("base_color"))
            {
                Color c = material.GetColor("base_color");
                float weight = material.HasProperty("base_weight") ? material.GetFloat("base_weight") : 1f;
                // Metals reflect almost nothing diffusely; keep some, as their specular still bounces light around
                float metal = material.HasProperty("base_metalness") ? Math.Clamp(material.GetFloat("base_metalness"), 0f, 1f) : 0f;
                float diffuse = weight * (1f - 0.5f * metal);
                float opacity = (float)c.A * (material.HasProperty("geometry_opacity") ? material.GetFloat("geometry_opacity") : 1f);
                baseColor = new Float4((float)c.R * diffuse, (float)c.G * diffuse, (float)c.B * diffuse, opacity);
            }
            if (surface == "CUTOUT")
                cutoff = material.HasProperty("alpha_cutoff") ? material.GetFloat("alpha_cutoff") : 0.5f;
            if (material.HasProperty("emission_color") && material.HasProperty("emission_luminance"))
            {
                Color e = material.GetColor("emission_color");
                float luminance = material.GetFloat("emission_luminance");
                emission = new Float4((float)e.R * luminance, (float)e.G * luminance, (float)e.B * luminance, 0f);
            }
            if (material.HasProperty("base_color_map"))
            {
                baseMap = material.GetTexture("base_color_map");
                baseST = material.GetTextureScaleOffset("base_color_map");
                baseUVSet = material.GetTextureUVSet("base_color_map");
            }
            if (material.HasProperty("emission_color_map") && emission.X + emission.Y + emission.Z > 0f)
            {
                emissionMap = material.GetTexture("emission_color_map");
                emissionST = material.GetTextureScaleOffset("emission_color_map");
                emissionUVSet = material.GetTextureUVSet("emission_color_map");
            }
        }

        input.MaterialData.AddRange([baseColor.X, baseColor.Y, baseColor.Z, cutoff,
            emission.X, emission.Y, emission.Z, baseColor.W,
            baseST.X, baseST.Y, baseST.Z, baseST.W,
            emissionST.X, emissionST.Y, emissionST.Z, emissionST.W]);
        job.MapSlots.Add((MapSourceOf(job, baseMap), baseUVSet));
        job.MapSlots.Add((MapSourceOf(job, emissionMap), emissionUVSet));

        var entry = (index, transparent);
        if (material.IsValid()) job.Materials[material] = entry;
        return entry;
    }

    // Where a map's texels will come from: its file, decoded later on a worker, or its GPU pixels, read now. -1 for none.
    private static int MapSourceOf(Job job, Texture2D? texture)
    {
        if (texture.IsNotValid()) return -1;
        if (job.MapIndex.TryGetValue(texture, out int known)) return known;

        var source = new MapSource { Srgb = texture.Srgb };
        string? rel = texture.AssetPath;
        string? abs = !string.IsNullOrEmpty(rel) && !rel.Contains('#') && Project.Current != null ? Path.Combine(Project.Current.AssetsPath, rel) : null;
        if (abs != null && File.Exists(abs)) source.File = abs;
        else
        {
            // Embedded and generated textures have no file, only their GPU pixels
            try
            {
                if (texture.GetMipRgba8() is byte[] rgba) (source.Pixels, source.Width, source.Height) = (rgba, (int)texture.Width, (int)texture.Height);
            }
            catch { }
        }

        int index = job.MapSources.Count;
        job.MapSources.Add(source);
        job.MapIndex[texture] = index;
        return index;
    }

    // ---- preparing, worker thread ----

    private static LightmapperScene PrepareScene(Job job, CancellationToken token)
    {
        LightmapperInput input = job.Input;

        foreach (MeshSource source in job.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (AddMesh(input, source) is { } chart)
            {
                source.Share.Charts.Add(chart);
                source.Share.Renderers.Add(source.Renderer);
                source.Share.Names.Add(source.Name);
            }
        }
        if (input.TriangleCount == 0)
            throw new InvalidOperationException("Nothing to trace: every static surface is transparent or casts no shadows.");

        // Maps decode in parallel, then go into the pool in order
        var decoded = new (byte[] Pixels, int Width, int Height)?[job.MapSources.Count];
        Parallel.For(0, decoded.Length, new ParallelOptions { CancellationToken = token }, i => decoded[i] = Decode(job.MapSources[i]));
        var placed = new (uint Offset, uint Width, uint Height, bool Srgb)?[decoded.Length];
        for (int i = 0; i < decoded.Length; i++)
        {
            if (decoded[i] is not { } img) continue;
            uint offset = (uint)input.Pool.Count;
            for (int p = 0; p < img.Width * img.Height; p++)
                input.Pool.Add(img.Pixels[p * 4] | ((uint)img.Pixels[p * 4 + 1] << 8) | ((uint)img.Pixels[p * 4 + 2] << 16) | ((uint)img.Pixels[p * 4 + 3] << 24));
            placed[i] = (offset, (uint)img.Width, (uint)img.Height, job.MapSources[i].Srgb);
        }
        foreach ((int sourceIndex, int uvSet) in job.MapSlots)
        {
            if (sourceIndex >= 0 && placed[sourceIndex] is { } map)
            {
                uint flags = (map.Srgb ? S.MapSrgb : 0u) | (uvSet == 1 ? S.MapSecondUV : 0u);
                input.Maps.AddRange([map.Offset, map.Width, map.Height, flags]);
            }
            else input.Maps.AddRange([0u, 0u, 0u, 0u]);
        }

        // Each scene packs into pages of its own, and every page's texels are traced together against the whole world
        int size = Math.Max(16, job.Settings.AtlasSize);
        bool seams = job.Settings.StitchSeams || job.Overlay == LightmapOverlay.Seams;
        foreach (SceneBake share in job.Scenes)
        {
            if (share.Charts.Count == 0) continue;
            int pages = LightmapAtlas.Pack(share.Charts, size, job.Settings.TexelsPerUnit, Padding);
            var overlapping = new Dictionary<int, int>();   // chart, texels it overlaps on
            for (int p = 0; p < pages; p++)
            {
                token.ThrowIfCancellationRequested();
                var grid = new TexelGrid(size);
                for (int c = 0; c < share.Charts.Count; c++)
                    if (share.Charts[c].Page == p) LightmapAtlas.Rasterize(share.Charts[c], c, grid);

                var pixels = new List<int>();
                var charts = new List<int>();
                var overlap = new List<int>();
                int first = input.TexelCount;
                for (int i = 0; i < grid.Covered.Length; i++)
                {
                    if (!grid.Covered[i]) continue;
                    pixels.Add(i);
                    charts.Add(grid.Chart[i]);
                    if (grid.Overlap[i])
                    {
                        overlap.Add(i);
                        overlapping[grid.Chart[i]] = overlapping.GetValueOrDefault(grid.Chart[i]) + 1;
                    }
                    Float3 pos = grid.Position[i], n = grid.Normal[i], dx = grid.StepX[i], dy = grid.StepY[i];
                    float radius = 0.5f * MathF.Max(Float3.Length(dx), Float3.Length(dy));
                    input.Texels.AddRange([pos.X, pos.Y, pos.Z, radius, n.X, n.Y, n.Z, S.TexelClean,
                        dx.X, dx.Y, dx.Z, 0f, dy.X, dy.Y, dy.Z, 0f]);
                }
                share.Pages.Add(new LightmapPage(size, first, [.. pixels], [.. charts], [.. overlap]));
            }
            if (seams) share.Seams = LightmapSeams.Find(share.Charts, size);

            if (overlapping.Count > 0)
            {
                IEnumerable<string> worst = overlapping.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{share.Names[kv.Key]} ({kv.Value})");
                job.PrepareReport.Add($"Lightmap UVs overlap on {overlapping.Values.Sum()} texel(s) of {overlapping.Count} renderer(s) in '{share.Scene.Name}', "
                    + $"so those surfaces share light: {string.Join(", ", worst)}{(overlapping.Count > 10 ? ", ..." : "")}. The UV Overlap overlay shows where.");
            }
        }

        token.ThrowIfCancellationRequested();
        return LightmapperScene.Build(input);
    }

    // A map's texels at most MaxMapSize on a side, bottom row first as every texture. Null when unreadable.
    private static (byte[], int, int)? Decode(MapSource source)
    {
        byte[]? pixels = source.Pixels;
        int w = source.Width, h = source.Height;
        if (pixels == null && source.File != null)
        {
            try
            {
                using Aperture.Image img = Aperture.Image.Load(source.File, Texture2D.DecodeToUploadLayout);
                pixels = new byte[img.Width * img.Height * 4];
                img.RootFrame.CopyTo(pixels, img.Width * 4);
                (w, h) = (img.Width, img.Height);
            }
            catch { return null; }
        }
        if (pixels == null) return null;
        while (w > MaxMapSize || h > MaxMapSize) (pixels, w, h) = Halve(pixels, w, h);
        return (pixels, w, h);
    }

    /// <summary>
    /// Adds a mesh's triangles in world space to the traced scene when it occludes, and returns its chart when it has
    /// chart UVs. Transparent submeshes let light through, so they aren't traced.
    /// </summary>
    private static LightmapChart? AddMesh(LightmapperInput input, MeshSource mesh)
    {
        Float3[] vertices = mesh.Vertices;
        Float3[]? meshNormals = mesh.Normals;
        Float2[]? uv0 = mesh.UV0, uv1 = mesh.UV1, chartUVs = mesh.ChartUVs;
        uint[] indices = mesh.Indices;
        Float4x4 transform = mesh.Transform;

        // Normals go through the inverse transpose, so a scaled object still lights by its real facing
        var upper = new Float3x3(transform);
        Float3x3 normalMatrix = Float3x3.Invert(upper, out Float3x3 inverse) ? Float3x3.Transpose(inverse) : upper;
        var world = new Float3[vertices.Length];
        var worldNormals = new Float3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            world[i] = Float4x4.TransformPoint(vertices[i], transform);
            if (meshNormals != null && i < meshNormals.Length)
                worldNormals[i] = Float3.NormalizeSafe(normalMatrix * meshNormals[i], Float3.UnitY);
        }

        var chartPositions = new List<Float3>();
        var chartNormals = new List<Float3>();
        var chartUVList = new List<Float2>();
        for (int s = 0; s < mesh.SubMeshes.Length; s++)
        {
            SubMeshDescriptor sub = mesh.SubMeshes[s];
            (int materialIndex, bool transparent) = mesh.Materials[s];
            bool traced = mesh.Occludes && !transparent;

            for (int k = sub.IndexStart; k + 2 < sub.IndexStart + sub.IndexCount && k + 2 < indices.Length; k += 3)
            {
                int i0 = (int)indices[k], i1 = (int)indices[k + 1], i2 = (int)indices[k + 2];
                if (i0 >= world.Length || i1 >= world.Length || i2 >= world.Length) continue;
                Float3 a = world[i0], b = world[i1], c = world[i2];
                // Meshes without normals light by their face
                Float3 face = Float3.NormalizeSafe(Float3.Cross(b - a, c - a), Float3.UnitY);
                Float3 na = meshNormals != null ? worldNormals[i0] : face;
                Float3 nb = meshNormals != null ? worldNormals[i1] : face;
                Float3 nc = meshNormals != null ? worldNormals[i2] : face;

                if (traced)
                {
                    input.Positions.AddRange([a, b, c]);
                    input.Normals.AddRange([na, nb, nc]);
                    foreach (int v in (ReadOnlySpan<int>)[i0, i1, i2])
                    {
                        Float2 t0 = uv0 != null && v < uv0.Length ? uv0[v] : default;
                        Float2 t1 = uv1 != null && v < uv1.Length ? uv1[v] : t0;
                        input.UVs.Add(new Float4(t0.X, t0.Y, t1.X, t1.Y));
                    }
                    input.Materials.Add(materialIndex);
                }

                if (chartUVs == null || i0 >= chartUVs.Length || i1 >= chartUVs.Length || i2 >= chartUVs.Length) continue;
                chartPositions.AddRange([a, b, c]);
                chartNormals.AddRange([na, nb, nc]);
                chartUVList.AddRange([chartUVs[i0], chartUVs[i1], chartUVs[i2]]);
            }
        }

        if (chartPositions.Count == 0) return null;
        return new LightmapChart { Positions = [.. chartPositions], Normals = [.. chartNormals], UVs = [.. chartUVList] };
    }

    // Half the size on each side, each texel the average of the four it covers.
    private static (byte[], int, int) Halve(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                for (int c = 0; c < 4; c++)
                    dst[(y * nw + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c] + 2) / 4);
            }
        }
        return (dst, nw, nh);
    }

    // The scene's ambient arriving from each direction, latitude by longitude with +Y up, as the shader looks it up.
    private static float[] SkyTable(Scene scene)
    {
        var table = new float[S.SkyWidth * S.SkyHeight * 4];
        for (int y = 0; y < S.SkyHeight; y++)
        {
            float theta = (y + 0.5f) / S.SkyHeight * MathF.PI;
            for (int x = 0; x < S.SkyWidth; x++)
            {
                float phi = ((x + 0.5f) / S.SkyWidth - 0.5f) * 2f * MathF.PI;
                var dir = new Float3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                Float3 c = scene.Rendering.Ambient(dir);
                int i = (y * S.SkyWidth + x) * 4;
                table[i] = c.X; table[i + 1] = c.Y; table[i + 2] = c.Z; table[i + 3] = 1f;
            }
        }
        return table;
    }

    /// <summary>
    /// The colour a light bakes with, from lux for a directional light and candela for the rest (an area light's
    /// straight ahead). The tracer lights a white surface with it times the cosine, which is what Graphite reads from a
    /// lightmap: irradiance over pi.
    /// </summary>
    internal static Float3 BakeColor(Light light)
    {
        Color c = light.FinalColor;
        float intensity = light is LocalLight local ? local.AxisCandela : light.Intensity;
        return new Float3((float)c.R, (float)c.G, (float)c.B) * (intensity / Maths.PI);
    }

    /// <summary>Appends a light as <see cref="LightmapperShader"/> reads it, <see cref="LightmapperShader.LightStride"/> float4.</summary>
    internal static void AddLight(List<float> into, Light light)
    {
        Float3 color = BakeColor(light);
        Float4x4 xform = light.Transform.LocalToWorldMatrix;
        Float3 position = new(xform.c3.X, xform.c3.Y, xform.c3.Z);
        Float3 right = Float3.NormalizeSafe(new Float3(xform.c0.X, xform.c0.Y, xform.c0.Z), Float3.UnitX);
        Float3 up = Float3.NormalizeSafe(new Float3(xform.c1.X, xform.c1.Y, xform.c1.Z), Float3.UnitY);
        Float3 forward = Float3.NormalizeSafe(new Float3(xform.c2.X, xform.c2.Y, xform.c2.Z), Float3.UnitZ);
        int flags = (light.BakeMode == LightBakeMode.Baked ? S.LightBakesDirect : 0)   // Mixed bakes its bounce only
            | (light.CastShadows ? S.LightCastsShadows : 0);

        int type;
        float range = 0f, cosOuter = -1f, cosInner = -1f, radius = 0f, surfaceOverProjected = 1f;
        Float3 axisX = default, axisY = default;
        switch (light)
        {
            case DirectionalLight sun:
                type = S.LightDirectional;
                radius = MathF.Cos(Math.Clamp(sun.AngularRadius, 0f, 45f) * Maths.Deg2Rad);
                break;
            case SpotLight spot:
                {
                    // Graphite's cone: the half angle, softened inward by the spot's softness
                    float outer = Maths.Clamp(spot.SpotAngle, 1f, 179f) * 0.5f * Maths.Deg2Rad;
                    float inner = outer * (1f - Maths.Clamp(spot.SpotSoftness, 0f, 1f));
                    type = S.LightSpot;
                    range = spot.Range;
                    cosOuter = MathF.Cos(outer);
                    cosInner = MathF.Cos(inner);
                    radius = MathF.Max(spot.SourceRadius, 0f);
                    break;
                }
            case PunctualLight point:
                type = S.LightPoint;
                range = point.Range;
                radius = MathF.Max(point.SourceRadius, 0f);
                break;
            case RectLight rect:
                type = S.LightRect;
                range = rect.Range;
                axisX = right * (MathF.Max(rect.Width, 0f) * 0.5f);
                axisY = up * (MathF.Max(rect.Height, 0f) * 0.5f);
                break;
            case DiskLight disk:
                type = S.LightDisk;
                range = disk.Range;
                axisX = right * (MathF.Max(disk.Diameter, 0f) * 0.5f);
                axisY = up * (MathF.Max(disk.Diameter, 0f) * 0.5f);
                break;
            case TubeLight tube:
                {
                    type = S.LightTube;
                    range = tube.Range;
                    radius = MathF.Max(tube.Radius, 0f);
                    axisX = forward * (MathF.Max(tube.Length, 0f) * 0.5f);
                    // Its axis candela is its luminance times the area seen side on; the samples cover all of it
                    float projected = tube.Intensity > 0f ? tube.AxisCandela / tube.Intensity : 0f;
                    surfaceOverProjected = projected > 0f ? tube.SurfaceArea / projected : 0f;
                    break;
                }
            default:
                return;
        }
        into.AddRange([position.X, position.Y, position.Z, type,
            forward.X, forward.Y, forward.Z, range,
            color.X, color.Y, color.Z, flags,
            cosOuter, cosInner, radius, surfaceOverProjected,
            axisX.X, axisX.Y, axisX.Z, 0f,
            axisY.X, axisY.Y, axisY.Z, 0f]);
    }

    // ---- files ----

    /// <summary>Fills empty pixels from filled neighbours, three floats a pixel. See <see cref="LightmapComposer.Dilate"/>.</summary>
    internal static void Dilate(float[] rgb, bool[] covered, int size, int passes, bool[]? within = null)
        => LightmapComposer.Dilate(rgb, 3, covered, size, passes, within);

    /// <summary>
    /// Writes a page as a half float RGBA EXR, losslessly compressed. Rows go bottom first, since the page is row 0 at v 0
    /// and the importer flips on the way in, so the two cancel and the sampled v matches the placement.
    /// </summary>
    internal static void WriteExr(string absolutePath, float[] rgb, int size)
    {
        using Aperture.Image img = Aperture.Image.FromPixels(LightmapComposer.LightHalves(rgb, size), size, size, PixelFormat.RgbaF16);
        img.Save(absolutePath, new EncodeOptions { Format = ImageFormat.Exr, FlipVertically = true });
    }

    /// <summary>Writes a direction page as an RGBA8 PNG, flipped like <see cref="WriteExr"/>.</summary>
    internal static void WriteDirection(string absolutePath, float[] direction, int size)
    {
        using Aperture.Image img = Aperture.Image.FromPixels(LightmapComposer.DirectionBytes(direction, size), size, size, PixelFormat.Rgba8);
        img.Save(absolutePath, new EncodeOptions { Format = ImageFormat.Png, FlipVertically = true });
    }

    // A page imports as a Lightmap texture: a light page kept as the half floats it was baked as, a direction page as
    // linear 8 bit without mips, since it holds vectors.
    private static void WriteLightmapMeta(string pageAbs, bool direction)
    {
        EchoObject settings = new TextureImporter().DefaultSettings() ?? EchoObject.NewCompound();
        settings[TextureImportSettings.Keys.Type] = new EchoObject((int)TextureImportType.Lightmap);
        if (direction)
        {
            settings[TextureImportSettings.Keys.Srgb] = new EchoObject(false);
            settings[TextureImportSettings.Keys.Mipmaps] = new EchoObject(false);
        }
        MetaFile.Write(MetaFile.GetMetaPath(pageAbs), MetaFile.CreateNew(nameof(TextureImporter), defaultSettings: settings));
    }
}
