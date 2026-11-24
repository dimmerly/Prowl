// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading;

using Prowl.Editor.Lightmapping;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Vector;

using Xunit;

using S = Prowl.Editor.Lightmapping.LightmapperShader;

namespace Prowl.Editor.Test;

/// <summary>
/// A test that traces on the GPU. Skipped unless PROWL_GPU_TESTS=1, since opening a device would change every other
/// test in the same process, which all run headless. Run these on their own:
/// <c>PROWL_GPU_TESTS=1 dotnet test ... --filter FullyQualifiedName~LightmapReferenceTests</c>.
/// </summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PROWL_GPU_TESTS") != "1")
            Skip = "Set PROWL_GPU_TESTS=1 to run the lightmapper on a GPU.";
    }
}

/// <summary>
/// The lightmapper against answers known in closed form: a sun and a point, spot and rect light over a plane, a closed
/// box of glowing walls (the furnace test, which catches any energy lost or gained over bounces), a uniform sky over a
/// plane and around a probe, and the light's direction. If one of these drifts, every bake is wrong by the same amount.
/// </summary>
public class LightmapReferenceTests
{
    public LightmapReferenceTests()
    {
        if (Environment.GetEnvironmentVariable("PROWL_GPU_TESTS") == "1") Graphics.InitializeOffscreen();
    }

    // ---- scene building ----

    private static int Material(LightmapperInput input, Float3 albedo, Float3 emission = default)
    {
        int index = input.MaterialData.Count / (S.MaterialStride * 4);
        input.MaterialData.AddRange([albedo.X, albedo.Y, albedo.Z, -1f, emission.X, emission.Y, emission.Z, 1f, 1, 1, 0, 0, 1, 1, 0, 0]);
        input.Maps.AddRange([0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u]);
        return index;
    }

    // A rectangle around centre spanning +-u and +-v, facing normal
    private static void Quad(LightmapperInput input, Float3 centre, Float3 u, Float3 v, Float3 normal, int material)
    {
        Float3 a = centre - u - v, b = centre + u - v, c = centre + u + v, d = centre - u + v;
        foreach ((Float3 p, Float3 q, Float3 r) in new[] { (a, b, c), (a, c, d) })
        {
            input.Positions.AddRange([p, q, r]);
            input.Normals.AddRange([normal, normal, normal]);
            input.UVs.AddRange([default, default, default]);
            input.Materials.Add(material);
        }
    }

    // A point sample: no footprint to jitter over
    private static void Texel(LightmapperInput input, Float3 position, Float3 normal)
        => input.Texels.AddRange([position.X, position.Y, position.Z, 1e-4f, normal.X, normal.Y, normal.Z, S.TexelClean, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static void Light(LightmapperInput input, int type, Float3 position, Float3 forward, float range, Float3 color,
        float cosOuter = -1f, float cosInner = -1f, float radius = 0f, Float3 axisX = default, Float3 axisY = default)
    {
        int flags = S.LightBakesDirect | S.LightCastsShadows;
        input.Lights.AddRange([position.X, position.Y, position.Z, type, forward.X, forward.Y, forward.Z, range,
            color.X, color.Y, color.Z, flags, cosOuter, cosInner, radius, 1f,
            axisX.X, axisX.Y, axisX.Z, 0f, axisY.X, axisY.Y, axisY.Z, 0f]);
    }

    // A big floor at y = 0 facing up
    private static LightmapperInput Floor(int bounces = 0)
    {
        var input = new LightmapperInput { Bounces = bounces };
        Quad(input, Float3.Zero, new Float3(100, 0, 0), new Float3(0, 0, 100), Float3.UnitY, Material(input, new Float3(0.5f)));
        return input;
    }

    private static LightmapperResult Trace(LightmapperInput input, int samples)
    {
        using var gpu = new GpuLightmapper(LightmapperScene.Build(input), samples);
        gpu.Validate();
        while (!gpu.TraceDone)
        {
            gpu.Advance();
            Thread.Yield();
        }
        while (!gpu.Idle) Thread.Yield();
        return gpu.Read();
    }

    private static Float3 At(float[] values, int texel) => new(values[texel * 3], values[texel * 3 + 1], values[texel * 3 + 2]);

    private static void Near(float expected, float actual, float relative, string what)
        => Assert.True(MathF.Abs(actual - expected) <= relative * MathF.Abs(expected) + 1e-6f,
            $"{what}: expected {expected}, traced {actual} ({(actual / expected - 1f) * 100f:F2}% off, {relative * 100f:F1}% allowed).");

    // ---- direct light ----

    [GpuFact]
    public void ASun_LightsAPlane_WithItsLuxTimesTheCosineOverPi_FromItsDirection()
    {
        LightmapperInput input = Floor();
        const float Lux = 1000f;
        float angle = 30f * MathF.PI / 180f;
        Float3 forward = new(MathF.Sin(angle), -MathF.Cos(angle), 0f);
        Light(input, S.LightDirectional, Float3.Zero, forward, 0f, new Float3(Lux / MathF.PI), radius: 1f);   // cos of a zero angular radius
        Texel(input, new Float3(0, 0, 0), Float3.UnitY);
        Texel(input, new Float3(3, 0, -2), Float3.UnitY);

        LightmapperResult result = Trace(input, 8);

        float expected = Lux / MathF.PI * MathF.Cos(angle);
        for (int t = 0; t < 2; t++)
        {
            Near(expected, At(result.Direct, t).X, 1e-3f, $"texel {t}");
            Float3 toward = At(result.Direction, t);
            Assert.True(Float3.Dot(toward, -forward) > 0.999f, $"The light arrives from {toward}, not {-forward}.");
        }
    }

    // Graphite's window: inverse square, faded smoothly to nothing at the range
    private static float Window(float distance2, float range)
    {
        float factor = distance2 / (range * range);
        float w = Math.Clamp(1f - factor * factor, 0f, 1f);
        return w * w;
    }

    [GpuFact]
    public void APointLight_FallsOffAsInverseSquareTimesGraphitesWindow()
    {
        LightmapperInput input = Floor();
        const float Candela = 100f, Range = 6f;
        var light = new Float3(0, 2, 0);
        Light(input, S.LightPoint, light, -Float3.UnitY, Range, new Float3(Candela / MathF.PI));
        float[] xs = [0f, 1f, 3f, 5f];
        foreach (float x in xs) Texel(input, new Float3(x, 0, 0), Float3.UnitY);

        LightmapperResult result = Trace(input, 8);

        for (int t = 0; t < xs.Length; t++)
        {
            float d2 = xs[t] * xs[t] + 4f;
            float expected = Candela / MathF.PI * (2f / MathF.Sqrt(d2)) / d2 * Window(d2, Range);
            Near(expected, At(result.Direct, t).Y, 1e-3f, $"x = {xs[t]}");
        }
    }

    [GpuFact]
    public void ASpotLight_ShadesItsConeWithGraphitesSmoothstep()
    {
        LightmapperInput input = Floor();
        const float Candela = 100f;
        float cosOuter = MathF.Cos(40f * MathF.PI / 180f), cosInner = MathF.Cos(25f * MathF.PI / 180f);
        Light(input, S.LightSpot, new Float3(0, 2, 0), -Float3.UnitY, 0f, new Float3(Candela / MathF.PI), cosOuter, cosInner);
        float[] xs = [0f, 1.2f, 1.5f, 3f];   // inside, in the penumbra twice, outside
        foreach (float x in xs) Texel(input, new Float3(x, 0, 0), Float3.UnitY);

        LightmapperResult result = Trace(input, 8);

        for (int t = 0; t < xs.Length; t++)
        {
            float d2 = xs[t] * xs[t] + 4f, cos = 2f / MathF.Sqrt(d2);
            float cone = cos <= cosOuter ? 0f : SmoothStep(cosOuter, cosInner, cos);
            float expected = Candela / MathF.PI * cos / d2 * cone;
            if (expected == 0f) Assert.Equal(0f, At(result.Direct, t).X);
            else Near(expected, At(result.Direct, t).X, 1e-3f, $"x = {xs[t]}");
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    [GpuFact]
    public void ARectLight_GivesTheIrradianceOfItsPolygon()
    {
        LightmapperInput input = Floor();
        const float Luminance = 2000f;   // nits
        Float3 centre = new(0, 2, 0), axisX = new(0.5f, 0, 0), axisY = new(0, 0, 0.25f);
        float area = 4f * Float3.Length(axisX) * Float3.Length(axisY);
        // Its axis candela is its luminance times its area, and it bakes with that over pi
        Light(input, S.LightRect, centre, -Float3.UnitY, 0f, new Float3(Luminance * area / MathF.PI), axisX: axisX, axisY: axisY);
        Float3[] points = [new(0, 0, 0), new(1.5f, 0, 0.5f), new(-2f, 0, 3f)];
        foreach (Float3 p in points) Texel(input, p, Float3.UnitY);

        LightmapperResult result = Trace(input, 4096);

        Float3[] corners = [centre - axisX - axisY, centre + axisX - axisY, centre + axisX + axisY, centre - axisX + axisY];
        for (int t = 0; t < points.Length; t++)
        {
            float expected = Luminance * PolygonFormFactor(points[t], Float3.UnitY, corners);
            Near(expected, At(result.Direct, t).Z, 0.02f, $"at {points[t]}");
        }
    }

    // Lambert's form factor from a point on a surface to a polygon: irradiance over pi per unit radiance
    private static float PolygonFormFactor(Float3 p, Float3 n, Float3[] corners)
    {
        float sum = 0f;
        for (int i = 0; i < corners.Length; i++)
        {
            Float3 a = Float3.Normalize(corners[i] - p), b = Float3.Normalize(corners[(i + 1) % corners.Length] - p);
            float angle = MathF.Acos(Math.Clamp(Float3.Dot(a, b), -1f, 1f));
            sum += angle * Float3.Dot(n, Float3.Normalize(Float3.Cross(a, b)));
        }
        return MathF.Abs(sum) / (2f * MathF.PI);
    }

    // ---- indirect light ----

    [GpuFact]
    public void AClosedBoxOfGlowingWalls_HoldsTheGeometricSeriesOfItsBounces()
    {
        // Every wall emits Le and reflects albedo a, all facing in. The light arriving anywhere is Le (1 + a + a^2 ...),
        // cut at the bounce count: nothing lost, nothing made up, whatever the paths do on the way.
        const float Albedo = 0.5f, Emission = 1f;
        const int Bounces = 24;
        var input = new LightmapperInput { Bounces = Bounces };
        int wall = Material(input, new Float3(Albedo), new Float3(Emission));
        Quad(input, new Float3(0, -1, 0), new Float3(1, 0, 0), new Float3(0, 0, 1), Float3.UnitY, wall);
        Quad(input, new Float3(0, 1, 0), new Float3(1, 0, 0), new Float3(0, 0, 1), -Float3.UnitY, wall);
        Quad(input, new Float3(-1, 0, 0), new Float3(0, 1, 0), new Float3(0, 0, 1), Float3.UnitX, wall);
        Quad(input, new Float3(1, 0, 0), new Float3(0, 1, 0), new Float3(0, 0, 1), -Float3.UnitX, wall);
        Quad(input, new Float3(0, 0, -1), new Float3(1, 0, 0), new Float3(0, 1, 0), Float3.UnitZ, wall);
        Quad(input, new Float3(0, 0, 1), new Float3(1, 0, 0), new Float3(0, 1, 0), -Float3.UnitZ, wall);
        Texel(input, new Float3(0, -1, 0), Float3.UnitY);
        Texel(input, new Float3(0.6f, 0, 1), -Float3.UnitZ);

        LightmapperResult result = Trace(input, 2048);

        float expected = Emission * (1f - MathF.Pow(Albedo, Bounces)) / (1f - Albedo);
        Near(expected, At(result.Indirect, 0).X, 0.02f, "floor");
        Near(expected, At(result.Indirect, 1).Y, 0.02f, "wall");
    }

    private static float[] UniformSky(Float3 radiance)
    {
        var sky = new float[S.SkyWidth * S.SkyHeight * 4];
        for (int i = 0; i < S.SkyWidth * S.SkyHeight; i++)
        {
            sky[i * 4] = radiance.X; sky[i * 4 + 1] = radiance.Y; sky[i * 4 + 2] = radiance.Z; sky[i * 4 + 3] = 1f;
        }
        return sky;
    }

    [GpuFact]
    public void AUniformSky_LightsAnOpenPlane_WithItsRadiance()
    {
        Float3 sky = new(0.3f, 0.5f, 0.7f);
        LightmapperInput input = Floor(bounces: 1);
        input.Sky = UniformSky(sky);
        Texel(input, Float3.Zero, Float3.UnitY);

        LightmapperResult result = Trace(input, 64);

        Float3 traced = At(result.Indirect, 0);
        Near(sky.X, traced.X, 1e-3f, "red");
        Near(sky.Y, traced.Y, 1e-3f, "green");
        Near(sky.Z, traced.Z, 1e-3f, "blue");
    }

    [GpuFact]
    public void AProbeInAUniformSky_GivesItsRadianceForEveryNormal()
    {
        Float3 sky = new(0.3f, 0.5f, 0.7f);
        var input = new LightmapperInput { Bounces = 1, Sky = UniformSky(sky) };
        // Something to build the BVH over, far out of the way
        Quad(input, new Float3(1000, 1000, 1000), new Float3(0.01f, 0, 0), new Float3(0, 0, 0.01f), Float3.UnitY, Material(input, new Float3(0.5f)));
        Texel(input, new Float3(1000, 1000, 1000), Float3.UnitY);

        using var gpu = new GpuLightmapper(LightmapperScene.Build(input), 1);
        gpu.BeginProbes([Float3.Zero], 8192);
        while (!gpu.AdvanceProbes()) Thread.Yield();
        SphericalHarmonicsL2 sh = Assert.Single(gpu.ReadProbes());

        foreach (Float3 n in new[] { Float3.UnitY, -Float3.UnitY, Float3.UnitX, Float3.Normalize(new Float3(1, 1, -1)) })
            Near(sky.Y, sh.Evaluate(n).Y, 0.04f, $"normal {n}");
    }
}
