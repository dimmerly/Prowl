// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Runtime;
using Prowl.Vector;
using Prowl.Vector.Geometry;

using Face = Prowl.Vector.Geometry.GeometryData.Face;
using Edge = Prowl.Vector.Geometry.GeometryData.Edge;
using Vertex = Prowl.Vector.Geometry.GeometryData.Vertex;

namespace Prowl.Editor.GUI.SceneView.Editors;

// Selection beyond a single click: box select, X-ray, loops and rings, growing and shrinking, by material, and
// duplicating what is selected.
public sealed partial class ModelingTool
{
    private const float MarqueeThreshold = 4f;
    private static readonly Color32 MarqueeFill = Color32.FromArgb(40, 255, 160, 40);
    private static readonly Color32 MarqueeOutline = Color32.FromArgb(220, 255, 160, 40);

    /// <summary>X-ray: hidden and back-facing elements can be hovered, clicked and box selected, and draw as
    /// clearly as the ones in front. Kept across meshes and mode switches.</summary>
    internal static bool XRay { get; set; }

    // A press on an element or on the background, held until release: a click if it stayed put, a box select if it
    // travelled. The element under the cursor when it went down is what a click picks.
    private sealed class Press
    {
        public ControlID Control;
        public bool OnElement;
        public Vertex? Vert;
        public Edge? Edge;
        public Face? Face;
        public Float2 Start;
        public bool Marquee;
    }

    private Press? _press;

    private void BeginPress(HandleContext ctx, ControlID control, bool onElement)
        => _press = new Press
        {
            Control = control,
            OnElement = onElement,
            Vert = _hoverVert,
            Edge = _hoverEdge,
            Face = _hoverFace,
            Start = ctx.MousePosition,
        };

    private void UpdatePress(SceneToolContext toolCtx)
    {
        HandleContext ctx = toolCtx.Handles;
        if (_press == null) return;
        // The release went unseen (the viewport lost it): nothing to finish.
        if (!ctx.IsHot(_press.Control))
        {
            _press = null;
            return;
        }

        if (!_press.Marquee && HasMesh && ctx.DragExceededThreshold(_press.Control, MarqueeThreshold)) _press.Marquee = true;
        if (_press.Marquee) ctx.Draw.ScreenRect(_press.Start, ctx.MousePosition, MarqueeFill, MarqueeOutline);
        if (!ctx.PrimaryUp) return;

        Press press = _press;
        _press = null;
        ctx.TryEndDrag(press.Control);
        if (press.Marquee) ApplyMarquee(ctx, press.Start, ctx.MousePosition);
        else if (press.OnElement) ClickElement(ctx, press.Vert, press.Edge, press.Face);
        else ClickBackground(toolCtx);
    }

    /// <summary>
    /// Selects the elements of the current mode inside the screen rectangle: vertices by their point, edges by both
    /// ends, faces by their centre. Only what can be seen counts unless X-ray is on. Shift adds, Ctrl removes, plain
    /// replaces.
    /// </summary>
    private void ApplyMarquee(HandleContext ctx, Float2 a, Float2 b)
    {
        if (!HasMesh) return;
        _adjust = null;
        float minX = MathF.Min(a.X, b.X), maxX = MathF.Max(a.X, b.X);
        float minY = MathF.Min(a.Y, b.Y), maxY = MathF.Max(a.Y, b.Y);
        Float4x4 l2w = _target!.Transform.LocalToWorldMatrix;
        Float3 eye = ctx.Camera.GameObject.Transform.Position;

        bool Inside(Float3 world)
            => ctx.WorldToScreen(world) is Float2 p && p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        Float3 World(Vertex v) => Float4x4.TransformPoint(v.Point, l2w);

        var verts = new List<Vertex>();
        var edges = new List<Edge>();
        var faces = new List<Face>();
        switch (SubTool)
        {
            case ModelingSubTool.Vertex:
                foreach (Vertex v in _geo!.Vertices)
                    if (Inside(World(v)) && (XRay || (IsVisible(v, l2w, eye) && !Occluded(eye, World(v)))))
                        verts.Add(v);
                break;
            case ModelingSubTool.Edge:
                foreach (Edge e in _geo!.Edges)
                {
                    Float3 p1 = World(e.Vert1), p2 = World(e.Vert2);
                    if (Inside(p1) && Inside(p2) && (XRay || (IsVisible(e, l2w, eye) && !Occluded(eye, (p1 + p2) * 0.5f))))
                        edges.Add(e);
                }
                break;
            case ModelingSubTool.Face:
                foreach (Face f in _geo!.Faces)
                {
                    Float3 c = Float4x4.TransformPoint(f.Center(), l2w);
                    if (Inside(c) && (XRay || (FacesCamera(f, l2w, eye) && !Occluded(eye, c))))
                        faces.Add(f);
                }
                break;
        }

        if (!ctx.Shift && !ctx.Ctrl) ClearSelection();
        Apply(_verts, verts);
        Apply(_edges, edges);
        Apply(_faces, faces);

        void Apply<T>(HashSet<T> set, List<T> hits)
        {
            if (ctx.Ctrl) set.ExceptWith(hits);
            else set.UnionWith(hits);
        }
    }

    // Whether a face of the mesh lies between the eye and the point. A face through the point itself does not count.
    private bool Occluded(Float3 eye, Float3 point)
    {
        Float3 toPoint = point - eye;
        float distance = Float3.Length(toPoint);
        if (distance < 1e-5f) return false;
        if (RaycastFace(new Ray(eye, toPoint / distance), out float hit) == null) return false;
        return hit < distance - MathF.Max(1e-4f, distance * 1e-3f);
    }

    // ================================================================
    //  Loops, rings, grow, shrink, by material
    // ================================================================

    /// <summary>
    /// Extends the selection along loops. Edges: the loop through each selected edge, or the ring across it. Vertices:
    /// the same for each edge between two selected vertices. Faces: the band of faces through each pair of neighbouring
    /// selected faces.
    /// </summary>
    internal void SelectLoop(bool ring)
    {
        if (_geo == null) return;
        _adjust = null;
        switch (SubTool)
        {
            case ModelingSubTool.Edge:
                foreach (Edge e in _edges.ToList())
                    _edges.UnionWith(ring ? GeometryOperators.EdgeRing(e) : GeometryOperators.EdgeLoop(e));
                break;

            case ModelingSubTool.Vertex:
                foreach (Edge seed in _geo.Edges.Where(e => _verts.Contains(e.Vert1) && _verts.Contains(e.Vert2)).ToList())
                    foreach (Edge e in ring ? GeometryOperators.EdgeRing(seed) : GeometryOperators.EdgeLoop(seed))
                    {
                        _verts.Add(e.Vert1);
                        _verts.Add(e.Vert2);
                    }
                break;

            case ModelingSubTool.Face:
                // The edge two selected faces share runs across the band; its ring is every rung of the band, and
                // the faces either side of each rung are the band.
                foreach (Edge shared in _geo.Edges.Where(e => e.NeighborFaces().Count(_faces.Contains) >= 2).ToList())
                    foreach (Edge rung in GeometryOperators.EdgeRing(shared))
                        _faces.UnionWith(rung.NeighborFaces());
                break;
        }
    }

    /// <summary>Adds every element that touches the selection.</summary>
    internal void GrowSelection()
    {
        if (_geo == null) return;
        _adjust = null;
        switch (SubTool)
        {
            case ModelingSubTool.Vertex:
                foreach (Vertex v in _verts.ToList())
                    foreach (Edge e in v.NeighborEdges())
                    {
                        _verts.Add(e.Vert1);
                        _verts.Add(e.Vert2);
                    }
                break;
            case ModelingSubTool.Edge:
                foreach (Vertex v in _edges.SelectMany(e => new[] { e.Vert1, e.Vert2 }).Distinct().ToList())
                    _edges.UnionWith(v.NeighborEdges());
                break;
            case ModelingSubTool.Face:
                foreach (Vertex v in _faces.SelectMany(f => f.NeighborVertices()).Distinct().ToList())
                    _faces.UnionWith(v.NeighborFaces());
                break;
        }
    }

    /// <summary>Drops every selected element on the edge of the selection.</summary>
    internal void ShrinkSelection()
    {
        if (_geo == null) return;
        _adjust = null;
        switch (SubTool)
        {
            case ModelingSubTool.Vertex:
            {
                var border = _verts.Where(v => v.NeighborEdges().Any(e => !_verts.Contains(e.Vert1) || !_verts.Contains(e.Vert2))).ToList();
                _verts.ExceptWith(border);
                break;
            }
            case ModelingSubTool.Edge:
            {
                var border = _edges.SelectMany(e => new[] { e.Vert1, e.Vert2 })
                    .Where(v => v.NeighborEdges().Any(e => !_edges.Contains(e))).ToHashSet();
                _edges.RemoveWhere(e => border.Contains(e.Vert1) || border.Contains(e.Vert2));
                break;
            }
            case ModelingSubTool.Face:
            {
                var border = _faces.Where(f => f.NeighborVertices().Any(v => v.NeighborFaces().Any(n => !_faces.Contains(n)))).ToList();
                _faces.ExceptWith(border);
                break;
            }
        }
    }

    /// <summary>Adds every face on the material of any selected face.</summary>
    internal void SelectSameMaterial()
    {
        if (_geo == null || _faces.Count == 0) return;
        _adjust = null;
        var slots = _faces.Select(f => f.Material).ToHashSet();
        foreach (Face f in _geo.Faces)
            if (slots.Contains(f.Material)) _faces.Add(f);
    }

    // ================================================================
    //  Duplicate
    // ================================================================

    /// <summary>
    /// Copies the selected faces in place, on new vertices of their own so the copy comes away from the mesh, with
    /// their material and texture mapping, and selects the copy to move it.
    /// </summary>
    internal void DuplicateSelected()
    {
        if (SubTool != ModelingSubTool.Face || _faces.Count == 0) return;
        List<Face> faces = [.. _faces];
        RunOp("Duplicate Faces", g =>
        {
            var copies = new Dictionary<Vertex, Vertex>();
            var made = new List<Face>();
            foreach (Face f in faces)
            {
                Vertex[] corners = [.. f.NeighborVertices().Select(v => copies.TryGetValue(v, out Vertex? c) ? c : copies[v] = g.AddVertex(v.Point))];
                if (g.AddFace(corners) is not Face copy) continue;
                foreach (KeyValuePair<string, GeometryData.AttributeValue> attribute in f.Attributes)
                    copy.Attributes[attribute.Key] = GeometryData.AttributeValue.Copy(attribute.Value);
                made.Add(copy);
            }
            ClearSelection();
            _faces.UnionWith(made);
        });
    }
}
