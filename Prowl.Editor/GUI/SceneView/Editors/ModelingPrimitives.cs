// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;
using Prowl.Vector.Geometry;

namespace Prowl.Editor.GUI.SceneView.Editors;

/// <summary>The shapes the Modeling tool's Block draws into the box dragged out on the grid.</summary>
public enum ModelingPrimitive
{
    Box,
    Stairs,
    Ramp,
    Cylinder,
    Arch,
}

/// <summary>
/// The settings of a primitive. Each shape reads the ones it has: <see cref="Steps"/> for stairs,
/// <see cref="Sides"/> for cylinders and arches, <see cref="Thickness"/> for arches, and <see cref="Turns"/>
/// (quarter turns about up) for the shapes that face a way.
/// </summary>
public sealed record PrimitiveSettings(int Steps = 8, int Sides = 12, float Thickness = 0.5f, int Turns = 0);

/// <summary>
/// Builds level primitives as polygon geometry that fills a box standing on the origin: X across
/// <c>-size.X/2..size.X/2</c>, Y up <c>0..size.Y</c>, Z across <c>-size.Z/2..size.Z/2</c>. Faces are quads and n-gons
/// rather than triangle fans, so their edges stay pickable and they read cleanly in the editor.
/// </summary>
public static class ModelingPrimitives
{
    /// <summary>Whether <paramref name="kind"/> faces a way, and so can be turned.</summary>
    public static bool Turns(ModelingPrimitive kind) => kind is ModelingPrimitive.Stairs or ModelingPrimitive.Ramp or ModelingPrimitive.Arch;

    public static GeometryData Build(ModelingPrimitive kind, Float3 size, PrimitiveSettings settings)
    {
        int turns = Turns(kind) ? ((settings.Turns % 4) + 4) % 4 : 0;
        // Built facing +Z, then turned. A quarter turn swaps the footprint, so build it on the swapped one.
        Float3 built = turns % 2 == 1 ? new Float3(size.Z, size.Y, size.X) : size;

        var solid = new Solid();
        switch (kind)
        {
            case ModelingPrimitive.Stairs: Stairs(solid, built, Math.Max(1, settings.Steps)); break;
            case ModelingPrimitive.Ramp: Ramp(solid, built); break;
            case ModelingPrimitive.Cylinder: Cylinder(solid, built, Math.Max(3, settings.Sides)); break;
            case ModelingPrimitive.Arch: Arch(solid, built, Math.Max(1, settings.Sides), settings.Thickness); break;
            default: return GeometryGenerator.Box(size, new Float3(0, size.Y * 0.5f, 0));
        }
        if (turns != 0) solid.TurnAboutY(turns);
        return solid.ToGeometry();
    }

    // Steps rising toward +Z. The side outline is traced clockwise in (z, y): up the first riser, along each tread
    // and up the next riser to the top, down the back, along the bottom.
    private static void Stairs(Solid s, Float3 size, int steps)
    {
        float hx = size.X * 0.5f, z0 = -size.Z * 0.5f;
        float run = size.Z / steps, rise = size.Y / steps;

        var outline = new List<Float2> { new(z0, 0f) };
        for (int i = 0; i < steps; i++)
        {
            outline.Add(new Float2(z0 + i * run, (i + 1) * rise));
            outline.Add(new Float2(z0 + (i + 1) * run, (i + 1) * rise));
        }
        outline.Add(new Float2(z0 + size.Z, 0f));

        SweepOutline(s, outline, hx);
    }

    // A wedge rising toward +Z: flat bottom, upright back, sloped top.
    private static void Ramp(Solid s, Float3 size)
    {
        float hz = size.Z * 0.5f;
        SweepOutline(s, [new(-hz, 0f), new(hz, size.Y), new(hz, 0f)], size.X * 0.5f);
    }

    /// <summary>
    /// A clockwise outline in (z, y) swept across X from <c>-hx</c> to <c>hx</c>: the outline on each side, and a
    /// quad along each of its edges. Clockwise means the solid lies right of each edge, so outward is left of it.
    /// </summary>
    private static void SweepOutline(Solid s, List<Float2> outline, float hx)
    {
        int n = outline.Count;
        var left = new int[n];
        var right = new int[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = s.Point(new Float3(-hx, outline[i].Y, outline[i].X));
            right[i] = s.Point(new Float3(hx, outline[i].Y, outline[i].X));
        }
        s.Face(-Float3.UnitX, left);
        s.Face(Float3.UnitX, right);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Float2 d = outline[j] - outline[i];
            var outward = new Float3(0f, d.X, -d.Y);
            s.Face(outward, left[i], left[j], right[j], right[i]);
        }
    }

    // An upright prism on an ellipse filling the footprint, with flat polygon caps.
    private static void Cylinder(Solid s, Float3 size, int sides)
    {
        float rx = size.X * 0.5f, rz = size.Z * 0.5f;
        var bottom = new int[sides];
        var top = new int[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = i * MathF.Tau / sides;
            float x = MathF.Cos(a) * rx, z = MathF.Sin(a) * rz;
            bottom[i] = s.Point(new Float3(x, 0f, z));
            top[i] = s.Point(new Float3(x, size.Y, z));
        }
        s.Face(-Float3.UnitY, bottom);
        s.Face(Float3.UnitY, top);
        var center = new Float3(0, size.Y * 0.5f, 0);
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            s.Face(s.Center(bottom[i], bottom[j], top[j], top[i]) - center, bottom[i], bottom[j], top[j], top[i]);
        }
    }

    // A half ring standing in the X/Y plane, open underneath, as deep as the footprint along Z. The outer edge is a
    // half ellipse reaching the footprint's sides and top; the inner one sits the thickness inside it.
    private static void Arch(Solid s, Float3 size, int sides, float thickness)
    {
        float hz = size.Z * 0.5f;
        float ox = size.X * 0.5f, oy = size.Y;
        float t = Math.Clamp(thickness, 0.001f, MathF.Max(0.002f, MathF.Min(ox, oy) - 0.001f));
        float ix = ox - t, iy = oy - t;

        // Corners along the arch, outer and inner, front (+Z) and back (-Z).
        var of = new int[sides + 1];
        var ob = new int[sides + 1];
        var inf = new int[sides + 1];
        var inb = new int[sides + 1];
        for (int i = 0; i <= sides; i++)
        {
            float a = i * MathF.PI / sides;
            float c = MathF.Cos(a), sn = MathF.Sin(a);
            of[i] = s.Point(new Float3(c * ox, sn * oy, hz));
            ob[i] = s.Point(new Float3(c * ox, sn * oy, -hz));
            inf[i] = s.Point(new Float3(c * ix, sn * iy, hz));
            inb[i] = s.Point(new Float3(c * ix, sn * iy, -hz));
        }

        // Each segment is a small convex block, so its middle is inside it and every face of it points away from it.
        for (int i = 0; i < sides; i++)
        {
            int j = i + 1;
            Float3 mid = s.Center(of[i], of[j], inf[i], inf[j], ob[i], ob[j], inb[i], inb[j]);
            s.Face(Float3.UnitZ, of[i], of[j], inf[j], inf[i]);
            s.Face(-Float3.UnitZ, ob[i], ob[j], inb[j], inb[i]);
            s.Face(s.Center(of[i], of[j], ob[j], ob[i]) - mid, of[i], of[j], ob[j], ob[i]);
            s.Face(s.Center(inf[i], inf[j], inb[j], inb[i]) - mid, inf[i], inf[j], inb[j], inb[i]);
        }
        // The two feet standing on the ground.
        s.Face(-Float3.UnitY, of[0], inf[0], inb[0], ob[0]);
        s.Face(-Float3.UnitY, of[sides], inf[sides], inb[sides], ob[sides]);
    }

    /// <summary>Points and faces, each face wound to face the way it was told, then turned into geometry.</summary>
    private sealed class Solid
    {
        private readonly List<Float3> _points = [];
        private readonly List<int[]> _faces = [];

        public int Point(Float3 p)
        {
            _points.Add(p);
            return _points.Count - 1;
        }

        public Float3 Center(params int[] corners)
        {
            Float3 sum = Float3.Zero;
            foreach (int c in corners) sum += _points[c];
            return sum / corners.Length;
        }

        /// <summary>Adds a face, reversed when its winding would turn it away from <paramref name="outward"/>.</summary>
        public void Face(Float3 outward, params int[] corners)
        {
            // Newell's normal, right for concave outlines too.
            Float3 n = Float3.Zero;
            for (int i = 0; i < corners.Length; i++)
            {
                Float3 a = _points[corners[i]], b = _points[corners[(i + 1) % corners.Length]];
                n += new Float3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            int[] face = (int[])corners.Clone();
            if (Float3.Dot(n, outward) < 0f) Array.Reverse(face);
            _faces.Add(face);
        }

        public void TurnAboutY(int quarterTurns)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                Float3 p = _points[i];
                for (int k = 0; k < quarterTurns; k++) p = new Float3(p.Z, p.Y, -p.X);
                _points[i] = p;
            }
        }

        public GeometryData ToGeometry()
        {
            var sizes = new int[_faces.Count];
            var indices = new List<int>();
            for (int f = 0; f < _faces.Count; f++)
            {
                sizes[f] = _faces[f].Length;
                indices.AddRange(_faces[f]);
            }
            // Corners at the same place are one vertex, so the faces join into one closed solid.
            var unique = new Dictionary<Float3, int>();
            var points = new List<Float3>();
            var remap = new int[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                Float3 key = Round(_points[i]);
                if (!unique.TryGetValue(key, out int at))
                {
                    at = unique[key] = points.Count;
                    points.Add(_points[i]);
                }
                remap[i] = at;
            }
            for (int i = 0; i < indices.Count; i++) indices[i] = remap[indices[i]];
            return GeometryGenerator.IndexedMesh([.. points], [.. indices], sizes);
        }

        private static Float3 Round(Float3 p) => new(MathF.Round(p.X, 4), MathF.Round(p.Y, 4), MathF.Round(p.Z, 4));
    }
}
