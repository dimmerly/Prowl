// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Graphite.Rendering;

namespace Prowl.Runtime.Rendering;

/// <summary>What a view shows in place of the lit scene: one part of its GBuffer. Views on the forward path show the lit scene whatever the mode.</summary>
public enum DebugViewMode
{
    Lit,
    BaseColor,
    Metalness,
    Normals,
    Roughness,
    Specular,
    Occlusion,
    ShadingModel,
    Custom,
    Baked,
    Emission,
    Lighting,
    ShadowCascades,
    LightmapDensity,
    /// <summary>The baked light alone on a white surface, directional lightmaps turned by the normal.</summary>
    BakedLighting,
    /// <summary>Lightmap texels as stored, neither exposed nor turned: what a bake wrote, and the bake's overlays.</summary>
    LightmapTexels,
    Motion,
    Depth,
}

public static class DebugViews
{
    private static readonly string[] s_values = ["OFF", "UNLIT", "ALBEDO", "NORMALS", "ROUGHNESS", "METALLIC", "AO", "EMISSION", "LIGHTING", "CASCADES", "LIGHTMAP_DENSITY", "BAKED", "LIGHTMAP", "MOTION", "DEPTH"];
    private static readonly GlobalKeyword[]?[] s_keywords = new GlobalKeyword[]?[s_values.Length];
    private static Renderer? s_renderer;
    private static GBufferDebug? s_debug;
    private static readonly List<RenderFeature> s_list = [];
    private static IReadOnlyList<RenderFeature>? s_with;

    /// <summary>
    /// The extra features of a render showing a mode, after the ones given: the same list back for the lit scene. One
    /// render at a time shows a mode, as the scene view does.
    /// </summary>
    public static IReadOnlyList<RenderFeature>? Features(DebugViewMode mode, IReadOnlyList<RenderFeature>? with = null)
    {
        if (mode == DebugViewMode.Lit || Graphics.Renderer is not { } renderer) return with;
        if (s_renderer != renderer || s_debug == null)
        {
            s_renderer = renderer;
            s_debug = new GBufferDebug(renderer);
            s_with = null;
            s_list.Clear();
        }
        s_debug.Mode = (GBufferDebugMode)((int)mode - 1);
        if (s_list.Count == 0 || !ReferenceEquals(s_with, with))
        {
            s_with = with;
            s_list.Clear();
            if (with != null) s_list.AddRange(with);
            s_list.Add(s_debug);
        }
        return s_list;
    }
}
