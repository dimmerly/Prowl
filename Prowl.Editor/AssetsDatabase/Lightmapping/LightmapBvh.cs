// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

namespace Prowl.Editor.Lightmapping;

/// <summary>
/// A binned SAH bounding volume hierarchy over a triangle soup, flattened the way <see cref="LightmapperShader"/> walks
/// it: an inner node's children next to each other, a leaf's triangles one run of <see cref="Order"/>.
/// </summary>
internal sealed class LightmapBvh
{
    /// <summary>Eight floats per node, its min and max, each padded to a float4.</summary>
    public float[] Bounds = [];

    /// <summary>Two per node: a leaf's first triangle and count, or an inner node's left child and 0.</summary>
    public uint[] Links = [];

    /// <summary>The soup's triangles in leaf order.</summary>
    public int[] Order = [];

    public int NodeCount => Links.Length / 2;

    private const int Bins = 12;
    private const int MaxLeaf = 4;
    private const int MaxDepth = 60;   // the shader's traversal stack holds 64 entries

    private struct Node
    {
        public Float3 Min, Max;
        public int First, Count;   // Count 0 is an inner node whose children start at First
    }

    /// <summary>Builds over triangles given as three corners each, at least one, since a node of none reads as inner.</summary>
    public static LightmapBvh Build(IReadOnlyList<Float3> corners)
    {
        int triangles = corners.Count / 3;
        if (triangles == 0) throw new ArgumentException("A BVH needs at least one triangle.", nameof(corners));
        var boxMin = new Float3[triangles];
        var boxMax = new Float3[triangles];
        var centroid = new Float3[triangles];
        var order = new int[triangles];
        for (int i = 0; i < triangles; i++)
        {
            Float3 a = corners[i * 3], b = corners[i * 3 + 1], c = corners[i * 3 + 2];
            boxMin[i] = Min(a, Min(b, c));
            boxMax[i] = Max(a, Max(b, c));
            centroid[i] = (boxMin[i] + boxMax[i]) * 0.5f;
            order[i] = i;
        }

        var nodes = new List<Node>(Math.Max(1, triangles * 2)) { new() { First = 0, Count = triangles } };
        var work = new Stack<(int Node, int Depth)>();
        work.Push((0, 0));

        Span<int> binCount = stackalloc int[Bins];
        Span<Float3> binMin = stackalloc Float3[Bins];
        Span<Float3> binMax = stackalloc Float3[Bins];
        Span<float> rightCost = stackalloc float[Bins];

        while (work.Count > 0)
        {
            (int index, int depth) = work.Pop();
            Node node = nodes[index];
            int start = node.First, count = node.Count;

            Float3 min = new(float.MaxValue), max = new(float.MinValue);
            Float3 cMin = new(float.MaxValue), cMax = new(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = order[i];
                min = Min(min, boxMin[t]); max = Max(max, boxMax[t]);
                cMin = Min(cMin, centroid[t]); cMax = Max(cMax, centroid[t]);
            }
            if (count == 0) { min = Float3.Zero; max = Float3.Zero; }
            node.Min = min;
            node.Max = max;

            if (count <= MaxLeaf || depth >= MaxDepth)
            {
                nodes[index] = node;
                continue;
            }

            // The cheapest split of the centroids over every axis, in bins
            int bestAxis = -1, bestBin = -1;
            float bestCost = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                float lo = Axis(cMin, axis), extent = Axis(cMax, axis) - lo;
                if (extent <= 1e-12f) continue;
                float toBin = Bins / extent;
                for (int b = 0; b < Bins; b++) { binCount[b] = 0; binMin[b] = new Float3(float.MaxValue); binMax[b] = new Float3(float.MinValue); }
                for (int i = start; i < start + count; i++)
                {
                    int t = order[i];
                    int b = Math.Min(Bins - 1, (int)((Axis(centroid[t], axis) - lo) * toBin));
                    binCount[b]++;
                    binMin[b] = Min(binMin[b], boxMin[t]);
                    binMax[b] = Max(binMax[b], boxMax[t]);
                }

                Float3 rMin = new(float.MaxValue), rMax = new(float.MinValue);
                int rCount = 0;
                for (int b = Bins - 1; b > 0; b--)
                {
                    rCount += binCount[b];
                    if (binCount[b] > 0) { rMin = Min(rMin, binMin[b]); rMax = Max(rMax, binMax[b]); }
                    rightCost[b] = rCount == 0 ? 0f : rCount * Area(rMin, rMax);
                }
                Float3 lMin = new(float.MaxValue), lMax = new(float.MinValue);
                int lCount = 0;
                for (int b = 0; b < Bins - 1; b++)
                {
                    lCount += binCount[b];
                    if (binCount[b] > 0) { lMin = Min(lMin, binMin[b]); lMax = Max(lMax, binMax[b]); }
                    if (lCount == 0 || lCount == count) continue;
                    float cost = lCount * Area(lMin, lMax) + rightCost[b + 1];
                    if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestBin = b; }
                }
            }

            int mid;
            if (bestAxis < 0)
            {
                // Every centroid in one place: halve the run so a pile of coincident triangles still splits
                mid = start + count / 2;
            }
            else
            {
                float leafCost = count * Area(min, max);
                if (bestCost >= leafCost && count <= 16)
                {
                    nodes[index] = node;
                    continue;
                }
                float lo = Axis(cMin, bestAxis), toBin = Bins / (Axis(cMax, bestAxis) - lo);
                int i = start, j = start + count - 1;
                while (i <= j)
                {
                    int b = Math.Min(Bins - 1, (int)((Axis(centroid[order[i]], bestAxis) - lo) * toBin));
                    if (b <= bestBin) { i++; continue; }
                    (order[i], order[j]) = (order[j], order[i]);
                    j--;
                }
                mid = i;
                if (mid == start || mid == start + count) mid = start + count / 2;
            }

            int left = nodes.Count;
            nodes.Add(new Node { First = start, Count = mid - start });
            nodes.Add(new Node { First = mid, Count = start + count - mid });
            node.First = left;
            node.Count = 0;
            nodes[index] = node;
            work.Push((left, depth + 1));
            work.Push((left + 1, depth + 1));
        }

        var bounds = new float[nodes.Count * 8];
        var links = new uint[nodes.Count * 2];
        for (int i = 0; i < nodes.Count; i++)
        {
            Node n = nodes[i];
            bounds[i * 8 + 0] = n.Min.X; bounds[i * 8 + 1] = n.Min.Y; bounds[i * 8 + 2] = n.Min.Z;
            bounds[i * 8 + 4] = n.Max.X; bounds[i * 8 + 5] = n.Max.Y; bounds[i * 8 + 6] = n.Max.Z;
            links[i * 2] = (uint)n.First;
            links[i * 2 + 1] = (uint)n.Count;
        }
        return new LightmapBvh { Bounds = bounds, Links = links, Order = order };
    }

    private static float Axis(Float3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static Float3 Min(Float3 a, Float3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
    private static Float3 Max(Float3 a, Float3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    private static float Area(Float3 min, Float3 max)
    {
        Float3 d = max - min;
        return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
    }
}
