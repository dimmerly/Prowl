// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

namespace Prowl.Editor.Lightmapping;

/// <summary>
/// An edge two triangles share in the world but not in the lightmap: the same line on the surface lands in two places
/// in the atlas, in page pixels (UV times the page size). Each end of A matches the same end of B.
/// </summary>
internal readonly record struct SeamEdge(int PageA, Float2 A0, Float2 A1, int PageB, Float2 B0, Float2 B1);

/// <summary>
/// Finds UV seams and makes the light agree across them. Each side of a seam is lit from its own texels, so bilinear
/// filtering on either side gives a slightly different value and a line shows along the edge. Stitching samples the edge
/// on both sides and solves, in the least squares sense, for the smallest change to the texels under those samples that
/// makes both sides read the same, as in "Seamless" lightmap stitching (Sloan, Liu): each texel is pulled toward what it
/// was and every pair of samples toward each other.
/// </summary>
internal static class LightmapSeams
{
    // Positions this close are the same corner. Meshes that should be welded are well within it, distinct ones well outside
    private const float WeldDistance = 1e-4f;
    // Corner normals at least this close are one smooth surface; a hard edge between faces isn't a seam to hide
    private const float SmoothNormal = 0.9f;
    private const float SamePixel = 0.01f;

    private readonly struct EdgeRef(Float3 n0, Float3 n1, Float2 u0, Float2 u1, int page)
    {
        public readonly Float3 N0 = n0, N1 = n1;
        public readonly Float2 U0 = u0, U1 = u1;
        public readonly int Page = page;
    }

    /// <summary>The seams between and within the charts of one scene's pages, every page <paramref name="pageSize"/> square.</summary>
    public static List<SeamEdge> Find(IReadOnlyList<LightmapChart> charts, int pageSize)
    {
        var edges = new Dictionary<(long, long, long, long, long, long), List<EdgeRef>>();
        foreach (LightmapChart chart in charts)
        {
            Float4 so = chart.ScaleOffset(pageSize);
            for (int t = 0; t + 2 < chart.Positions.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = t + k, j = t + (k + 1) % 3;
                    var qi = Quantize(chart.Positions[i]);
                    var qj = Quantize(chart.Positions[j]);
                    if (qi == qj) continue;
                    Float2 ui = Pixels(chart.UVs[i], so, pageSize), uj = Pixels(chart.UVs[j], so, pageSize);
                    Float3 ni = chart.Normals[i], nj = chart.Normals[j];
                    // One way round for both triangles, so their ends line up
                    if (qj.CompareTo(qi) < 0) { (qi, qj) = (qj, qi); (ui, uj) = (uj, ui); (ni, nj) = (nj, ni); }
                    var key = (qi.Item1, qi.Item2, qi.Item3, qj.Item1, qj.Item2, qj.Item3);
                    if (!edges.TryGetValue(key, out List<EdgeRef>? list)) edges[key] = list = new List<EdgeRef>(2);
                    list.Add(new EdgeRef(ni, nj, ui, uj, chart.Page));
                }
            }
        }

        var seams = new List<SeamEdge>();
        foreach (List<EdgeRef> list in edges.Values)
        {
            // Two triangles to an edge is a manifold surface; more pairs the first with each other
            for (int b = 1; b < list.Count; b++)
            {
                EdgeRef ea = list[0], eb = list[b];
                if (ea.Page == eb.Page && Near(ea.U0, eb.U0) && Near(ea.U1, eb.U1)) continue;   // shared in UV too
                if (Float3.Dot(ea.N0, eb.N0) < SmoothNormal || Float3.Dot(ea.N1, eb.N1) < SmoothNormal) continue;
                if (Float2.Length(ea.U1 - ea.U0) < SamePixel || Float2.Length(eb.U1 - eb.U0) < SamePixel) continue;
                seams.Add(new SeamEdge(ea.Page, ea.U0, ea.U1, eb.Page, eb.U0, eb.U1));
            }
        }
        return seams;
    }

    /// <summary>
    /// Changes the pages' texels as little as it can so both sides of every seam read the same when sampled bilinearly.
    /// <paramref name="pages"/> holds <paramref name="channels"/> floats a pixel, each page <paramref name="size"/> square.
    /// </summary>
    public static void Stitch(IReadOnlyList<float[]> pages, int channels, int size, IReadOnlyList<SeamEdge> seams, float weight = 8f, int iterations = 100)
    {
        if (seams.Count == 0 || pages.Count == 0) return;

        // Every texel under a seam sample becomes an unknown; each sample is a row: side A's taps minus side B's
        var unknowns = new Dictionary<long, int>();
        var keys = new List<long>();
        var rows = new List<(int Var, float Weight)[]>();
        foreach (SeamEdge seam in seams)
        {
            if (seam.PageA >= pages.Count || seam.PageB >= pages.Count) continue;
            foreach ((Float2 a, Float2 b) in Samples(seam))
            {
                var row = new (int, float)[8];
                int n = 0;
                foreach ((int pixel, float w) in Bilinear(a, size)) row[n++] = (Unknown(seam.PageA, pixel), w);
                foreach ((int pixel, float w) in Bilinear(b, size)) row[n++] = (Unknown(seam.PageB, pixel), -w);
                rows.Add(row);
            }
        }
        int count = keys.Count;
        if (count == 0) return;

        int Unknown(int page, int pixel)
        {
            long key = (long)page * size * size + pixel;
            if (!unknowns.TryGetValue(key, out int index))
            {
                unknowns[key] = index = keys.Count;
                keys.Add(key);
            }
            return index;
        }

        // (I + weight * sum of row row^T) x = original, by conjugate gradients, one channel at a time
        void Apply(float[] v, float[] into)
        {
            Array.Copy(v, into, count);
            foreach ((int Var, float Weight)[] row in rows)
            {
                float dot = 0f;
                foreach ((int var, float w) in row) dot += w * v[var];
                dot *= weight;
                foreach ((int var, float w) in row) into[var] += w * dot;
            }
        }

        var x = new float[count];
        var r = new float[count];
        var p = new float[count];
        var ap = new float[count];
        for (int c = 0; c < channels; c++)
        {
            var original = new float[count];
            for (int k = 0; k < count; k++)
            {
                (int page, int pixel) = Split(keys[k]);
                original[k] = pages[page][pixel * channels + c];
            }
            Array.Copy(original, x, count);
            Apply(x, ap);
            float rr = 0f, bb = 0f;
            for (int k = 0; k < count; k++)
            {
                r[k] = original[k] - ap[k];
                p[k] = r[k];
                rr += r[k] * r[k];
                bb += original[k] * original[k];
            }

            for (int it = 0; it < iterations && rr > 1e-12f * MathF.Max(bb, 1e-12f); it++)
            {
                Apply(p, ap);
                float pAp = 0f;
                for (int k = 0; k < count; k++) pAp += p[k] * ap[k];
                if (pAp <= 0f) break;
                float alpha = rr / pAp, next = 0f;
                for (int k = 0; k < count; k++)
                {
                    x[k] += alpha * p[k];
                    r[k] -= alpha * ap[k];
                    next += r[k] * r[k];
                }
                float beta = next / rr;
                rr = next;
                for (int k = 0; k < count; k++) p[k] = r[k] + beta * p[k];
            }

            for (int k = 0; k < count; k++)
            {
                (int page, int pixel) = Split(keys[k]);
                pages[page][pixel * channels + c] = x[k];
            }
        }

        (int, int) Split(long key) => ((int)(key / ((long)size * size)), (int)(key % ((long)size * size)));
    }

    /// <summary>Every page pixel the stitch reads or writes, as page and pixel, for showing where the seams are.</summary>
    public static IEnumerable<(int Page, int Pixel)> Footprint(IReadOnlyList<SeamEdge> seams, int size)
    {
        foreach (SeamEdge seam in seams)
        {
            foreach ((Float2 a, Float2 b) in Samples(seam))
            {
                foreach ((int pixel, float _) in Bilinear(a, size)) yield return (seam.PageA, pixel);
                foreach ((int pixel, float _) in Bilinear(b, size)) yield return (seam.PageB, pixel);
            }
        }
    }

    // Matching points along both sides, about two per texel of the longer side
    private static IEnumerable<(Float2 A, Float2 B)> Samples(SeamEdge seam)
    {
        float length = MathF.Max(Float2.Length(seam.A1 - seam.A0), Float2.Length(seam.B1 - seam.B0));
        int n = Math.Clamp((int)MathF.Ceiling(length * 2f), 2, 8192);
        for (int k = 0; k < n; k++)
        {
            float t = (k + 0.5f) / n;
            yield return (seam.A0 + (seam.A1 - seam.A0) * t, seam.B0 + (seam.B1 - seam.B0) * t);
        }
    }

    // The four texels a bilinear tap at a page pixel position reads, and their weights. Texel centres sit at half pixels.
    private static IEnumerable<(int Pixel, float Weight)> Bilinear(Float2 p, int size)
    {
        float x = p.X - 0.5f, y = p.Y - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int Clamp(int v) => Math.Clamp(v, 0, size - 1);
        int cx0 = Clamp(x0), cx1 = Clamp(x0 + 1), cy0 = Clamp(y0), cy1 = Clamp(y0 + 1);
        yield return (cy0 * size + cx0, (1 - fx) * (1 - fy));
        yield return (cy0 * size + cx1, fx * (1 - fy));
        yield return (cy1 * size + cx0, (1 - fx) * fy);
        yield return (cy1 * size + cx1, fx * fy);
    }

    private static (long, long, long) Quantize(Float3 p)
        => ((long)MathF.Round(p.X / WeldDistance), (long)MathF.Round(p.Y / WeldDistance), (long)MathF.Round(p.Z / WeldDistance));

    private static Float2 Pixels(Float2 uv, Float4 so, int size) => new((uv.X * so.X + so.Z) * size, (uv.Y * so.Y + so.W) * size);

    private static bool Near(Float2 a, Float2 b) => MathF.Abs(a.X - b.X) < SamePixel && MathF.Abs(a.Y - b.Y) < SamePixel;
}
