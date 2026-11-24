// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

namespace Prowl.Editor.Lightmapping;

/// <summary>One renderer's lightmapped surface in world space, with its own lightmap UVs and where it landed.</summary>
internal sealed class LightmapChart
{
    public Float3[] Positions = [];   // three per triangle
    public Float3[] Normals = [];     // three per triangle
    public Float2[] UVs = [];         // three per triangle, the chart's own [0, 1] square

    public int Page, X, Y, Width, Height;

    /// <summary>The page UV of a chart UV is <c>uv * xy + zw</c>.</summary>
    public Float4 ScaleOffset(int pageSize) => new(Width / (float)pageSize, Height / (float)pageSize, X / (float)pageSize, Y / (float)pageSize);
}

/// <summary>The texels one page's charts cover: where each one sits in the world and which way it faces.</summary>
internal sealed class TexelGrid(int size)
{
    public readonly int Size = size;
    public readonly bool[] Covered = new bool[size * size];
    public readonly bool[] Inside = new bool[size * size];   // the texel centre lies in its triangle, not only its square
    public readonly Float3[] Position = new Float3[size * size];
    public readonly Float3[] Normal = new Float3[size * size];
    public readonly Float3[] StepX = new Float3[size * size];   // the world step to the next texel along x
    public readonly Float3[] StepY = new Float3[size * size];   // and along y
    public readonly int[] Chart = new int[size * size];         // which chart of the page the texel is on

    /// <summary>
    /// Texels whose centre lies inside two triangles that are apart in the world: lightmap UVs that overlap, so two
    /// surfaces share the texel and one of them gets the other's light.
    /// </summary>
    public readonly bool[] Overlap = new bool[size * size];
}

/// <summary>Packs charts into square pages and rasterizes them into texels, row 0 holding page v 0.</summary>
internal static class LightmapAtlas
{
    /// <summary>
    /// Sizes each chart so its most stretched triangle still gets <paramref name="texelsPerUnit"/>, then shelf packs them,
    /// largest first, into as many pages as it takes. Returns the page count.
    /// </summary>
    public static int Pack(IReadOnlyList<LightmapChart> charts, int pageSize, float texelsPerUnit, int padding)
    {
        int maxSide = pageSize - 2 * padding;
        if (maxSide < 4) throw new ArgumentException("The page is too small for its padding.", nameof(pageSize));

        foreach (LightmapChart chart in charts)
        {
            int side = (int)MathF.Ceiling(MathF.Sqrt(MaxWorldOverUV(chart)) * MathF.Max(texelsPerUnit, 1e-3f));
            chart.Width = chart.Height = Math.Clamp(side, 4, maxSide);
        }

        var order = new List<LightmapChart>(charts);
        order.Sort((a, b) => b.Width.CompareTo(a.Width));

        var cursors = new List<(int X, int Y, int Row)>();
        foreach (LightmapChart chart in order)
        {
            int w = chart.Width, h = chart.Height;
            bool placed = false;
            for (int p = 0; p < cursors.Count && !placed; p++)
            {
                (int cx, int cy, int row) = cursors[p];
                if (cx + w + padding > pageSize) { cy += row + padding; cx = padding; row = 0; }
                if (cy + h + padding > pageSize) continue;
                (chart.Page, chart.X, chart.Y) = (p, cx, cy);
                cursors[p] = (cx + w + padding, cy, Math.Max(row, h));
                placed = true;
            }
            if (!placed)
            {
                (chart.Page, chart.X, chart.Y) = (cursors.Count, padding, padding);
                cursors.Add((padding + w + padding, padding, h));
            }
        }
        return cursors.Count;
    }

    // The largest world area over UV area of any real triangle, so the most squeezed one still meets the density.
    private static float MaxWorldOverUV(LightmapChart chart)
    {
        float max = 0f;
        for (int i = 0; i + 2 < chart.Positions.Length; i += 3)
        {
            Float3 a = chart.Positions[i], b = chart.Positions[i + 1], c = chart.Positions[i + 2];
            float world = Float3.Length(Float3.Cross(b - a, c - a)) * 0.5f;
            if (world < 1e-9f) continue;
            float uv = MathF.Abs(Edge(chart.UVs[i], chart.UVs[i + 1], chart.UVs[i + 2])) * 0.5f;
            if (uv < 1e-10f) continue;
            max = MathF.Max(max, world / uv);
        }
        return max;
    }

    /// <summary>
    /// Marks every texel a triangle of <paramref name="chart"/> touches, conservatively so seams don't crack, with the
    /// world position and normal at the texel centre, pulled into the triangle when the centre falls outside it.
    /// A texel whose centre is inside a triangle wins over one that only touches it.
    /// </summary>
    public static void Rasterize(LightmapChart chart, int chartId, TexelGrid grid)
    {
        int size = grid.Size;
        Float4 so = chart.ScaleOffset(size);
        for (int t = 0; t + 2 < chart.Positions.Length; t += 3)
        {
            Float2 p0 = ToPixels(chart.UVs[t], so, size), p1 = ToPixels(chart.UVs[t + 1], so, size), p2 = ToPixels(chart.UVs[t + 2], so, size);
            float area = Edge(p0, p1, p2);
            if (MathF.Abs(area) < 1e-10f) continue;

            float minX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X)), maxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
            float minY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)), maxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
            // UVs far outside the chart (a tiling UV0 standing in for lightmap UVs) would walk huge empty areas
            if (maxX < 0 || minX > size || maxY < 0 || minY > size) continue;
            if (maxX - minX > size * 4 || maxY - minY > size * 4) continue;

            int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(size - 1, (int)MathF.Floor(maxX));
            int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(size - 1, (int)MathF.Floor(maxY));
            Float3 a = chart.Positions[t], b = chart.Positions[t + 1], c = chart.Positions[t + 2];
            Float3 na = chart.Normals[t], nb = chart.Normals[t + 1], nc = chart.Normals[t + 2];

            // The triangle's world position is affine in pixels, so one step along each pixel axis is the same everywhere
            Float2 d1 = p1 - p0, d2 = p2 - p0;
            Float3 stepX = ((b - a) * d2.Y - (c - a) * d1.Y) / area;
            Float3 stepY = ((c - a) * d1.X - (b - a) * d2.X) / area;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!Overlaps(p0, p1, p2, new Float2(x, y), new Float2(x + 1, y + 1))) continue;

                    Float2 centre = new(x + 0.5f, y + 0.5f);
                    float w0 = Edge(p1, p2, centre) / area;
                    float w1 = Edge(p2, p0, centre) / area;
                    float w2 = 1f - w0 - w1;
                    bool inside = w0 >= 0 && w1 >= 0 && w2 >= 0;
                    if (!inside) Closest(p0, p1, p2, centre, area, out w0, out w1, out w2);

                    int i = y * size + x;
                    if (grid.Covered[i] && grid.Inside[i] && inside && !grid.Overlap[i])
                    {
                        // Neighbouring triangles share the texels along their edge; two holding its centre far apart overlap
                        Float3 here = a * w0 + b * w1 + c * w2;
                        float texelWorld = MathF.Max(Float3.Length(stepX), Float3.Length(stepY));
                        if (grid.Chart[i] != chartId || Float3.Length(here - grid.Position[i]) > 2f * texelWorld + 1e-5f)
                            grid.Overlap[i] = true;
                    }
                    if (grid.Covered[i] && (grid.Inside[i] || !inside)) continue;

                    // A little toward the centroid, so a texel on a chart edge doesn't start its rays on the next surface
                    const float Inset = 0.02f, Third = 1f / 3f;
                    w0 = w0 * (1f - Inset) + Third * Inset;
                    w1 = w1 * (1f - Inset) + Third * Inset;
                    w2 = w2 * (1f - Inset) + Third * Inset;

                    Float3 n = na * w0 + nb * w1 + nc * w2;
                    if (Float3.Dot(n, n) < 1e-10f) continue;
                    grid.Covered[i] = true;
                    grid.Inside[i] = inside;
                    grid.Position[i] = a * w0 + b * w1 + c * w2;
                    grid.Normal[i] = Float3.Normalize(n);
                    grid.StepX[i] = stepX;
                    grid.StepY[i] = stepY;
                    grid.Chart[i] = chartId;
                }
            }
        }
    }

    private static Float2 ToPixels(Float2 uv, Float4 so, int size) => new((uv.X * so.X + so.Z) * size, (uv.Y * so.Y + so.W) * size);

    // Twice the signed area of abc
    private static float Edge(Float2 a, Float2 b, Float2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    // Separating axes between a triangle and a box: the box's two, then each edge's normal. Only touching is no overlap.
    private static bool Overlaps(Float2 a, Float2 b, Float2 c, Float2 min, Float2 max)
    {
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) <= min.X || MathF.Min(a.X, MathF.Min(b.X, c.X)) >= max.X) return false;
        if (MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) <= min.Y || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) >= max.Y) return false;
        Float2 centre = (min + max) * 0.5f, half = (max - min) * 0.5f;
        return !Separates(a, b, a, b, c, centre, half) && !Separates(b, c, a, b, c, centre, half) && !Separates(c, a, a, b, c, centre, half);
    }

    private static bool Separates(Float2 p, Float2 q, Float2 a, Float2 b, Float2 c, Float2 centre, Float2 half)
    {
        Float2 axis = new(p.Y - q.Y, q.X - p.X);
        float da = axis.X * a.X + axis.Y * a.Y, db = axis.X * b.X + axis.Y * b.Y, dc = axis.X * c.X + axis.Y * c.Y;
        float triMin = MathF.Min(da, MathF.Min(db, dc)), triMax = MathF.Max(da, MathF.Max(db, dc));
        float boxCentre = axis.X * centre.X + axis.Y * centre.Y;
        float boxHalf = MathF.Abs(axis.X) * half.X + MathF.Abs(axis.Y) * half.Y;
        return triMin >= boxCentre + boxHalf || triMax <= boxCentre - boxHalf;
    }

    // Barycentrics of the point of the triangle's edges nearest p
    private static void Closest(Float2 a, Float2 b, Float2 c, Float2 p, float area, out float w0, out float w1, out float w2)
    {
        float best = float.MaxValue;
        Float2 nearest = a;
        foreach ((Float2 s, Float2 e) in (ReadOnlySpan<(Float2, Float2)>)[(a, b), (b, c), (c, a)])
        {
            Float2 d = e - s;
            float len = d.X * d.X + d.Y * d.Y;
            float t = len > 0f ? Math.Clamp(((p.X - s.X) * d.X + (p.Y - s.Y) * d.Y) / len, 0f, 1f) : 0f;
            Float2 q = s + d * t;
            float dist = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
            if (dist < best) { best = dist; nearest = q; }
        }
        w0 = Math.Clamp(Edge(b, c, nearest) / area, 0f, 1f);
        w1 = Math.Clamp(Edge(c, a, nearest) / area, 0f, 1f);
        w2 = Math.Max(0f, 1f - w0 - w1);
    }
}
