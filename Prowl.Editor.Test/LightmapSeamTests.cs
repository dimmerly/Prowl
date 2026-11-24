// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Lightmapping;
using Prowl.Vector;

using Xunit;

namespace Prowl.Editor.Test;

public class LightmapSeamTests
{
    private const int Size = 32;

    // A unit quad in the XZ plane at x0, facing up, as two triangles, with its corners' chart UVs given
    private static LightmapChart Quad(float x0, Float2 uv00, Float2 uv10, Float2 uv11, Float2 uv01, Float3? up = null)
    {
        Float3 n = up ?? Float3.UnitY;
        Float3 a = new(x0, 0, 0), b = new(x0 + 1, 0, 0), c = new(x0 + 1, 0, 1), d = new(x0, 0, 1);
        return new LightmapChart
        {
            Positions = [a, b, c, a, c, d],
            Normals = [n, n, n, n, n, n],
            UVs = [uv00, uv10, uv11, uv00, uv11, uv01],
            Width = Size, Height = Size,
        };
    }

    private static readonly Float2 s_00 = new(0, 0), s_10 = new(1, 0), s_11 = new(1, 1), s_01 = new(0, 1);

    // Two quads side by side in the world, sharing the edge x = 1, each in its own half of one page
    private static (LightmapChart Left, LightmapChart Right) SideBySide()
    {
        LightmapChart left = Quad(0, s_00, s_10, s_11, s_01);
        left.Width = 16; left.Height = 16; left.X = 0; left.Y = 0;
        LightmapChart right = Quad(1, s_00, s_10, s_11, s_01);
        right.Width = 16; right.Height = 16; right.X = 16; right.Y = 16;
        return (left, right);
    }

    [Fact]
    public void AnEdgeSharedInTheWorld_ButNotInUV_IsASeam()
    {
        (LightmapChart left, LightmapChart right) = SideBySide();

        SeamEdge seam = Assert.Single(LightmapSeams.Find([left, right], Size));

        // The left quad's right edge, x = 16 on the page, against the right quad's left edge, x = 16 too but 16 higher
        Assert.Equal(16f, seam.A0.X, 3);
        Assert.Equal(16f, seam.A1.X, 3);
        Assert.Equal(16f, seam.B0.X, 3);
        Assert.Equal(seam.A0.Y + 16f, seam.B0.Y, 3);
        Assert.Equal(seam.A1.Y + 16f, seam.B1.Y, 3);
    }

    [Fact]
    public void TrianglesSharingTheirUVs_AndHardEdges_AreNoSeams()
    {
        // One quad's own diagonal is shared in UV as well
        Assert.Empty(LightmapSeams.Find([Quad(0, s_00, s_10, s_11, s_01)], Size));

        // A fold between two faces at right angles shows its edge anyway
        (LightmapChart left, LightmapChart right) = SideBySide();
        right.Normals = [Float3.UnitX, Float3.UnitX, Float3.UnitX, Float3.UnitX, Float3.UnitX, Float3.UnitX];
        Assert.Empty(LightmapSeams.Find([left, right], Size));
    }

    [Fact]
    public void Stitching_MakesBothSidesOfASeamRead_TheSame()
    {
        (LightmapChart left, LightmapChart right) = SideBySide();
        List<SeamEdge> seams = LightmapSeams.Find([left, right], Size);

        // Left half dark, right half bright, so the seam starts out as a hard step
        var page = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                page[y * Size + x] = y >= 16 ? 1f : 0.2f;

        float Gap() => seams.Max(s => MathF.Abs(Sample(page, (s.A0 + s.A1) * 0.5f) - Sample(page, (s.B0 + s.B1) * 0.5f)));
        float before = Gap();
        LightmapSeams.Stitch([page], 1, Size, seams);

        Assert.True(before > 0.5f, $"Expected a step to start with, was {before}.");
        Assert.True(Gap() < 0.05f, $"The seam still differs by {Gap()}.");
        // Away from the seam nothing moves
        Assert.Equal(0.2f, page[4 * Size + 4], 5);
        Assert.Equal(1f, page[28 * Size + 28], 5);
    }

    // Bilinear between texel centres, clamped at the page edge, as the GPU samples
    private static float Sample(float[] page, Float2 p)
    {
        float x = p.X - 0.5f, y = p.Y - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float At(int px, int py) => page[Math.Clamp(py, 0, Size - 1) * Size + Math.Clamp(px, 0, Size - 1)];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    [Fact]
    public void UVsMappingTwoPlacesToOneTexel_AreMarkedAsOverlapping()
    {
        // Two quads far apart in the world, both over the whole chart
        LightmapChart overlapping = Quad(0, s_00, s_10, s_11, s_01);
        LightmapChart far = Quad(5, s_00, s_10, s_11, s_01);
        overlapping.Positions = [.. overlapping.Positions, .. far.Positions];
        overlapping.Normals = [.. overlapping.Normals, .. far.Normals];
        overlapping.UVs = [.. overlapping.UVs, .. far.UVs];
        var grid = new TexelGrid(Size);
        LightmapAtlas.Rasterize(overlapping, 0, grid);
        Assert.True(grid.Overlap.Count(o => o) > Size * Size / 2);

        // One quad's two triangles meet along a diagonal, which is no overlap
        var clean = new TexelGrid(Size);
        LightmapAtlas.Rasterize(Quad(0, s_00, s_10, s_11, s_01), 0, clean);
        Assert.DoesNotContain(true, clean.Overlap);
    }

    [Fact]
    public void ADirectionPage_EncodesTheVector_AndTheHalfLambertAtTheBakedNormal()
    {
        // Straight up, all of it, on an upward surface; then a texel with no direction at all
        float[] direction = [0f, 1f, 0f, 1f, 0f, 0f, 0f, 0.5f];
        byte[] bytes = LightmapComposer.DirectionBytes(direction, 1).Concat(LightmapComposer.DirectionBytes(direction[4..], 1)).ToArray();

        Assert.Equal([128, 255, 128, 255], bytes[..4]);
        Assert.Equal([128, 128, 128, 128], bytes[4..]);

        // Graphite's turn: half Lambert at the shading normal over the stored one, 1 at the baked normal
        static float Turn(byte[] t, Float3 n) => (Float3.Dot(n, new Float3(t[0], t[1], t[2]) / 255f - new Float3(0.5f)) + 0.5f) / (t[3] / 255f);
        Assert.Equal(1f, Turn(bytes[..4], Float3.UnitY), 2);
        Assert.Equal(0.5f, Turn(bytes[..4], Float3.UnitX), 2);
        Assert.Equal(1f, Turn(bytes[4..], Float3.UnitX), 2);   // no direction turns nothing
    }
}
