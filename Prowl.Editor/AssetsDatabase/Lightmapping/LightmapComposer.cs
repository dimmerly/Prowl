// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Prowl.Vector;

using S = Prowl.Editor.Lightmapping.LightmapperShader;

namespace Prowl.Editor.Lightmapping;

/// <summary>One atlas page: the pixel and chart of each of its texels, from FirstTexel on in the traced texel list.</summary>
internal sealed class LightmapPage(int size, int firstTexel, int[] pixels, int[] charts, int[] overlap)
{
    public readonly int Size = size;
    public readonly int FirstTexel = firstTexel;
    public readonly int[] Pixels = pixels;
    public readonly int[] Charts = charts;

    /// <summary>Pixels where lightmap UVs overlap, see <see cref="TexelGrid.Overlap"/>.</summary>
    public readonly int[] Overlap = overlap;
}

/// <summary>What the bake shows on its pages in place of light, to find what's wrong with a scene's lightmapping.</summary>
public enum LightmapOverlay
{
    None,
    /// <summary>Each chart in a colour of its own: how surfaces landed in the atlas.</summary>
    Charts,
    /// <summary>Green where texels trace as they are, yellow where they had to be pushed out of geometry, red where buried.</summary>
    InvalidTexels,
    /// <summary>Red where lightmap UVs overlap, so two surfaces share texels.</summary>
    UVOverlap,
    /// <summary>Cyan along UV seams, the texels stitching makes agree.</summary>
    Seams,
}

/// <summary>One scene's pages composed: light, three floats a pixel, and direction, four (vector, half Lambert at the baked normal).</summary>
internal sealed class ComposedScene
{
    public readonly List<float[]> Light = [];
    public readonly List<float[]> Direction = [];   // empty for a bake without direction pages
}

/// <summary>Turns the traced texels into page images on a worker thread: denoise, fill, stitch, and the debug overlays.</summary>
internal static class LightmapComposer
{
    /// <summary>
    /// Every page of one scene: direct plus denoised indirect light at each traced texel, texels buried in geometry
    /// filled from their chart's neighbours, grown outward so bilinear taps at chart edges find light, then stitched
    /// across <paramref name="seams"/>. With <paramref name="directional"/> the direction pages are made the same way.
    /// </summary>
    public static ComposedScene Compose(IReadOnlyList<LightmapPage> pages, LightmapperResult result, int denoisePasses, int dilatePixels,
        bool directional, IReadOnlyList<SeamEdge>? seams, CancellationToken token = default)
    {
        var scene = new ComposedScene();
        foreach (LightmapPage page in pages)
        {
            token.ThrowIfCancellationRequested();
            (float[] light, float[]? direction) = Page(page, result, denoisePasses, dilatePixels, directional);
            scene.Light.Add(light);
            if (direction != null) scene.Direction.Add(direction);
        }
        if (seams is { Count: > 0 } && pages.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            int size = pages[0].Size;
            LightmapSeams.Stitch(scene.Light, 3, size, seams);
            if (scene.Direction.Count == scene.Light.Count) LightmapSeams.Stitch(scene.Direction, 4, size, seams);
        }
        return scene;
    }

    private static (float[] Light, float[]? Direction) Page(LightmapPage page, LightmapperResult result, int denoisePasses, int dilatePixels, bool directional)
    {
        int size = page.Size, n = size * size;
        var guide = new DenoiseGuide(size);
        var direct = new float[n * 3];
        var indirect = new float[n * 3];
        var variance = new float[n];
        var rasterized = new bool[n];
        float[]? toward = directional ? new float[n * 3] : null;
        int stride = S.TexelStride * 4;
        float samples = Math.Max(1, result.Samples);

        for (int k = 0; k < page.Pixels.Length; k++)
        {
            int px = page.Pixels[k], t = page.FirstTexel + k;
            rasterized[px] = true;
            ReadOnlySpan<float> texel = result.Texels.AsSpan(t * stride, stride);
            if (texel[7] == S.TexelInvalid) continue;
            guide.Valid[px] = true;
            guide.Position[px] = new Float3(texel[0], texel[1], texel[2]);
            guide.Radius[px] = texel[3];
            guide.Normal[px] = new Float3(texel[4], texel[5], texel[6]);
            guide.Chart[px] = page.Charts[k];
            for (int c = 0; c < 3; c++)
            {
                direct[px * 3 + c] = result.Direct[t * 3 + c];
                indirect[px * 3 + c] = result.Indirect[t * 3 + c];
                if (toward != null && result.Direction.Length > t * 3 + c) toward[px * 3 + c] = result.Direction[t * 3 + c];
            }
            variance[px] = result.Variance[t] / samples;   // the noise of the mean, not of one path
        }

        int passes = Math.Clamp(denoisePasses, 0, 8);
        if (passes > 0) LightmapDenoiser.Run(indirect, variance, guide, passes);

        var light = new float[n * 3];
        for (int i = 0; i < n * 3; i++) light[i] = direct[i] + indirect[i];
        Fill(light, 3, guide.Valid, rasterized, size, dilatePixels);

        float[]? direction = null;
        if (toward != null)
        {
            // Directions are noisy too, and change slowly across a surface; the geometry alone guides their smoothing
            if (passes > 0) LightmapDenoiser.Run(toward, null, guide, Math.Min(passes, 3));
            direction = new float[n * 4];
            for (int px = 0; px < n; px++)
            {
                if (!guide.Valid[px]) continue;
                Float3 d = new(toward[px * 3], toward[px * 3 + 1], toward[px * 3 + 2]);
                float length = Float3.Length(d);
                if (length > 1f) d /= length;   // smoothing never lengthens it, rounding might
                direction[px * 4] = d.X;
                direction[px * 4 + 1] = d.Y;
                direction[px * 4 + 2] = d.Z;
                // The half Lambert term at the normal the light was baked for, so that normal gets the light as baked
                direction[px * 4 + 3] = Math.Clamp(Float3.Dot(guide.Normal[px], d), -1f, 1f) * 0.5f + 0.5f;
            }
            // Where nothing was traced, no direction: every normal gets the light as is
            for (int px = 0; px < n; px++)
                if (!guide.Valid[px]) direction[px * 4 + 3] = 0.5f;
            Fill(direction, 4, guide.Valid, rasterized, size, dilatePixels);
        }
        return (light, direction);
    }

    // Buried texels first, only from texels on the page's charts, until none is left or none can be reached, then outward
    private static void Fill(float[] data, int channels, bool[] valid, bool[] rasterized, int size, int dilatePixels)
    {
        var covered = (bool[])valid.Clone();
        Dilate(data, channels, covered, size, 64, rasterized);
        Dilate(data, channels, covered, size, Math.Max(1, dilatePixels));
    }

    /// <summary>
    /// Fills each empty pixel next to a filled one with the average of its filled neighbours, up to
    /// <paramref name="passes"/> times, only where <paramref name="within"/> allows when given. Stops early once a pass
    /// fills nothing.
    /// </summary>
    public static void Dilate(float[] data, int channels, bool[] covered, int size, int passes, bool[]? within = null)
    {
        var snapshot = new float[data.Length];
        var was = new bool[covered.Length];
        for (int p = 0; p < passes; p++)
        {
            Array.Copy(data, snapshot, data.Length);
            Array.Copy(covered, was, covered.Length);
            int filled = 0;
            Parallel.For(0, size, () => 0, (y, _, count) =>
            {
                Span<float> sum = stackalloc float[channels];
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (was[i] || (within != null && !within[i])) continue;
                    int n = 0;
                    sum.Clear();
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                            int j = ny * size + nx;
                            if (!was[j]) continue;
                            for (int c = 0; c < channels; c++) sum[c] += snapshot[j * channels + c];
                            n++;
                        }
                    }
                    if (n == 0) continue;
                    for (int c = 0; c < channels; c++) data[i * channels + c] = sum[c] / n;
                    covered[i] = true;
                    count++;
                }
                return count;
            }, count => Interlocked.Add(ref filled, count));
            if (filled == 0) break;
        }
    }

    // ---- encoding ----

    /// <summary>A light page as half float RGBA, alpha 1, row 0 first.</summary>
    public static byte[] LightHalves(float[] rgb, int size)
    {
        int n = size * size;
        var halves = new byte[n * 8];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                float v = c < 3 ? rgb[i * 3 + c] : 1f;
                v = float.IsFinite(v) ? Math.Clamp(v, 0f, 65504f) : 0f;
                ushort bits = BitConverter.HalfToUInt16Bits((Half)v);
                halves[i * 8 + c * 2] = (byte)bits;
                halves[i * 8 + c * 2 + 1] = (byte)(bits >> 8);
            }
        }
        return halves;
    }

    /// <summary>A direction page as linear RGBA8 the way Graphite reads it: the vector as v * 0.5 + 0.5, then the half Lambert term.</summary>
    public static byte[] DirectionBytes(float[] direction, int size)
    {
        int n = size * size;
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                float v = direction[i * 4 + c];
                if (!float.IsFinite(v)) v = c < 3 ? 0f : 0.5f;
                float unit = c < 3 ? v * 0.5f + 0.5f : v;
                bytes[i * 4 + c] = (byte)Math.Clamp((int)MathF.Round(unit * 255f), 0, 255);
            }
        }
        return bytes;
    }

    // ---- overlays ----

    private static readonly Float3 s_empty = new(0.02f, 0.02f, 0.03f);

    /// <summary>
    /// The pages of one scene showing <paramref name="overlay"/> instead of light, three floats a pixel, for the Lightmap
    /// Texels view. <paramref name="sceneIndex"/> only varies the chart colours between scenes.
    /// </summary>
    public static List<float[]> Overlay(LightmapOverlay overlay, IReadOnlyList<LightmapPage> pages, float[] texels,
        IReadOnlyList<SeamEdge> seams, int sceneIndex)
    {
        var images = new List<float[]>();
        int stride = S.TexelStride * 4;
        var seamPixels = new HashSet<(int, int)>();
        if (overlay == LightmapOverlay.Seams && pages.Count > 0)
            foreach ((int page, int pixel) in LightmapSeams.Footprint(seams, pages[0].Size)) seamPixels.Add((page, pixel));

        for (int p = 0; p < pages.Count; p++)
        {
            LightmapPage page = pages[p];
            int n = page.Size * page.Size;
            var rgb = new float[n * 3];
            for (int i = 0; i < n; i++) Put(rgb, i, s_empty);
            var overlap = new HashSet<int>(page.Overlap);

            for (int k = 0; k < page.Pixels.Length; k++)
            {
                int px = page.Pixels[k], t = page.FirstTexel + k;
                float state = texels.Length >= (t + 1) * stride ? texels[t * stride + 7] : S.TexelClean;
                Float3 c = overlay switch
                {
                    LightmapOverlay.Charts => ChartColor(sceneIndex, page.Charts[k]),
                    LightmapOverlay.InvalidTexels => state == S.TexelInvalid ? new Float3(1f, 0.1f, 0.1f)
                        : state == S.TexelPushed ? new Float3(1f, 0.85f, 0.1f) : new Float3(0.15f, 0.7f, 0.25f),
                    LightmapOverlay.UVOverlap => overlap.Contains(px) ? new Float3(1f, 0.1f, 0.1f) : new Float3(0.35f),
                    LightmapOverlay.Seams => new Float3(0.35f),
                    _ => new Float3(0.35f),
                };
                Put(rgb, px, c);
            }
            if (overlay == LightmapOverlay.Seams)
                foreach ((int seamPage, int pixel) in seamPixels)
                    if (seamPage == p) Put(rgb, pixel, new Float3(0.1f, 0.9f, 1f));
            images.Add(rgb);
        }
        return images;
    }

    private static void Put(float[] rgb, int i, Float3 c)
    {
        rgb[i * 3] = c.X; rgb[i * 3 + 1] = c.Y; rgb[i * 3 + 2] = c.Z;
    }

    // A bright colour of its own for each chart, from a hash so neighbours rarely match
    private static Float3 ChartColor(int scene, int chart)
    {
        uint h = (uint)(chart * 73856093) ^ (uint)(scene * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        float hue = (h & 0xFFFF) / 65535f;
        float r = Math.Clamp(MathF.Abs(hue * 6f - 3f) - 1f, 0f, 1f);
        float g = Math.Clamp(2f - MathF.Abs(hue * 6f - 2f), 0f, 1f);
        float b = Math.Clamp(2f - MathF.Abs(hue * 6f - 4f), 0f, 1f);
        return new Float3(r, g, b) * 0.75f + new Float3(0.15f);
    }
}
