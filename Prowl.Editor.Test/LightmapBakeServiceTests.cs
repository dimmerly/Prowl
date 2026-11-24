// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Lightmapping;
using Prowl.Graphite.ShaderDef;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Xunit;

namespace Prowl.Editor.Test;

public class LightmapBakeServiceTests
{
    private static T NewLight<T>(float intensity) where T : Light, new()
    {
        T light = new GameObject("Light").AddComponent<T>();
        light.UseTemperature = false;
        light.Color = Color.White;
        light.Intensity = intensity;
        return light;
    }

    [Fact]
    public void BakeColor_IsTheLightsLuxOrCandelaOverPi()
    {
        Assert.Equal(100000f / MathF.PI, LightmapBakeService.BakeColor(NewLight<DirectionalLight>(100000f)).X, 2);
        Assert.Equal(70f / MathF.PI, LightmapBakeService.BakeColor(NewLight<PointLight>(70f)).Y, 4);

        RectLight rect = NewLight<RectLight>(300f);
        rect.Width = 2f;
        rect.Height = 0.5f;
        Assert.Equal(300f / MathF.PI, LightmapBakeService.BakeColor(rect).Z, 3);
    }

    [Fact]
    public void ALight_PacksAsSixFloat4s_WithItsTypeRangeAndCone()
    {
        SpotLight spot = NewLight<SpotLight>(500f);
        spot.Range = 12f;
        spot.SpotAngle = 60f;
        spot.SpotSoftness = 0.5f;
        spot.BakeMode = LightBakeMode.Mixed;
        spot.Transform.Position = new Float3(1f, 2f, 3f);

        var packed = new List<float>();
        LightmapBakeService.AddLight(packed, spot);

        Assert.Equal(LightmapperShader.LightStride * 4, packed.Count);
        Assert.Equal((1f, 2f, 3f, (float)LightmapperShader.LightSpot), (packed[0], packed[1], packed[2], packed[3]));
        Assert.Equal(12f, packed[7]);
        Assert.Equal((float)LightmapperShader.LightCastsShadows, packed[11]);   // a mixed light bakes only its bounce
        Assert.Equal(MathF.Cos(30f * MathF.PI / 180f), packed[12], 4);
        Assert.Equal(MathF.Cos(15f * MathF.PI / 180f), packed[13], 4);
    }

    [Fact]
    public void ARectLight_PacksItsHalfExtentsAlongItsAxes()
    {
        RectLight rect = NewLight<RectLight>(1000f);
        rect.Width = 2f;
        rect.Height = 0.5f;

        var packed = new List<float>();
        LightmapBakeService.AddLight(packed, rect);

        Assert.Equal((float)LightmapperShader.LightRect, packed[3]);
        Assert.Equal(1f, Float3.Length(new Float3(packed[16], packed[17], packed[18])), 4);
        Assert.Equal(0.25f, Float3.Length(new Float3(packed[20], packed[21], packed[22])), 4);
        Assert.Equal(1f, packed[15]);
    }

    [Fact]
    public void APage_RoundTripsThroughExr_AsHalfFloats()
    {
        const int size = 4;
        var rgb = new float[size * size * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = i * 37.5f + 0.001f * i;
        string path = Path.Combine(Path.GetTempPath(), $"lightmap-{Guid.NewGuid():N}.exr");
        try
        {
            LightmapBakeService.WriteExr(path, rgb, size);
            using Aperture.Image image = Aperture.Image.Load(path, new Aperture.DecodeOptions { TargetPixelFormat = Aperture.PixelFormat.RgbaF16, FlipVertically = true });
            Assert.Equal((size, size), (image.Width, image.Height));
            ReadOnlySpan<ushort> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.RootFrame.Pixels);
            for (int p = 0; p < size * size; p++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float expected = (float)(Half)rgb[p * 3 + c];
                    Assert.Equal(expected, (float)BitConverter.UInt16BitsToHalf(halves[p * 4 + c]));
                }
                Assert.Equal(1f, (float)BitConverter.UInt16BitsToHalf(halves[p * 4 + 3]));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheDenoiser_FlattensNoiseOnOneSurface_ButNotAcrossCharts()
    {
        const int size = 16;
        var guide = new DenoiseGuide(size);
        var rgb = new float[size * size * 3];
        var variance = new float[size * size];
        var rng = new Random(3);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                guide.Valid[i] = true;
                guide.Radius[i] = 0.05f;
                guide.Normal[i] = Float3.UnitY;
                // Two charts side by side in the atlas, far apart in the world and differently lit
                bool right = x >= size / 2;
                guide.Chart[i] = right ? 1 : 0;
                guide.Position[i] = new Float3(x * 0.1f + (right ? 100f : 0f), 0f, y * 0.1f);
                float light = (right ? 10f : 1f) * (0.5f + rng.NextSingle());
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = light;
                variance[i] = (right ? 10f : 1f) * 0.1f;
            }
        }

        LightmapDenoiser.Run(rgb, variance, guide, 4);

        float min = float.MaxValue, max = 0f;
        for (int y = 2; y < size - 2; y++)
        {
            for (int x = 2; x < size / 2 - 2; x++)
            {
                float v = rgb[(y * size + x) * 3];
                min = MathF.Min(min, v); max = MathF.Max(max, v);
            }
        }
        Assert.True(max - min < 0.5f, $"Left chart still spans {min}..{max}");
        Assert.True(max < 2f, "Light bled in from the other chart");
    }

    [Fact]
    public void Dilation_GrowsLightIntoEmptyPixels_OnePixelAPass()
    {
        const int size = 5;
        var rgb = new float[size * size * 3];
        var covered = new bool[size * size];
        int centre = 2 * size + 2;
        rgb[centre * 3] = 1f;
        covered[centre] = true;

        LightmapBakeService.Dilate(rgb, covered, size, 1);

        Assert.True(covered[centre + 1]);
        Assert.Equal(1f, rgb[(centre + 1) * 3]);
        Assert.False(covered[centre + 2]);
    }

    [Fact]
    public void TheBvh_HoldsEveryTriangleOnce_InsideItsLeafBounds()
    {
        var rng = new Random(7);
        var corners = new List<Float3>();
        for (int i = 0; i < 300; i++)
        {
            var at = new Float3(rng.NextSingle() * 20f, rng.NextSingle() * 20f, rng.NextSingle() * 20f);
            corners.AddRange([at, at + new Float3(rng.NextSingle(), 0f, 0f), at + new Float3(0f, rng.NextSingle(), 0.1f)]);
        }

        LightmapBvh bvh = LightmapBvh.Build(corners);

        Assert.Equal(Enumerable.Range(0, 300), bvh.Order.Order());
        var seen = new HashSet<int>();
        for (int n = 0; n < bvh.NodeCount; n++)
        {
            int count = (int)bvh.Links[n * 2 + 1];
            if (count == 0) continue;
            int first = (int)bvh.Links[n * 2];
            var min = new Float3(bvh.Bounds[n * 8], bvh.Bounds[n * 8 + 1], bvh.Bounds[n * 8 + 2]);
            var max = new Float3(bvh.Bounds[n * 8 + 4], bvh.Bounds[n * 8 + 5], bvh.Bounds[n * 8 + 6]);
            for (int i = first; i < first + count; i++)
            {
                Assert.True(seen.Add(i));
                for (int k = 0; k < 3; k++)
                {
                    Float3 p = corners[bvh.Order[i] * 3 + k];
                    Assert.True(p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z);
                }
            }
        }
        Assert.Equal(300, seen.Count);
    }

    [Fact]
    public void AChart_IsSizedByTexelDensity_AndRasterizesToWorldPositions()
    {
        // A two metre square of floor, at four texels a metre, fills an eight texel chart
        var chart = new LightmapChart
        {
            Positions = [new(0, 0, 0), new(2, 0, 0), new(2, 0, 2), new(0, 0, 0), new(2, 0, 2), new(0, 0, 2)],
            Normals = [.. Enumerable.Repeat(Float3.UnitY, 6)],
            UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 0), new(1, 1), new(0, 1)],
        };
        Assert.Equal(1, LightmapAtlas.Pack([chart], 32, 4f, 2));
        Assert.Equal((8, 2, 2), (chart.Width, chart.X, chart.Y));

        var grid = new TexelGrid(32);
        LightmapAtlas.Rasterize(chart, 0, grid);

        Assert.Equal(64, grid.Covered.Count(c => c));
        int texel = (2 + 3) * 32 + (2 + 5);   // row 3, column 5 of the chart
        Assert.True(grid.Covered[texel]);
        // Within the small pull toward the triangle's centre
        Assert.InRange(grid.Position[texel].X, 5.5f / 4f - 0.05f, 5.5f / 4f + 0.05f);
        Assert.InRange(grid.Position[texel].Z, 3.5f / 4f - 0.05f, 3.5f / 4f + 0.05f);
        Assert.Equal(1f, grid.Normal[texel].Y, 4);
        // A texel's step is a quarter metre, the floor's size over its eight texels
        Assert.Equal(0.25f, grid.StepX[texel].X, 4);
        Assert.Equal(0.25f, grid.StepY[texel].Z, 4);
    }

    [Theory]
    [InlineData("Validate")]
    [InlineData("Trace")]
    [InlineData("Probe")]
    public void EveryKernel_Compiles(string kernel)
    {
        ComputeShader shader = ComputeShader.FromSource("Lightmapper", LightmapperShader.Source);
        var pass = new ShaderPass { State = new PassState(), Source = shader.KernelSource(shader.FindKernel(kernel)), SourceName = $"Lightmapper/{kernel}" };
        Graphics.Compiler.CompileProgram(pass, []);
    }
}
