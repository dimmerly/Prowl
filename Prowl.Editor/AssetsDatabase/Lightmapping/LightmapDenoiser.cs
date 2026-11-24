// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading.Tasks;

using Prowl.Vector;

namespace Prowl.Editor.Lightmapping;

/// <summary>One page's texels as the denoiser sees them: what they hold and the surface each one is on.</summary>
internal sealed class DenoiseGuide(int size)
{
    public readonly int Size = size;
    public readonly bool[] Valid = new bool[size * size];
    public readonly Float3[] Position = new Float3[size * size];
    public readonly Float3[] Normal = new Float3[size * size];
    public readonly float[] Radius = new float[size * size];     // half the texel's world size
    public readonly int[] Chart = new int[size * size];
}

/// <summary>
/// An edge avoiding a-trous wavelet filter (Dammertz et al. 2010) over the indirect light of one page. Indirect light
/// changes slowly across a surface, so a wide average clears the speckle while the geometry keeps it on one surface: a
/// tap on another chart, facing more than about 25 degrees away, off the texel's plane, or further away in the world than
/// in the atlas (an unrelated chart packed alongside) counts for nothing. On top, as in SVGF, a tap's light has to sit
/// within the noise the texel's sample variance predicts, which keeps contact darkening. Each pass doubles the step, so
/// n passes reach about 4 (2^n - 1) texels. Direct light is left alone, its shadow edges are real detail.
/// </summary>
internal static class LightmapDenoiser
{
    private static readonly float[] s_kernel = [1f / 16f, 1f / 4f, 3f / 8f, 1f / 4f, 1f / 16f];

    private const float MinFacing = 0.9f;
    // A tap one step away covers about one texel of surface; UV stretch can make it more, so allow some slack
    private const float StretchAllowance = 1.75f;
    private const float LuminanceSigma = 4f;

    /// <summary>
    /// Filters <paramref name="rgb"/> (three floats a pixel) in place, with <paramref name="variance"/> the noise of each
    /// pixel's mean. Without variance only the geometry guides it, for values that aren't light, such as directions.
    /// </summary>
    public static void Run(float[] rgb, float[]? variance, DenoiseGuide guide, int passes = 5)
    {
        int size = guide.Size;
        bool guided = variance != null;
        var src = (float[])rgb.Clone();
        var dst = new float[rgb.Length];
        var varSrc = variance != null ? (float[])variance.Clone() : new float[size * size];
        var varDst = new float[varSrc.Length];

        for (int pass = 0; pass < passes; pass++)
        {
            int step = 1 << pass;
            float[] from = src, to = dst, vFrom = varSrc, vTo = varDst;
            Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (!guide.Valid[i])
                    {
                        to[i * 3] = from[i * 3]; to[i * 3 + 1] = from[i * 3 + 1]; to[i * 3 + 2] = from[i * 3 + 2];
                        vTo[i] = vFrom[i];
                        continue;
                    }

                    Float3 p = guide.Position[i], n = guide.Normal[i];
                    float texel = MathF.Max(guide.Radius[i] * 2f, 1e-5f);
                    float planeTolerance = texel * 1.5f;
                    int chart = guide.Chart[i];
                    float lum = Luminance(from, i);
                    float lumScale = guided ? 1f / (LuminanceSigma * MathF.Sqrt(MathF.Max(vFrom[i], 0f)) + 1e-4f) : 0f;

                    float r = 0, g = 0, b = 0, sum = 0, varSum = 0;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int qy = y + dy * step;
                        if (qy < 0 || qy >= size) continue;
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int qx = x + dx * step;
                            if (qx < 0 || qx >= size) continue;
                            int q = qy * size + qx;
                            if (!guide.Valid[q] || guide.Chart[q] != chart) continue;

                            float facing = Float3.Dot(n, guide.Normal[q]);
                            if (facing < MinFacing) continue;
                            Float3 d = guide.Position[q] - p;
                            float reach = texel * step * StretchAllowance * MathF.Sqrt(dx * dx + dy * dy) + planeTolerance;
                            if (Float3.LengthSquared(d) > reach * reach) continue;
                            if (MathF.Abs(Float3.Dot(n, d)) > planeTolerance) continue;
                            float w = s_kernel[dx + 2] * s_kernel[dy + 2] * facing * facing;
                            if (guided) w *= MathF.Exp(-MathF.Abs(Luminance(from, q) - lum) * lumScale);
                            if (w <= 1e-8f) continue;
                            r += from[q * 3] * w; g += from[q * 3 + 1] * w; b += from[q * 3 + 2] * w;
                            sum += w;
                            varSum += w * w * vFrom[q];
                        }
                    }

                    // The texel's own tap always counts, so sum is never zero
                    to[i * 3] = r / sum; to[i * 3 + 1] = g / sum; to[i * 3 + 2] = b / sum;
                    vTo[i] = varSum / (sum * sum);
                }
            });
            (src, dst) = (dst, src);
            (varSrc, varDst) = (varDst, varSrc);
        }
        Array.Copy(src, rgb, rgb.Length);
    }

    private static float Luminance(float[] rgb, int i) => 0.2126f * rgb[i * 3] + 0.7152f * rgb[i * 3 + 1] + 0.0722f * rgb[i * 3 + 2];
}
