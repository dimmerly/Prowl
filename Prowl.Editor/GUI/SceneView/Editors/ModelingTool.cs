// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Editor.Core;
using Prowl.Editor.GUI.Popups;
using Prowl.Editor.GUI.Panels;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Vector.Geometry;

using Face = Prowl.Vector.Geometry.GeometryData.Face;
using Edge = Prowl.Vector.Geometry.GeometryData.Edge;
using Vertex = Prowl.Vector.Geometry.GeometryData.Vertex;

namespace Prowl.Editor.GUI.SceneView.Editors;

/// <summary>Which part of the Modeling tool the scene view is using.</summary>
public enum ModelingSubTool
{
    /// <summary>Drag out a base on the grid, then move up to set the height, to make a new block.</summary>
    Block,
    Vertex,
    Edge,
    Face,
}

/// <summary>
/// Modeling mode: polygon editing of <see cref="EditableMesh"/> in the scene view, a small take on s&amp;box's Mapping
/// tool. Make blocks, select vertices, edges or faces, move them with the handle, and run the
/// <see cref="GeometryOperators"/> on them (extrude, inset, bevel, split, merge, delete). Shift-dragging the handle
/// with faces selected extrudes them first.
///
/// <para>The selected GameObject's mesh is held as live <see cref="GeometryData"/> so selections can point at its
/// elements; every edit writes it back with <see cref="EditableMesh.SetGeometry"/>. A version change the tool did not
/// make (an undo) rebuilds it.</para>
/// </summary>
[GlobalSceneTool]
public sealed class ModelingTool : SceneTool
{
    public override string Name => "Modeling";
    public override string Icon => EditorIcons.Cube;
    // Always live, so its bar can offer Block and Edit Mesh from Object mode; it only takes scene input while Modeling.
    public override bool SuppressTransformGizmo => SceneTools.EditMode == SceneEditMode.Modeling;

    /// <summary>The active sub tool, kept across mode switches.</summary>
    public static ModelingSubTool SubTool { get; set; } = ModelingSubTool.Face;

    private const string MoveHandleId = "modeling_move";
    private const float VertexPickPixels = 10f;
    private const float EdgePickPixels = 7f;

    private static readonly Color32 WireColor = new(170, 180, 195, 200);
    private static readonly Color32 HiddenWireColor = new(170, 180, 195, 55);
    private static readonly Color32 SelectedColor = new(255, 160, 40, 255);
    private static readonly Color32 SelectedFill = new(255, 160, 40, 70);
    private static readonly Color32 HoverColor = new(255, 235, 120, 255);
    private static readonly Color32 HoverFill = new(255, 235, 120, 45);
    private static readonly Color32 BlockColor = new(90, 190, 255, 255);
    private static readonly Color32 BlockFill = new(90, 190, 255, 50);

    private EditableMesh? _target;
    private GeometryData? _geo;
    private int _geoVersion;

    private readonly HashSet<Vertex> _verts = [];
    private readonly HashSet<Edge> _edges = [];
    private readonly HashSet<Face> _faces = [];

    // The element under the cursor this frame, one of these at most.
    private Vertex? _hoverVert;
    private Edge? _hoverEdge;
    private Face? _hoverFace;

    private bool _dragging;

    // Block creation: 0 idle, 1 dragging the base, 2 setting the height.
    private int _blockPhase;
    private Float3 _blockStart, _blockEnd;
    private float _blockHeight;

    public override void OnActivated(SceneToolContext ctx) => Reset();

    public override void OnDeactivated()
    {
        Reset();
        _target = null;
        _geo = null;
    }

    private void Reset()
    {
        ClearSelection();
        _dragging = false;
        _drag = null;
        _blockPhase = 0;
        TransformHandles.Forget(MoveHandleId);
    }

    internal void ClearSelection()
    {
        _adjust = null;
        _verts.Clear();
        _edges.Clear();
        _faces.Clear();
    }

    // ================================================================
    //  Input
    // ================================================================

    public override void OnSceneInput(SceneToolContext toolCtx)
    {
        HandleContext ctx = toolCtx.Handles;
        SyncTarget(toolCtx);

        bool modeling = SceneTools.EditMode == SceneEditMode.Modeling;
        if (!ctx.Ctrl && !ctx.Alt && ctx.GetKeyDown(KeyCode.B)) { Enter(ModelingSubTool.Block); return; }
        if (ctx.GetKeyDown(KeyCode.Tab))
        {
            if (modeling) Exit();
            else if (_target.IsValid()) Enter(ModelingSubTool.Face);
            return;
        }
        if (!modeling) return;

        // Esc backs out a step at a time: a block being drawn, then the selection, then Modeling itself.
        if (ctx.GetKeyDown(KeyCode.Escape))
        {
            if (_blockPhase != 0) _blockPhase = 0;
            else if (SelectionCount > 0) ClearSelection();
            else { SceneTools.EditMode = SceneEditMode.Object; return; }
        }

        if (!ctx.Ctrl && !ctx.Alt)
        {
            if (ctx.GetKeyDown(KeyCode.Number1)) SetSubTool(ModelingSubTool.Block);
            else if (ctx.GetKeyDown(KeyCode.Number2)) SetSubTool(ModelingSubTool.Vertex);
            else if (ctx.GetKeyDown(KeyCode.Number3)) SetSubTool(ModelingSubTool.Edge);
            else if (ctx.GetKeyDown(KeyCode.Number4)) SetSubTool(ModelingSubTool.Face);
        }
        if (ctx.Ctrl && ctx.GetKeyDown(KeyCode.A)) SelectAll();

        if (SubTool == ModelingSubTool.Block)
        {
            BlockInput(toolCtx);
            DrawMesh(ctx);
            return;
        }

        if (_target.IsValid() && _geo != null)
        {
            if (ctx.GetKeyDown(KeyCode.Delete) || ctx.GetKeyDown(KeyCode.Backspace)) DeleteSelected();

            bool handleHot = TransformElements(ctx);
            if (!handleHot && !_dragging) Hover(ctx);
            else { _hoverVert = null; _hoverEdge = null; _hoverFace = null; }

            ControlID element = ControlId("element");
            float distance = ElementDistance(ctx, out float depth);
            ctx.AddControl(element, distance, depth);
            if (ctx.ViewportHovered && ctx.TryBeginDrag(element)) ClickElement(ctx);
        }

        // Absorbs every other click so it never falls through to object picking, which would leave the mesh.
        ControlID background = ControlId("background");
        ctx.AddControl(background, HandleContext.BodyDistance);
        // Mouse buttons are global, so a press only counts here while the cursor is on the viewport itself and not on
        // the bar, the tool strip or any other panel.
        if (ctx.ViewportHovered && ctx.TryBeginDrag(background)) ClickBackground(toolCtx);

        DrawMesh(ctx);
    }

    /// <summary>Follows the GameObject selection, and rebuilds the geometry when the mesh changed under the tool.</summary>
    private void SyncTarget(SceneToolContext ctx)
    {
        GameObject? go = ctx.ActiveObject;
        EditableMesh? mesh = go.IsValid() ? go.GetComponent<EditableMesh>() : null;
        if (mesh.IsNotValid()) mesh = null;

        if (!ReferenceEquals(mesh, _target))
        {
            _target = mesh;
            _geo = null;
            Reset();
        }

        // Selecting something that is not an editable mesh leaves Modeling, as leaving a terrain does. Block draws
        // new meshes from nothing, so it stays.
        if (_target.IsNotValid() && SubTool != ModelingSubTool.Block && _blockPhase == 0 && SceneTools.EditMode == SceneEditMode.Modeling)
            SceneTools.EditMode = SceneEditMode.Object;

        if (_target.IsValid() && (_geo == null || _geoVersion != _target.Version))
        {
            _geo = _target.ToGeometry();
            _geoVersion = _target.Version;
            ClearSelection();
            _dragging = false;
            _drag = null;
        }
    }

    // A transform drag in progress: where it started, the gizmo's totals so far, and every moving vertex's world
    // position at the start. Each frame applies the whole transform to those starts again, so rotating and scaling
    // never accumulate rounding.
    private sealed class ElementDrag
    {
        public Float3 Pivot;
        public Quaternion Frame;
        public Float3 Position;
        public Quaternion Rotation;
        public Float3 Scale = Float3.One;
        public readonly Dictionary<Vertex, Float3> Start = [];
    }

    private ElementDrag? _drag;

    /// <summary>
    /// The scene view's own transform tool (Move, Rotate, Scale or Universal, from the tool strip and its shortcuts),
    /// applied to the selected elements around their centre, in the Local or Global frame the scene bar sets.
    /// </summary>
    private bool TransformElements(HandleContext ctx)
    {
        HashSet<Vertex> verts = SelectedVertices();
        if (verts.Count == 0 || _target.IsNotValid() || _geo == null)
        {
            _drag = null;
            _dragging = false;
            return false;
        }

        Float4x4 l2w = _target.Transform.LocalToWorldMatrix;
        Float3 position;
        Quaternion rotation;
        Float3 scale;
        if (_drag != null)
        {
            position = _drag.Position;
            rotation = _drag.Rotation;
            scale = _drag.Scale;
        }
        else
        {
            Float3 center = Float3.Zero;
            foreach (Vertex v in verts) center += v.Point;
            position = Float4x4.TransformPoint(center / verts.Count, l2w);
            rotation = SceneTools.Orientation == PivotOrientation.Local ? _target.Transform.Rotation : Quaternion.Identity;
            scale = Float3.One;
        }
        Float3 pivot = position;
        Quaternion frame = rotation;

        bool changed = TransformHandles.TransformHandle(ctx, MoveHandleId, SceneTools.GizmoMode,
            ref position, ref rotation, ref scale, out bool hot);

        if (changed)
        {
            Undo.RecordObject(_target, "Transform Mesh Elements");
            if (_drag == null)
            {
                // Shift-dragging the move handle pulls a copy of the faces out, the way s&box and most modellers extrude.
                if (ctx.Shift && SubTool == ModelingSubTool.Face && _faces.Count > 0 && SceneTools.Transform != TransformTool.Rotate)
                {
                    ExtrudeFaces(0f);
                    verts = SelectedVertices();
                }
                _drag = new ElementDrag { Pivot = pivot, Frame = frame };
                foreach (Vertex v in verts) _drag.Start[v] = Float4x4.TransformPoint(v.Point, l2w);
            }

            _drag.Position = position;
            _drag.Rotation = rotation;
            _drag.Scale = scale;

            // Scale in the handle's frame at the start, then the rotation since, then the move.
            Quaternion inverseFrame = Quaternion.Inverse(_drag.Frame);
            Quaternion turned = rotation * inverseFrame;
            Float4x4 w2l = _target.Transform.WorldToLocalMatrix;
            foreach ((Vertex v, Float3 start) in _drag.Start)
            {
                Float3 inFrame = inverseFrame * (start - _drag.Pivot) * scale;
                v.Point = Float4x4.TransformPoint(position + turned * (_drag.Frame * inFrame), w2l);
            }
            Commit();
        }

        if (_drag != null && !ctx.PrimaryHeld) _drag = null;
        _dragging = _drag != null;
        return hot || _dragging;
    }

    private void Hover(HandleContext ctx)
    {
        _hoverVert = null; _hoverEdge = null; _hoverFace = null;
        if (_target.IsNotValid() || _geo == null || !ctx.ViewportHovered || ctx.Blocked) return;

        Float4x4 l2w = _target.Transform.LocalToWorldMatrix;
        Float3 eye = ctx.Camera.GameObject.Transform.Position;

        switch (SubTool)
        {
            case ModelingSubTool.Vertex:
            {
                float best = float.MaxValue;
                foreach (Vertex v in _geo.Vertices)
                {
                    if (!IsVisible(v, l2w, eye)) continue;
                    float d = ctx.DistanceToPoint(Float4x4.TransformPoint(v.Point, l2w), VertexPickPixels);
                    if (d < best) { best = d; _hoverVert = v; }
                }
                break;
            }
            case ModelingSubTool.Edge:
            {
                float best = float.MaxValue;
                foreach (Edge e in _geo.Edges)
                {
                    if (!IsVisible(e, l2w, eye)) continue;
                    float d = ScreenDistanceToSegment(ctx, Float4x4.TransformPoint(e.Vert1.Point, l2w), Float4x4.TransformPoint(e.Vert2.Point, l2w));
                    if (d < EdgePickPixels && d < best) { best = d; _hoverEdge = e; }
                }
                break;
            }
            case ModelingSubTool.Face:
                _hoverFace = RaycastFace(ctx.MouseRay, out _);
                break;
        }
    }

    /// <summary>The hovered element's arbitration distance: its pixel distance for points and lines, or a body
    /// distance for a face, so any real handle near the cursor still wins over the face under it.</summary>
    private float ElementDistance(HandleContext ctx, out float depth)
    {
        depth = float.MaxValue;
        if (_target.IsNotValid()) return float.MaxValue;
        Float4x4 l2w = _target.Transform.LocalToWorldMatrix;

        if (_hoverVert != null)
        {
            Float3 world = Float4x4.TransformPoint(_hoverVert.Point, l2w);
            depth = ctx.DepthOf(world);
            return ctx.DistanceToPoint(world, VertexPickPixels);
        }
        if (_hoverEdge != null)
        {
            Float3 a = Float4x4.TransformPoint(_hoverEdge.Vert1.Point, l2w), b = Float4x4.TransformPoint(_hoverEdge.Vert2.Point, l2w);
            depth = ctx.DepthOf((a + b) * 0.5f);
            return ScreenDistanceToSegment(ctx, a, b);
        }
        if (_hoverFace != null && RaycastFace(ctx.MouseRay, out float t) == _hoverFace)
        {
            depth = ctx.DepthOf(ctx.MouseRay.Origin + ctx.MouseRay.Direction * t);
            return HandleContext.BodyDistance - 1f;
        }
        return float.MaxValue;
    }

    private void ClickElement(HandleContext ctx)
    {
        if (_hoverVert != null) Pick(_verts, _hoverVert, ctx);
        else if (_hoverEdge != null) Pick(_edges, _hoverEdge, ctx);
        else if (_hoverFace != null) Pick(_faces, _hoverFace, ctx);
    }

    // Shift adds, Ctrl toggles, a plain click replaces.
    private void Pick<T>(HashSet<T> set, T element, HandleContext ctx)
    {
        _adjust = null;
        if (ctx.Shift) { set.Add(element); return; }
        if (ctx.Ctrl) { if (!set.Remove(element)) set.Add(element); return; }
        ClearSelection();
        set.Add(element);
    }

    /// <summary>A click that hit no element: switch to another editable mesh under the cursor, or deselect.</summary>
    private void ClickBackground(SceneToolContext toolCtx)
    {
        HandleContext ctx = toolCtx.Handles;
        EditableMesh? hit = RaycastEditableMeshes(toolCtx.Scene, ctx.MouseRay, out _);
        if (hit.IsValid() && !ReferenceEquals(hit, _target))
        {
            Selection.Select(hit.GameObject);
            return;
        }
        if (!ctx.Shift && !ctx.Ctrl) ClearSelection();
    }

    // ================================================================
    //  Block creation
    // ================================================================

    private void BlockInput(SceneToolContext toolCtx)
    {
        HandleContext ctx = toolCtx.Handles;
        ControlID control = ControlId("block");
        ctx.AddControl(control, HandleContext.BodyDistance);
        float step = SceneTools.MoveSnap;

        switch (_blockPhase)
        {
            case 0:
                if (ctx.ViewportHovered && ctx.TryBeginDrag(control) && GroundPoint(toolCtx, ctx.MouseRay, out Float3 start))
                {
                    _blockStart = _blockEnd = Snap(start, step);
                    _blockPhase = 1;
                }
                break;

            case 1:
                if (RayPlane(ctx.MouseRay, _blockStart.Y, out Float3 end)) _blockEnd = Snap(end, step);
                if (!ctx.PrimaryHeld)
                {
                    ctx.TryEndDrag(control);
                    bool hasArea = MathF.Abs(_blockEnd.X - _blockStart.X) >= step * 0.5f && MathF.Abs(_blockEnd.Z - _blockStart.Z) >= step * 0.5f;
                    _blockPhase = hasArea ? 2 : 0;
                    _blockHeight = step;
                }
                break;

            case 2:
                _blockHeight = MathF.Max(step, MathF.Round(HeightUnderCursor(ctx) / step) * step);
                if (ctx.ViewportHovered && ctx.TryBeginDrag(control))
                {
                    CreateBlock();
                    _blockPhase = 0;
                }
                break;
        }

        if (_blockPhase != 0) DrawBlockPreview();
    }

    // How far above the base the cursor is, measured on the vertical line through the dragged corner.
    private float HeightUnderCursor(HandleContext ctx)
    {
        Float3 eye = ctx.Camera.GameObject.Transform.Position;
        Float3 toEye = eye - _blockEnd;
        toEye.Y = 0;
        if (Float3.LengthSquared(toEye) < 1e-6f) return _blockHeight;

        Float3 n = Float3.Normalize(toEye);
        Ray ray = ctx.MouseRay;
        float denom = Float3.Dot(ray.Direction, n);
        if (MathF.Abs(denom) < 1e-6f) return _blockHeight;
        float t = Float3.Dot(_blockEnd - ray.Origin, n) / denom;
        if (t <= 0) return _blockHeight;
        return (ray.Origin + ray.Direction * t).Y - _blockStart.Y;
    }

    private void CreateBlock()
    {
        var min = new Float3(MathF.Min(_blockStart.X, _blockEnd.X), _blockStart.Y, MathF.Min(_blockStart.Z, _blockEnd.Z));
        var max = new Float3(MathF.Max(_blockStart.X, _blockEnd.X), _blockStart.Y, MathF.Max(_blockStart.Z, _blockEnd.Z));
        var size = new Float3(max.X - min.X, _blockHeight, max.Z - min.Z);
        var basePoint = new Float3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);

        GameObject go = HierarchyPanel.CreateGameObject("Block", null, select: true, beginRename: false);
        go.Transform.Position = basePoint;
        go.IsStatic = true; // level geometry: bakes into lightmaps and navigation
        EditableMesh mesh = go.AddComponent<EditableMesh>();
        mesh.SetGeometry(GeometryGenerator.Box(size, new Float3(0, size.Y * 0.5f, 0)));
        go.AddComponent<MeshCollider>(); // builds from the editable mesh and follows its edits

        _lastBlock = mesh;
        _lastBlockSize = size;
        _lastBlockFlipped = false;
        _lastBlockVersion = mesh.Version;
    }

    // The block placed last and its size, which the bar can still change until the block is edited any other way.
    private EditableMesh? _lastBlock;
    private Float3 _lastBlockSize;
    private bool _lastBlockFlipped;
    private int _lastBlockVersion;

    private bool LastBlockEditable() => _lastBlock.IsValid() && _lastBlock.Version == _lastBlockVersion;

    // Width, height and depth of the block just placed, as the Inspector's X/Y/Z fields look. The block stays standing
    // on the point it was drawn from.
    private void DrawBlockSize(Paper paper, string id)
    {
        BarHint(paper, $"{id}_l", "Block size");
        SizeField(paper, $"{id}_w", "W", EditorTheme.Red400, _lastBlockSize.X, v => ResizeLastBlock(new Float3(v, _lastBlockSize.Y, _lastBlockSize.Z)));
        SizeField(paper, $"{id}_h", "H", EditorTheme.Green400, _lastBlockSize.Y, v => ResizeLastBlock(new Float3(_lastBlockSize.X, v, _lastBlockSize.Z)));
        SizeField(paper, $"{id}_d", "D", EditorTheme.Blue400, _lastBlockSize.Z, v => ResizeLastBlock(new Float3(_lastBlockSize.X, _lastBlockSize.Y, v)));
        BarButton(paper, $"{id}_flip", EditorIcons.ArrowsRotate_I, "Flip", "Turn the block inside out, into a room you can stand in",
            _lastBlockFlipped, () => RebuildLastBlock(_lastBlockSize, !_lastBlockFlipped));
    }

    private static void SizeField(Paper paper, string id, string label, System.Drawing.Color color, float value, Action<float> set)
    {
        using (paper.Row(id).Width(72).Height(BarItem - 4).Margin(2, 2, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
            Origami.NumericField<float>(paper, $"{id}_v", value, set).Prefix(label, color).Min(0.01f).Format("0.###").Show();
    }

    private void ResizeLastBlock(Float3 size) => RebuildLastBlock(size, _lastBlockFlipped);

    private void RebuildLastBlock(Float3 size, bool flipped)
    {
        if (!LastBlockEditable()) return;
        EditableMesh mesh = _lastBlock!;
        Undo.RecordObject(mesh, flipped != _lastBlockFlipped ? "Flip Block" : "Resize Block");
        GeometryData box = GeometryGenerator.Box(size, new Float3(0, size.Y * 0.5f, 0));
        if (flipped) GeometryOperators.FlipFaces(box, box.Faces.ToList());
        mesh.SetGeometry(box);
        _lastBlockSize = size;
        _lastBlockFlipped = flipped;
        _lastBlockVersion = mesh.Version;
    }

    private void DrawBlockPreview()
    {
        Float3 a = _blockStart, b = _blockEnd;
        Span<Float3> quad = [new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z)];
        Draw.Polygon(quad, BlockFill);
        Draw.Polyline(quad, BlockColor, 2f, closed: true);
        Draw.LengthLabel(quad[0], quad[1], BlockColor);
        Draw.LengthLabel(quad[1], quad[2], BlockColor);

        if (_blockPhase != 2) return;
        var up = new Float3(0, _blockHeight, 0);
        Span<Float3> top = [quad[0] + up, quad[1] + up, quad[2] + up, quad[3] + up];
        Draw.Polyline(top, BlockColor, 2f, closed: true);
        for (int i = 0; i < 4; i++) Draw.Line(quad[i], top[i], BlockColor, 2f);
        Draw.LengthLabel(quad[2], top[2], BlockColor);
    }

    // Where a new block starts: on whatever surface is under the cursor, else on the ground plane.
    private static bool GroundPoint(SceneToolContext ctx, Ray ray, out Float3 point)
    {
        if (SceneViewPanel.RaycastSurface(ctx.Scene, ray) is Float3 surface) { point = surface; return true; }
        if (RaycastEditableMeshes(ctx.Scene, ray, out float d).IsValid()) { point = ray.Origin + ray.Direction * d; return true; }
        return RayPlane(ray, 0f, out point);
    }

    private static bool RayPlane(Ray ray, float height, out Float3 point)
    {
        point = default;
        if (MathF.Abs(ray.Direction.Y) < 1e-6f) return false;
        float t = (height - ray.Origin.Y) / ray.Direction.Y;
        if (t <= 0) return false;
        point = ray.Origin + ray.Direction * t;
        return true;
    }

    private static Float3 Snap(Float3 p, float step)
        => new(MathF.Round(p.X / step) * step, p.Y, MathF.Round(p.Z / step) * step);

    // ================================================================
    //  Operations
    // ================================================================

    /// <summary>The vertices the handle moves: the selection resolved down to points.</summary>
    private HashSet<Vertex> SelectedVertices()
    {
        var set = new HashSet<Vertex>();
        switch (SubTool)
        {
            case ModelingSubTool.Vertex: set.UnionWith(_verts); break;
            case ModelingSubTool.Edge: foreach (Edge e in _edges) { set.Add(e.Vert1); set.Add(e.Vert2); } break;
            case ModelingSubTool.Face: foreach (Face f in _faces) set.UnionWith(f.NeighborVertices()); break;
        }
        return set;
    }

    /// <summary>Writes the geometry back to the component and drops selected elements an operator removed.</summary>
    private void Commit()
    {
        if (_target.IsNotValid() || _geo == null) return;
        _target.SetGeometry(_geo);
        _geoVersion = _target.Version;
        _verts.RemoveWhere(v => !_geo.Contains(v));
        _edges.RemoveWhere(e => !_geo.Contains(e));
        _faces.RemoveWhere(f => !_geo.Contains(f));
    }

    private void RunOp(string name, Action<GeometryData> op)
    {
        if (_target.IsNotValid() || _geo == null) return;
        _adjust = null;
        SelectionKeys kept = CaptureSelection();
        Undo.RecordObject(_target, name);
        op(_geo);
        Commit();
        // Operators often rebuild the elements they touch (a flip remakes its faces), which empties a selection held as
        // element objects. Unless the operation chose what to select itself, the same places are selected again.
        if (SelectionCount == 0) Reselect(kept);
    }

    // What is selected, by where it is rather than by element object, so it survives an operator remaking elements.
    private sealed record SelectionKeys(HashSet<string> Faces, HashSet<string> Edges, HashSet<string> Verts);

    private SelectionKeys CaptureSelection()
        => new([.. _faces.Select(FaceKey)], [.. _edges.Select(EdgeKey)], [.. _verts.Select(v => PointKey(v.Point))]);

    private void Reselect(SelectionKeys keys)
    {
        if (_geo == null) return;
        foreach (Face f in _geo.Faces) if (keys.Faces.Contains(FaceKey(f))) _faces.Add(f);
        foreach (Edge e in _geo.Edges) if (keys.Edges.Contains(EdgeKey(e))) _edges.Add(e);
        foreach (Vertex v in _geo.Vertices) if (keys.Verts.Contains(PointKey(v.Point))) _verts.Add(v);
    }

    private static string PointKey(Float3 p) => $"{MathF.Round(p.X, 4)},{MathF.Round(p.Y, 4)},{MathF.Round(p.Z, 4)}";

    // A face by its corners in any order and either winding, so a flipped face still matches.
    private static string FaceKey(Face f) => string.Join("|", f.NeighborVertices().Select(v => PointKey(v.Point)).OrderBy(k => k, StringComparer.Ordinal));

    private static string EdgeKey(Edge e)
    {
        string a = PointKey(e.Vert1.Point), b = PointKey(e.Vert2.Point);
        return string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    /// <summary>Faces the operator just made, found as those built only from vertices that did not exist before.</summary>
    private void SelectNewFaces(HashSet<Vertex> before)
    {
        _verts.Clear();
        _edges.Clear();
        _faces.Clear();
        foreach (Face f in _geo!.Faces)
            if (f.NeighborVertices().All(v => !before.Contains(v)))
                _faces.Add(f);
    }

    private void ExtrudeFaces(float distance)
    {
        if (_faces.Count == 0 || _geo == null) return;
        var before = new HashSet<Vertex>(_geo.Vertices);
        GeometryOperators.ExtrudeFaces(_geo, _faces.ToList(), distance);
        SelectNewFaces(before);
        Commit();
    }

    // ================================================================
    //  Adjustable operations
    // ================================================================
    //  Extrude, inset and bevel stay adjustable from the bar after they run: changing the amount runs the operation
    //  again from the mesh as it was before, on the same selection. Any other edit, or a change of selection, ends it.

    private sealed class AdjustableOp
    {
        public required EditableMesh Mesh;
        public required EditableMesh.Polygons Before;
        public required int[] Faces;
        public required (Float3 A, Float3 B)[] Edges;
        public required Action<GeometryData, List<Face>, List<Edge>, float[]> Apply;
        public required AdjustParam[] Params;
        public int Version;
    }

    /// <summary>One setting of an adjustable operation, shown as a field in the bar.</summary>
    private sealed class AdjustParam(string label, float value, float min, float max, bool whole = false)
    {
        public readonly string Label = label;
        public readonly float Min = min, Max = max;
        public readonly bool Whole = whole;
        public float Value = value;
    }

    private AdjustableOp? _adjust;

    private bool AdjustValid() => _adjust != null && _target.IsValid() && ReferenceEquals(_adjust.Mesh, _target) && _adjust.Version == _target.Version;

    private void RunAdjustable(string name, Action<GeometryData, List<Face>, List<Edge>, float[]> apply, params AdjustParam[] settings)
    {
        if (_target.IsNotValid() || _geo == null) return;
        // Stored faces keep the geometry's face order, so a face's index finds it again in a copy of the mesh.
        _adjust = new AdjustableOp
        {
            Mesh = _target,
            Before = _target.CopyPolygons(),
            Faces = [.. _faces.Select(f => _geo.Faces.IndexOf(f)).Where(i => i >= 0)],
            Edges = [.. _edges.Select(e => (e.Vert1.Point, e.Vert2.Point))],
            Apply = apply,
            Params = settings,
        };
        Undo.RecordObject(_target, name);
        Reapply();
    }

    private void Reapply()
    {
        AdjustableOp op = _adjust!;
        GeometryData geo = EditableMesh.ToGeometry(op.Before);
        List<Face> faces = [.. op.Faces.Where(i => i < geo.Faces.Count).Select(i => geo.Faces[i])];
        List<Edge> edges = [.. geo.Edges.Where(e => op.Edges.Any(p => SamePoints(e, p)))];

        _geo = geo;
        _verts.Clear();
        _edges.Clear();
        _faces.Clear();
        op.Apply(geo, faces, edges, [.. op.Params.Select(p => p.Value)]);
        Commit();
        op.Version = _target!.Version;
    }

    private static bool SamePoints(Edge e, (Float3 A, Float3 B) p)
        => (e.Vert1.Point == p.A && e.Vert2.Point == p.B) || (e.Vert1.Point == p.B && e.Vert2.Point == p.A);

    private void ExtrudeApply(GeometryData g, List<Face> faces, List<Edge> edges, float[] v)
    {
        var before = new HashSet<Vertex>(g.Vertices);
        GeometryOperators.ExtrudeFaces(g, faces, v[0]);
        SelectNewFaces(before);
    }

    private void InsetApply(GeometryData g, List<Face> faces, List<Edge> edges, float[] v)
    {
        var before = new HashSet<Vertex>(g.Vertices);
        GeometryOperators.InsetFaces(g, faces, v[0]);
        SelectNewFaces(before);
    }

    // Width, segments (1 is a flat chamfer, more round it off) and profile (0.5 is round, lower concave, higher convex).
    private void BevelApply(GeometryData g, List<Face> faces, List<Edge> edges, float[] v)
    {
        var before = new HashSet<Vertex>(g.Vertices);
        GeometryOperators.BevelEdges(g, edges, v[0], (int)v[1], v[2]);
        foreach (Edge e in g.Edges)
            if (!before.Contains(e.Vert1) && !before.Contains(e.Vert2)) _edges.Add(e);
    }

    // The amount field for the operation just run, in the bar after the actions.
    private void DrawAdjust(Paper paper, string id)
    {
        AdjustableOp op = _adjust!;
        for (int i = 0; i < op.Params.Length; i++)
        {
            AdjustParam param = op.Params[i];
            BarHint(paper, $"{id}_l{i}", param.Label);
            using (paper.Row($"{id}_f{i}").Width(param.Whole ? 56 : 72).Height(BarItem - 4).Margin(0, 4, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.NumericField<float>(paper, $"{id}_v{i}", param.Value, v =>
                {
                    if (!AdjustValid()) return;
                    Undo.RecordObject(_target!, param.Label);
                    v = Math.Clamp(v, param.Min, param.Max);
                    param.Value = param.Whole ? MathF.Round(v) : v;
                    Reapply();
                }).Min(param.Min).Max(param.Max).Format(param.Whole ? "0" : "0.###").Show();
        }
    }

    private void DeleteSelected()
    {
        switch (SubTool)
        {
            case ModelingSubTool.Vertex when _verts.Count > 0:
                RunOp("Delete Vertices", g => GeometryOperators.DeleteVertices(g, _verts.ToList()));
                break;
            case ModelingSubTool.Edge when _edges.Count > 0:
                RunOp("Dissolve Edges", g => GeometryOperators.DissolveEdges(g, _edges.ToList()));
                break;
            case ModelingSubTool.Face when _faces.Count > 0:
                RunOp("Delete Faces", g => GeometryOperators.DeleteFaces(g, _faces.ToList()));
                break;
        }
        ClearSelection();
    }

    private void SetSubTool(ModelingSubTool tool)
    {
        if (SubTool == tool) return;
        SubTool = tool;
        Reset();
    }

    internal void SelectAll()
    {
        if (_geo == null) return;
        ClearSelection();
        switch (SubTool)
        {
            case ModelingSubTool.Vertex: _verts.UnionWith(_geo.Vertices); break;
            case ModelingSubTool.Edge: _edges.UnionWith(_geo.Edges); break;
            case ModelingSubTool.Face: _faces.UnionWith(_geo.Faces); break;
        }
    }

    private void FlipFaces()
    {
        List<Face> faces = [.. _faces];
        if (faces.Count == 0) return;
        RunOp("Flip Faces", g => GeometryOperators.FlipFaces(g, faces));
    }

    // A reversed face on the same points behind each face, so the surface shows from both sides and moving a point
    // moves both. (CopyFaces with flip cancels faces that land on their own reverse, so it cannot do this.)
    private void MakeDoubleSided()
    {
        List<Face> faces = [.. _faces];
        if (faces.Count == 0) return;
        RunOp("Make Double-Sided", g =>
        {
            foreach (Face f in faces)
            {
                List<Vertex> corners = f.NeighborVertices();
                corners.Reverse();
                if (g.AddFace([.. corners]) is Face back) back.Material = f.Material;
            }
        });
    }

    /// <summary>Puts the selected faces on <paramref name="slot"/>, or selects the faces on it when none are selected.</summary>
    internal void UseSlot(int slot)
    {
        if (_geo == null) return;
        if (SubTool == ModelingSubTool.Face && _faces.Count > 0)
        {
            RunOp("Assign Material", _ => { foreach (Face f in _faces) f.Material = slot; });
            return;
        }
        SetSubTool(ModelingSubTool.Face);
        foreach (Face f in _geo.Faces)
            if (f.Material == slot) _faces.Add(f);
    }

    /// <summary>A material dropped on the mesh goes on the face under the cursor, or on the selected faces when it
    /// lands on one of them.</summary>
    public override bool OnAssetDropped(SceneToolContext ctx, AssetDragPayload payload)
    {
        if (_target.IsNotValid() || _geo == null) return false;
        if (payload.AssetType == null || !typeof(Material).IsAssignableFrom(payload.AssetType)) return false;

        Face? hit = RaycastFace(ctx.Handles.MouseRay, out _);
        if (hit == null) return false;
        Material? material = AssetDatabase.Get<Material>(payload.AssetGuid);
        if (material.IsNotValid()) return false;

        Undo.RecordObject(_target, "Assign Material");
        int slot = _target.SlotFor(material);
        List<Face> faces = _faces.Contains(hit) ? [.. _faces] : [hit];
        foreach (Face f in faces) f.Material = slot;
        Commit();
        return true;
    }

    // ================================================================
    //  Inspector API
    // ================================================================
    //  What the viewport bar and the menu item use to drive the tool.

    /// <summary>The mesh being edited, null while none is.</summary>
    internal EditableMesh? Target => _target;

    /// <summary>How many elements of the current mode are selected.</summary>
    internal int SelectionCount => SubTool switch
    {
        ModelingSubTool.Vertex => _verts.Count,
        ModelingSubTool.Edge => _edges.Count,
        ModelingSubTool.Face => _faces.Count,
        _ => 0,
    };

    /// <summary>Switches the element mode, from the inspector or a shortcut. Clears the selection.</summary>
    internal static void Select(ModelingSubTool mode)
    {
        if (SceneToolManager.Get<ModelingTool>() is { } tool && SceneToolManager.IsLive(tool)) tool.SetSubTool(mode);
        else SubTool = mode;
    }

    /// <summary>Whether every selected face is on <paramref name="slot"/>, for highlighting it.</summary>
    internal bool SlotInUse(int slot) => SubTool == ModelingSubTool.Face && _faces.Count > 0 && _faces.All(f => f.Material == slot);

    /// <summary>The actions for the current mode and selection.</summary>
    internal List<(string Glyph, string Label, string Tooltip, bool Enabled, Action Run)> Actions()
    {
        bool any = SelectionCount > 0;
        float step = SceneTools.MoveSnap;
        var actions = new List<(string Glyph, string Label, string Tooltip, bool Enabled, Action Run)>();
        switch (SubTool)
        {
            case ModelingSubTool.Vertex:
                actions.Add((EditorIcons.Compress, "Merge", "Merge the selected vertices at their centre", _verts.Count > 1, () =>
                {
                    Float3 c = Float3.Zero;
                    foreach (Vertex v in _verts) c += v.Point;
                    c /= _verts.Count;
                    RunOp("Merge Vertices", g =>
                    {
                        GeometryOperators.MergeVertices(g, _verts.ToList(), c);
                        _verts.Clear();
                        if (g.Vertices.OrderBy(v => Float3.LengthSquared(v.Point - c)).FirstOrDefault() is Vertex merged) _verts.Add(merged);
                    });
                }));
                actions.Add((EditorIcons.Trash, "Delete", "Delete the selected vertices and their faces (Del)", any, DeleteSelected));
                break;

            case ModelingSubTool.Edge:
                actions.Add((EditorIcons.BezierCurve, "Bevel", "Bevel the selected edges; adjust the width in the bar after", any,
                    () => RunAdjustable("Bevel Edges", BevelApply,
                        new AdjustParam("Width", step * 0.25f, 0.001f, 1000f),
                        new AdjustParam("Segments", 1, 1, 16, whole: true),
                        new AdjustParam("Profile", 0.5f, 0f, 1f))));
                actions.Add((EditorIcons.Scissors, "Split", "Add a vertex in the middle of each selected edge", any, () =>
                    RunOp("Split Edges", g =>
                    {
                        var made = new HashSet<Vertex>(GeometryOperators.SubdivideEdges(g, _edges.ToList(), 1));
                        _edges.Clear();
                        foreach (Edge e in g.Edges)
                            if (made.Contains(e.Vert1) || made.Contains(e.Vert2)) _edges.Add(e);
                    })));
                actions.Add((EditorIcons.Trash, "Dissolve", "Remove the selected edges, joining the faces either side (Del)", any, DeleteSelected));
                break;

            case ModelingSubTool.Face:
                actions.Add((EditorIcons.ArrowUpFromBracket, "Extrude", "Pull the selected faces out; adjust the distance in the bar after, or Shift-drag the handle", any,
                    () => RunAdjustable("Extrude Faces", ExtrudeApply, new AdjustParam("Distance", step, -1000f, 1000f))));
                actions.Add((EditorIcons.Compress, "Inset", "Make a smaller face inside each selected face; adjust the amount in the bar after, or scale it with the Scale tool", any,
                    () => RunAdjustable("Inset Faces", InsetApply, new AdjustParam("Inset", 0.25f, 0.01f, 0.99f))));
                actions.Add((EditorIcons.ArrowsRotate, "Flip", "Turn the selected faces the other way. Select all (Ctrl+A) and flip to make a room", any, FlipFaces));
                actions.Add((EditorIcons.Clone, "Two-Sided", "Add a back face behind each selected face so it shows from both sides", any, MakeDoubleSided));
                actions.Add((EditorIcons.Trash, "Delete", "Delete the selected faces (Del)", any, DeleteSelected));
                break;
        }
        return actions;
    }

    // ================================================================
    //  Viewport bar
    // ================================================================
    //  Centred along the bottom of the viewport, the tool strip's horizontal sibling. In Object mode it only offers
    //  Edit Mesh while an editable mesh is selected; while Modeling it holds Block, the element modes, the actions for
    //  the selection, the material slots and collision.

    private static void Enter(ModelingSubTool mode)
    {
        SceneTools.EditMode = SceneEditMode.Modeling;
        Select(mode);
    }

    private void Exit()
    {
        Reset();
        SceneTools.EditMode = SceneEditMode.Object;
    }

    // Only while Modeling, or in Object mode while an editable mesh is selected, to offer Edit Mesh. Block is reached
    // with B or from inside Modeling, so the bar never sits in the way of ordinary scene work.
    public override bool ShowsViewportBar(SceneToolContext ctx)
        => SceneTools.EditMode == SceneEditMode.Modeling || _target.IsValid();

    public override void OnViewportBarGUI(SceneToolContext ctx, Paper paper, string id)
    {
        if (SceneTools.EditMode != SceneEditMode.Modeling)
        {
            BarButton(paper, $"{id}_edit", EditorIcons.PenToSquare_I, "Edit Mesh", "Edit this mesh's vertices, edges and faces (Tab)", false, () => Enter(ModelingSubTool.Face));
            return;
        }

        bool hasMesh = _target.IsValid() && _geo != null;
        BarIcon(paper, $"{id}_exit", EditorIcons.Xmark_I, "Done (Tab)", false, true, Exit);
        BarDivider(paper, $"{id}_d0");
        BarButton(paper, $"{id}_block", EditorIcons.Cube_I, "Block", "Draw blocks of level geometry (B)", SubTool == ModelingSubTool.Block, () => SetSubTool(ModelingSubTool.Block));
        BarDivider(paper, $"{id}_d1");
        BarIcon(paper, $"{id}_vert", EditorIcons.CircleDot_I, "Vertex (2)", SubTool == ModelingSubTool.Vertex, hasMesh, () => SetSubTool(ModelingSubTool.Vertex));
        BarIcon(paper, $"{id}_edge", EditorIcons.Slash_I, "Edge (3)", SubTool == ModelingSubTool.Edge, hasMesh, () => SetSubTool(ModelingSubTool.Edge));
        BarIcon(paper, $"{id}_face", EditorIcons.Square_I, "Face (4)", SubTool == ModelingSubTool.Face, hasMesh, () => SetSubTool(ModelingSubTool.Face));

        if (SubTool == ModelingSubTool.Block && LastBlockEditable())
        {
            BarDivider(paper, $"{id}_d2");
            DrawBlockSize(paper, $"{id}_bs");
            return;
        }
        if (SubTool == ModelingSubTool.Block || !hasMesh)
        {
            BarDivider(paper, $"{id}_d2");
            BarHint(paper, $"{id}_hint", SubTool == ModelingSubTool.Block
                ? "Drag a base on the grid, move up for height, click to place"
                : "Select an editable mesh, or draw one with Block");
            return;
        }

        var actions = Actions().Where(a => a.Enabled).ToList();
        BarDivider(paper, $"{id}_d2");
        if (actions.Count == 0)
            BarHint(paper, $"{id}_hint", $"Click {(SubTool == ModelingSubTool.Vertex ? "vertices" : SubTool == ModelingSubTool.Edge ? "edges" : "faces")} to select them. Shift adds, Ctrl toggles");
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            BarButton(paper, $"{id}_a{i}", a.Glyph, a.Label, a.Tooltip, false, a.Run);
        }
        if (AdjustValid())
        {
            BarDivider(paper, $"{id}_dadj");
            DrawAdjust(paper, $"{id}_adj");
        }

        BarDivider(paper, $"{id}_d3");
        EditableMesh target = _target!;
        int slots = Math.Max(1, target.Materials.Count);
        for (int i = 0; i < slots; i++) BarSwatch(paper, $"{id}_sw{i}", target, i);
        BarIcon(paper, $"{id}_swadd", EditorIcons.Plus_I, "Add a material. The selected faces go on it.", false, true, () => PickMaterial(m =>
        {
            if (m.IsNotValid()) return;
            Undo.RecordObject(target, "Add Material");
            UseSlot(target.SlotFor(m));
        }));
    }

    private const float BarItem = 28f;

    private static void BarButton(Paper paper, string id, OrigamiUI.IOrigamiIcon icon, string label, string tooltip, bool active, Action onClick)
    {
        ButtonBuilder b = Origami.Button(paper, id, label, onClick).LeadingIcon(icon).Small().Height(BarItem).Tooltip(tooltip);
        if (active) b.Primary(); else b.Ghost();
        b.Show();
    }

    private static void BarButton(Paper paper, string id, string glyph, string label, string tooltip, bool active, Action onClick)
    {
        ButtonBuilder b = Origami.Button(paper, id, label, onClick).LeadingIcon(glyph).Small().Height(BarItem).Tooltip(tooltip);
        if (active) b.Primary(); else b.Ghost();
        b.Show();
    }

    private static void BarIcon(Paper paper, string id, OrigamiUI.IOrigamiIcon icon, string tooltip, bool active, bool enabled, Action onClick)
    {
        ButtonBuilder b = Origami.IconButton(paper, id, icon, onClick).Small().Width(BarItem).Height(BarItem).Tooltip(tooltip).Disabled(!enabled);
        if (active) b.Primary(); else b.Ghost();
        b.Show();
    }

    private static void BarDivider(Paper paper, string id)
        => paper.Box(id).Width(1).Height(18).Margin(4, 4, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

    private static void BarHint(Paper paper, string id, string text)
        => paper.Box(id).Width(UnitValue.Auto).Height(BarItem).Padding(8, 8, 0, 0)
            .Text(text, EditorTheme.DefaultFont!).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

    // A material slot as a small square: its thumbnail, its base colour without one, or the orange checker for the
    // prototype. Click puts the selected faces on it, or selects its faces; right click changes it.
    private void BarSwatch(Paper paper, string id, EditableMesh target, int slot)
    {
        Material? assigned = slot < target.Materials.Count ? target.Materials[slot] : null;
        bool prototype = assigned.IsNotValid();
        string name = prototype ? "Prototype" : assigned!.Name;
        Texture2D? thumbnail = !prototype && assigned!.IsFromDatabase ? EditorAssetBackend.Instance?.GetThumbnailTexture(assigned.AssetID) : null;
        Color32 tint = SwatchColor(target.ResolveSlot(slot));
        bool inUse = SlotInUse(slot);
        float r = Origami.Current.Metrics.SmallRounding;

        paper.Box(id).Size(22, 22).Margin(3, 3, UnitValue.StretchOne, UnitValue.StretchOne).Rounded(r + 1)
            .BorderColor(inUse ? EditorTheme.Accent : EditorTheme.BorderSoft).BorderWidth(inUse ? 2 : 1)
            .Hovered.BorderColor(EditorTheme.AccentBright).End()
            .Cursor(PaperCursor.Pointer)
            .Tooltip($"{slot}: {name}. Click to apply to the selected faces, or select its faces. Right click to change.")
            .OnClick(_ => UseSlot(slot))
            .OnRightClick(_ => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
            {
                b.Header($"{slot}: {name}");
                b.Item("Change Material...", () => PickMaterial(m => SetSlot(target, slot, m)));
                b.Item("Use Prototype", () => SetSlot(target, slot, null), enabled: !prototype);
            }))
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, rr) =>
            {
                float x = (float)rr.Min.X + 2, y = (float)rr.Min.Y + 2, w = (float)rr.Size.X - 4, h = (float)rr.Size.Y - 4;
                if (thumbnail.IsValid()) { canvas.DrawImageRounded(thumbnail, x, y, w, h, r); return; }
                canvas.RoundedRectFilled(x, y, w, h, r, tint);
                if (!prototype) return;
                var dark = Color32.FromArgb(255, (byte)(tint.R * 0.45f), (byte)(tint.G * 0.45f), (byte)(tint.B * 0.45f));
                canvas.RoundedRectFilled(x + w / 2, y, w / 2, h / 2, 0, r, 0, 0, dark);
                canvas.RoundedRectFilled(x, y + h / 2, w / 2, h / 2, 0, 0, 0, r, dark);
            }));
    }

    private static void SetSlot(EditableMesh mesh, int slot, Material? material)
    {
        Undo.RecordObject(mesh, material.IsValid() ? "Change Material" : "Clear Material");
        var list = mesh.Materials.ToList();
        while (list.Count <= slot) list.Add(null!);
        list[slot] = material!;
        mesh.Materials = list;
    }

    private static void PickMaterial(Action<Material?> picked)
        => SelectorModal.Open("Select Material", typeof(Material), SelectorTabs.Assets, o => picked(o as Material));

    private static Color32 SwatchColor(Material material)
    {
        Color c = material.GetColor("base_color");
        return Color32.FromArgb(255, (byte)Math.Clamp(c.R * 255f, 0f, 255f), (byte)Math.Clamp(c.G * 255f, 0f, 255f), (byte)Math.Clamp(c.B * 255f, 0f, 255f));
    }

    public override void OnDrawOverlay(SceneToolContext ctx, Quill.Canvas canvas) => TransformHandles.Draw(canvas);

    // ================================================================
    //  Drawing
    // ================================================================

    private void DrawMesh(HandleContext ctx)
    {
        if (_target.IsNotValid() || _geo == null) return;
        Float4x4 l2w = _target.Transform.LocalToWorldMatrix;
        Float3 eye = ctx.Camera.GameObject.Transform.Position;

        foreach (Face f in _faces) DrawFace(f, l2w, SelectedFill, SelectedColor);
        if (_hoverFace != null && !_faces.Contains(_hoverFace)) DrawFace(_hoverFace, l2w, HoverFill, HoverColor);

        foreach (Edge e in _geo.Edges)
        {
            Color32 color = _edges.Contains(e) ? SelectedColor
                : e == _hoverEdge ? HoverColor
                : IsVisible(e, l2w, eye) ? WireColor : HiddenWireColor;
            float thickness = color.Equals(WireColor) || color.Equals(HiddenWireColor) ? 1.5f : 3f;
            Draw.Line(Float4x4.TransformPoint(e.Vert1.Point, l2w), Float4x4.TransformPoint(e.Vert2.Point, l2w), color, thickness);
        }

        if (SubTool != ModelingSubTool.Vertex) return;
        foreach (Vertex v in _geo.Vertices)
        {
            bool selected = _verts.Contains(v);
            if (!selected && v != _hoverVert && !IsVisible(v, l2w, eye)) continue;
            Color32 color = selected ? SelectedColor : v == _hoverVert ? HoverColor : WireColor;
            Draw.Dot(Float4x4.TransformPoint(v.Point, l2w), color, selected || v == _hoverVert ? 9f : 6f);
        }
    }

    private void DrawFace(Face f, Float4x4 l2w, Color32 fill, Color32 outline)
    {
        List<Vertex> verts = f.NeighborVertices();
        Span<Float3> points = verts.Count <= 64 ? stackalloc Float3[verts.Count] : new Float3[verts.Count];
        for (int i = 0; i < verts.Count; i++) points[i] = Float4x4.TransformPoint(verts[i].Point, l2w);
        Draw.Polygon(points, fill);
        Draw.Polyline(points, outline, 2f, closed: true);
    }

    // ================================================================
    //  Picking helpers
    // ================================================================

    private static bool FacesCamera(Face f, Float4x4 l2w, Float3 eye)
    {
        Float3 center = Float4x4.TransformPoint(f.Center(), l2w);
        Float3 normal = Float4x4.TransformNormal(f.Normal(), l2w);
        return Float3.Dot(normal, eye - center) > 0f;
    }

    // An edge or vertex is visible when a face it borders turns toward the camera, or when it borders no face.
    private static bool IsVisible(Edge e, Float4x4 l2w, Float3 eye)
    {
        List<Face> faces = e.NeighborFaces();
        return faces.Count == 0 || faces.Any(f => FacesCamera(f, l2w, eye));
    }

    private static bool IsVisible(Vertex v, Float4x4 l2w, Float3 eye)
    {
        List<Face> faces = v.NeighborFaces();
        return faces.Count == 0 || faces.Any(f => FacesCamera(f, l2w, eye));
    }

    private static float ScreenDistanceToSegment(HandleContext ctx, Float3 a, Float3 b)
    {
        Float2? sa = ctx.WorldToScreen(a), sb = ctx.WorldToScreen(b);
        if (sa is null || sb is null) return float.MaxValue;
        Float2 p = ctx.MousePosition, ab = sb.Value - sa.Value;
        float lengthSq = Float2.Dot(ab, ab);
        float t = lengthSq > 1e-6f ? Math.Clamp(Float2.Dot(p - sa.Value, ab) / lengthSq, 0f, 1f) : 0f;
        Float2 closest = sa.Value + ab * t;
        return Float2.Distance(p, closest);
    }

    /// <summary>The front-most face of the target under a world ray, with the hit distance along it.</summary>
    private Face? RaycastFace(Ray worldRay, out float distance)
    {
        distance = float.MaxValue;
        if (_target.IsNotValid() || _geo == null) return null;

        Float4x4 l2w = _target.Transform.LocalToWorldMatrix;
        Face? best = null;
        foreach (Face f in _geo.Faces)
        {
            List<Vertex> verts = f.NeighborVertices();
            if (verts.Count < 3) continue;
            // Back faces are skipped: a double-sided face picks the side you see, and the walls of a flipped room
            // pick through the near side from outside.
            if (Float3.Dot(Float4x4.TransformNormal(f.Normal(), l2w), worldRay.Direction) >= 0f) continue;
            Float3 a = Float4x4.TransformPoint(verts[0].Point, l2w);
            for (int i = 1; i + 1 < verts.Count; i++)
            {
                Float3 b = Float4x4.TransformPoint(verts[i].Point, l2w), c = Float4x4.TransformPoint(verts[i + 1].Point, l2w);
                if (worldRay.Intersects(new Triangle(a, b, c), out float t, out _, out _) && t > 0f && t < distance)
                {
                    distance = t;
                    best = f;
                }
            }
        }
        return best;
    }

    private static EditableMesh? RaycastEditableMeshes(Scene scene, Ray ray, out float distance)
    {
        distance = float.MaxValue;
        EditableMesh? best = null;
        foreach (GameObject go in scene.Objects)
        {
            if (!go.EnabledInHierarchy || go.HideFlags.HasFlag(HideFlags.Hide)) continue;
            EditableMesh? mesh = go.GetComponent<EditableMesh>();
            if (mesh.IsValid() && mesh.EnabledInHierarchy && mesh.Raycast(ray, out float d) && d < distance)
            {
                distance = d;
                best = mesh;
            }
        }
        return best;
    }
}
