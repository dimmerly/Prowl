// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Resources;
using Prowl.Vector;

using GLightmapBinding = Prowl.Graphite.Rendering.LightmapBinding;

namespace Prowl.Runtime.Rendering;

/// <summary>Where a renderer's surface landed in its scene's baked lightmaps.</summary>
internal static class BakedLightingBinding
{
    /// <summary>A renderer's resolved placement and the lighting version it came from.</summary>
    internal struct Cache
    {
        public SceneLighting? Lighting;
        public int Version;
        public LightmapPlacement? Placement;
    }

    /// <summary>
    /// The placement a renderer uses and the lighting it is in. Resolved from its section's lighting under its identifier,
    /// and again only when that lighting changes, so a renderer that moves sections or gets a fresh identifier keeps it.
    /// </summary>
    internal static (SceneLighting? Lighting, LightmapPlacement? Placement) Resolve(GameObject renderer, ref Cache cache)
    {
        SceneLighting? current = renderer.IsValid() ? renderer.Section?.Lighting : null;
        bool stale = cache.Lighting == null || cache.Lighting.Version != cache.Version
            || (!ReferenceEquals(cache.Lighting, current) && cache.Placement == null);
        if (stale && current != null)
            cache = new Cache { Lighting = current, Version = current.Version, Placement = current.PlacementFor(renderer.Identifier) };
        return (cache.Lighting, cache.Placement);
    }

    /// <summary>
    /// The lightmap a renderer samples, from the placement its scene's bake recorded under its identifier. None for a
    /// renderer the bake did not place, which Graphite lights with the scene's light probes instead. A page still
    /// loading binds no texture, which lights the renderer with ambient light until it arrives.
    /// </summary>
    public static GLightmapBinding For(GameObject renderer, ref Cache cache)
    {
        (SceneLighting? lighting, LightmapPlacement? placement) = Resolve(renderer, ref cache);
        IReadOnlyList<Texture2D>? pages = lighting?.DrawnLightmaps;
        if (lighting == null || pages == null || placement is not { } p || p.Index < 0 || p.Index >= pages.Count)
            return GLightmapBinding.None;

        Texture2D? lightmap = pages[p.Index];
        if (lightmap.IsNotValid()) return GLightmapBinding.None;
        if (lightmap.State == AssetState.Unloaded) _ = lightmap.LoadAsync();
        // A float page holds the light as baked, an 8 bit one is RGBM from an older bake
        Graphite.Rendering.LightmapEncoding encoding = lightmap.IsLoaded && lightmap.Format is TextureFormat.RGBA16Float or TextureFormat.RGBA32Float
            ? Graphite.Rendering.LightmapEncoding.Linear : Graphite.Rendering.LightmapEncoding.RGBM;
        // A direction page lights the normal map; until it has loaded the page lights without it
        Graphite.Texture? direction = null;
        IReadOnlyList<Texture2D> directions = lighting.DrawnDirections;
        if (p.Index < directions.Count && directions[p.Index] is { } dirPage && dirPage.IsValid())
        {
            if (dirPage.State == AssetState.Unloaded) _ = dirPage.LoadAsync();
            if (dirPage.IsLoaded) direction = dirPage.GpuIfLoaded;
        }
        return new GLightmapBinding(lightmap.IsLoaded ? lightmap.GpuIfLoaded : null, p.ScaleOffset, encoding, scale: lighting.DrawnLightmapScale) { Direction = direction };
    }
}
