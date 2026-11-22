// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Graphite.Rendering;
using Prowl.Runtime.AssetImporting;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Vector.Geometry;

using GMaterial = Prowl.Graphite.Rendering.Material;
using Material = Prowl.Runtime.Resources.Material;

namespace Prowl.Runtime;

/// <summary>
/// A polygon mesh edited in the scene view (the editor's Modeling mode), stored as points and polygon faces rather than
/// triangles so edges and n-gons survive editing. <see cref="ToGeometry"/> hands it to the
/// <see cref="GeometryData"/> operators and <see cref="SetGeometry"/> takes the result back.
/// <para>Each face picks one of <see cref="Materials"/> by index. A slot that is empty, or an index past the list,
/// draws with <see cref="PrototypeMaterial"/>, an orange checker sized to one square per world unit for blocking out.</para>
/// <para>For collision, add a <see cref="MeshCollider"/> with no mesh assigned: it builds from this mesh and is rebuilt
/// whenever the polygons change.</para>
/// </summary>
[AddComponentMenu("Rendering/Editable Mesh")]
[ComponentIcon("")] // Cube
public class EditableMesh : Component, IRenderSync, IMaterialRenderer
{
    [SerializeField, HideInInspector] private List<Float3> _points = [];
    // Faces as a flat list of point indices, _faceSizes[i] of them per face, in winding order (counter-clockwise
    // seen from the front, the engine's front face).
    [SerializeField, HideInInspector] private List<int> _faceIndices = [];
    [SerializeField, HideInInspector] private List<int> _faceSizes = [];
    // The material slot of each face.
    [SerializeField, HideInInspector] private List<int> _faceMaterials = [];
    [SerializeField] private List<Material> _materials = [];
    [SerializeField] private ShadowCasting _shadowCasting = ShadowCasting.On;

    [SerializeIgnore] private Mesh? _mesh;
    [SerializeIgnore] private bool _dirty = true;
    [SerializeIgnore] private int _version;
    // The material slot of each submesh of the built mesh.
    [SerializeIgnore] private int[] _subMeshSlots = [];
    // Whether the built mesh has its lightmap UVs yet. They are unwrapped only when something needs them, not per edit.
    [SerializeIgnore] private bool _hasLightmapUV;
    [System.NonSerialized] private BakedLightingBinding.Cache _lightmapCache;
    [System.NonSerialized, NotHeld] private readonly RenderableState _renderable = new();
    [System.NonSerialized, NotHeld] private GMaterial[] _gpuMaterials = [];
    [System.NonSerialized, NotHeld] private readonly List<Material?> _resolved = [];

    [HeldStatic] private static Material? s_prototype;

    /// <summary>The material an empty slot draws with: the standard material tinted orange.</summary>
    public static Material PrototypeMaterial
    {
        get
        {
            if (s_prototype.IsNotValid())
            {
                s_prototype = new Material(BuiltInAssets.Load<Material>(BuiltInAssets.GuidFor(DefaultMaterial.Standard))) { Name = "Prototype" };
                s_prototype.SetColor("base_color", new Color(1f, 0.56f, 0.24f, 1f));
            }
            return s_prototype;
        }
    }

    /// <summary>Moves on every change to the polygon data, including an undo restoring it, so an editor holding
    /// its own <see cref="GeometryData"/> knows to rebuild it.</summary>
    public int Version => _version;

    public int PointCount => _points.Count;
    public int FaceCount => _faceSizes.Count;

    /// <summary>Material slots that faces index into. Set a new list to change them.</summary>
    public IReadOnlyList<Material> Materials
    {
        get => _materials;
        set { _materials = [.. value]; MarkChanged(); }
    }

    public ShadowCasting ShadowCasting
    {
        get => _shadowCasting;
        set { _shadowCasting = value; MarkChanged(); }
    }

    /// <summary>Adds a slot, empty when <paramref name="material"/> is null, and returns its index. Slot 0 is what every
    /// face starts on, so a mesh with no slots gets an empty slot 0 first and the new material does not repaint it all.</summary>
    public int AddMaterialSlot(Material? material = null)
    {
        if (_materials.Count == 0) _materials.Add(null!);
        _materials.Add(material!);
        MarkChanged();
        return _materials.Count - 1;
    }

    /// <summary>The slot holding <paramref name="material"/>, adding one when no slot does.</summary>
    public int SlotFor(Material material)
    {
        int found = _materials.IndexOf(material);
        return found >= 0 ? found : AddMaterialSlot(material);
    }

    int IMaterialRenderer.MaterialSlotCount => _materials.Count;
    Material? IMaterialRenderer.GetMaterial(int slot) => MaterialSlots.Get(_materials, slot);
    void IMaterialRenderer.SetMaterial(int slot, Material? material) { MaterialSlots.Set(_materials, slot, material); MarkChanged(); }

    /// <summary>The render mesh, rebuilt from the polygons when they changed. Null while there are no faces.</summary>
    public Mesh? BuiltMesh
    {
        get
        {
            if (_dirty || _mesh.IsNotValid()) Rebuild();
            return _faceSizes.Count > 0 ? _mesh : null;
        }
    }

    /// <summary>
    /// <see cref="BuiltMesh"/> with a lightmap UV set (UV2) unwrapped onto it, for baking and for drawing with a baked
    /// lightmap. The box projected UVs repeat and overlap, so they cannot hold a lightmap. Unwrapped once per change to the
    /// polygons, and the unwrap is deterministic, so a bake and a later load agree on where each face lands.
    /// </summary>
    public Mesh? LightmapMesh
    {
        get
        {
            Mesh? mesh = BuiltMesh;
            if (mesh.IsValid() && !_hasLightmapUV)
            {
                LightmapUVGenerator.Generate(mesh);
                _hasLightmapUV = true;
            }
            return mesh;
        }
    }

    /// <summary>The material each submesh of <see cref="BuiltMesh"/> draws with, empty slots resolved to the prototype.</summary>
    public IReadOnlyList<Material> SubMeshMaterials
    {
        get
        {
            _ = BuiltMesh;
            var list = new List<Material>(_subMeshSlots.Length);
            foreach (int slot in _subMeshSlots) list.Add(ResolveSlot(slot));
            return list;
        }
    }

    /// <summary>
    /// Every polygon edge once, in local space: the outlines of the faces without the diagonals their triangulation
    /// adds. For gizmos that show the mesh's shape. Rebuilt only after the polygons change.
    /// </summary>
    public IReadOnlyList<(Float3 A, Float3 B)> PolygonEdges
    {
        get
        {
            if (_edgesVersion == _version && _edges != null) return _edges;
            var seen = new HashSet<(int, int)>();
            var edges = new List<(Float3, Float3)>();
            int at = 0;
            foreach (int size in _faceSizes)
            {
                for (int k = 0; k < size && at + size <= _faceIndices.Count; k++)
                {
                    int a = _faceIndices[at + k], b = _faceIndices[at + (k + 1) % size];
                    if ((uint)a >= (uint)_points.Count || (uint)b >= (uint)_points.Count) continue;
                    if (seen.Add(a < b ? (a, b) : (b, a))) edges.Add((_points[a], _points[b]));
                }
                at += size;
            }
            _edges = edges;
            _edgesVersion = _version;
            return edges;
        }
    }

    [SerializeIgnore] private List<(Float3, Float3)>? _edges;
    [SerializeIgnore] private int _edgesVersion = -1;

    /// <summary>A copy of the stored polygons, cheap to keep and to turn back into geometry, for an editor that
    /// re-runs an operation from the state before it.</summary>
    public readonly record struct Polygons(Float3[] Points, int[] FaceIndices, int[] FaceSizes, int[] FaceMaterials);

    /// <summary>Copies the polygons as they are now.</summary>
    public Polygons CopyPolygons() => new([.. _points], [.. _faceIndices], [.. _faceSizes], [.. _faceMaterials]);

    /// <summary>The polygons as editable geometry, with each face's slot in <see cref="GeometryData.Face.Material"/>. A new object every call.</summary>
    public GeometryData ToGeometry() => ToGeometry(_points, _faceIndices, _faceSizes, _faceMaterials);

    /// <summary><paramref name="polygons"/> as editable geometry, faces in the same order as they were stored.</summary>
    public static GeometryData ToGeometry(Polygons polygons)
        => ToGeometry(polygons.Points, polygons.FaceIndices, polygons.FaceSizes, polygons.FaceMaterials);

    private static GeometryData ToGeometry(IReadOnlyList<Float3> points, IReadOnlyList<int> faceIndices, IReadOnlyList<int> faceSizes, IReadOnlyList<int> faceMaterials)
    {
        var geometry = new GeometryData();
        // Declared so ToRenderMesh splits by slot, and so operators carry it onto the faces they make.
        geometry.AddFaceAttribute(GeometryData.MaterialAttribute, GeometryData.AttributeBaseType.Int, 1);
        var verts = new GeometryData.Vertex[points.Count];
        for (int i = 0; i < points.Count; i++) verts[i] = geometry.AddVertex(points[i]);

        int at = 0;
        for (int f = 0; f < faceSizes.Count; f++)
        {
            int size = faceSizes[f];
            if (size >= 3 && at + size <= faceIndices.Count)
            {
                var corners = new GeometryData.Vertex[size];
                bool valid = true;
                for (int k = 0; k < size; k++)
                {
                    int index = faceIndices[at + k];
                    if ((uint)index >= (uint)verts.Length) { valid = false; break; }
                    corners[k] = verts[index];
                }
                if (valid && geometry.AddFace(corners) is GeometryData.Face face)
                {
                    int slot = f < faceMaterials.Count ? faceMaterials[f] : 0;
                    if (slot != 0) face.Material = slot;
                }
            }
            at += size;
        }
        return geometry;
    }

    /// <summary>Replaces the polygons with <paramref name="geometry"/>'s faces and their slots. Points no face uses are dropped.</summary>
    public void SetGeometry(GeometryData geometry)
    {
        _points.Clear();
        _faceIndices.Clear();
        _faceSizes.Clear();
        _faceMaterials.Clear();

        var indexOf = new Dictionary<GeometryData.Vertex, int>();
        foreach (GeometryData.Face face in geometry.Faces)
        {
            List<GeometryData.Vertex> verts = face.NeighborVertices();
            if (verts.Count < 3) continue;
            foreach (GeometryData.Vertex v in verts)
            {
                if (!indexOf.TryGetValue(v, out int index))
                {
                    index = indexOf[v] = _points.Count;
                    _points.Add(v.Point);
                }
                _faceIndices.Add(index);
            }
            _faceSizes.Add(verts.Count);
            _faceMaterials.Add(Math.Max(0, face.Material));
        }
        MarkChanged();
    }

    public override void OnValidate()
    {
        base.OnValidate();
        MarkChanged();
    }

    public override void OnDisable()
    {
        if (_mesh.IsValid()) _mesh.Dispose();
        _mesh = null;
        _dirty = true;
    }

    private void MarkChanged()
    {
        _dirty = true;
        _version++;
        if (!IsInWorld) return;
        SceneRendering.Wake(this, GameObject);

        // A MeshCollider beside this builds from BuiltMesh, so it is rebuilt with the polygons. Small level meshes
        // re-bake in well under a millisecond, which keeps collision right even while a drag edits them every frame.
        MeshCollider? collider = GetComponent<MeshCollider>();
        if (collider.IsValid() && collider.EnabledInHierarchy && collider.Mesh.IsNotValid()) collider.Rebuild();
    }

    /// <summary>Raycast against the polygons in world space.</summary>
    public bool Raycast(Ray worldRay, out float distance)
    {
        distance = float.MaxValue;
        Mesh? mesh = BuiltMesh;
        if (mesh.IsNotValid()) return false;

        Float4x4 worldToLocal = Transform.WorldToLocalMatrix;
        Float3 origin = Float4x4.TransformPoint(worldRay.Origin, worldToLocal);
        Float3 direction = Float3.Normalize(Float4x4.TransformPoint(worldRay.Origin + worldRay.Direction, worldToLocal) - origin);
        var ray = new Ray(origin, direction);
        if (!ray.Intersects(mesh.bounds, out _, out _)) return false;
        if (!mesh.Raycast(ray, out float local)) return false;

        Float3 hit = Float4x4.TransformPoint(origin + direction * local, Transform.LocalToWorldMatrix);
        distance = Float3.Distance(worldRay.Origin, hit);
        return true;
    }

    private void Rebuild()
    {
        _dirty = false;
        _hasLightmapUV = false;
        if (_mesh.IsNotValid()) _mesh = new Mesh { Name = "Editable Mesh" };
        _mesh.Clear();
        _mesh.SetSubMeshCount(0);
        _subMeshSlots = [];
        if (_faceSizes.Count == 0) return;

        // Flat shaded: every face is sharp unless flagged smooth, so faces get their own corners.
        Prowl.Vector.Geometry.RenderMesh built = ToGeometry().ToRenderMesh();
        if (built.Indices.Length == 0) return;

        var uvs = new Float2[built.Positions.Length];
        for (int i = 0; i < uvs.Length; i++) uvs[i] = BoxProject(built.Positions[i], built.Normals[i]);

        _mesh.Vertices = built.Positions;
        _mesh.Normals = built.Normals;
        _mesh.UV = uvs;
        _mesh.Indices = built.Indices;

        // One submesh per slot in use, in rising slot order.
        _subMeshSlots = new int[built.SubMeshes.Length];
        for (int s = 0; s < built.SubMeshes.Length; s++)
        {
            (int slot, int start, int count) = built.SubMeshes[s];
            _subMeshSlots[s] = slot;
            _mesh.SetSubMesh(s, new SubMeshDescriptor(start, count));
        }

        _mesh.RecalculateBounds();
        _mesh.RecalculateTangents();
    }

    // Projected along the normal's dominant axis, at half a texture tile per world unit: the default checker has two
    // squares a tile, so that is one square per unit and blocks read against the grid.
    private static Float2 BoxProject(Float3 p, Float3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        Float2 uv = ax >= ay && ax >= az ? new Float2(p.Z, p.Y) : ay >= az ? new Float2(p.X, p.Z) : new Float2(p.X, p.Y);
        return uv * 0.5f;
    }

    /// <summary>The material a slot draws with: its own, or the prototype material when it has none.</summary>
    public Material ResolveSlot(int slot)
    {
        Material? material = slot >= 0 && slot < _materials.Count ? _materials[slot] : null;
        return material.IsValid() ? material : PrototypeMaterial;
    }

    Mesh? IRenderSync.SyncedMesh => _mesh;

    bool IRenderSync.Sync(RenderWorld world)
    {
        Mesh? mesh = BuiltMesh;
        if (mesh.IsNotValid() || _subMeshSlots.Length == 0)
        {
            _renderable.Detach(world);
            return false;
        }

        _resolved.Clear();
        foreach (int slot in _subMeshSlots) _resolved.Add(ResolveSlot(slot));
        foreach (Material? material in _resolved)
        {
            if (material!.State == AssetState.Unloaded) _ = material.LoadAsync();
            if (!material.IsLoaded)
            {
                _renderable.Detach(world);
                return false;
            }
        }
        // Only an object with a baked lightmap needs the unwrapped UVs.
        LightmapBinding lightmap = BakedLightingBinding.For(GameObject, ref _lightmapCache);
        if (lightmap.Lightmapped) mesh = LightmapMesh!;
        mesh.Upload();

        _renderable.Sync(world, new RenderableState.Desc
        {
            Mesh = mesh.GpuIfLoaded,
            Materials = RenderableState.MaterialsFor(_resolved, mesh.SubMeshCount, ref _gpuMaterials),
            Transform = (Double4x4)Transform.LocalToWorldMatrix,
            TransformVersion = Transform.WorldVersion,
            Flags = RenderableState.FlagsFor(GameObject, false, true) | _shadowCasting.ToFlags(),
            LayerMask = RenderableState.LayerBit(GameObject.LayerIndex),
            ObjectId = (uint)InstanceID,
            Lightmap = lightmap,
        });
        return RenderableState.MaterialsReady(_resolved) && !(lightmap.Lightmapped && lightmap.Texture == null);
    }

    void IRenderSync.Detach(RenderWorld world) => _renderable.Detach(world);
}
