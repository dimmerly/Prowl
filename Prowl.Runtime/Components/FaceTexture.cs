// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>Which axes a face's texture is projected along.</summary>
public enum TextureAlignment
{
    /// <summary>The world axes nearest the face, so walls and floors of separate meshes line up where they meet.</summary>
    World,
    /// <summary>The face's own plane, so a sloped face shows the texture without stretching.</summary>
    Face,
}

/// <summary>
/// How an <see cref="EditableMesh"/> face lays its material, the way Hammer's face edit does: a projection along
/// world or face axes, turned, scaled and shifted. Texture coordinates come from world positions, at
/// <see cref="UnitsPerTile"/> world units per texture repeat when the scale is 1.
/// </summary>
public readonly record struct FaceTexture(Float2 Offset, Float2 Scale, float Rotation, TextureAlignment Alignment)
{
    /// <summary>World units one repeat of the texture covers at scale 1. The prototype checker has two squares a
    /// tile, so that is one square per unit and blocks read against the grid.</summary>
    public const float UnitsPerTile = 2f;

    /// <summary>How many floats <see cref="Write"/> stores.</summary>
    public const int Stride = 6;

    /// <summary>World aligned, unturned, at scale 1 and no shift.</summary>
    public static FaceTexture Default => new(Float2.Zero, Float2.One, 0f, TextureAlignment.World);

    /// <summary>Reads a mapping. A zero scale, as a face made with nothing to copy from has, reads as 1.</summary>
    public static FaceTexture Read(ReadOnlySpan<float> data)
    {
        if (data.Length < Stride) return Default;
        float su = Clean(data[2]), sv = Clean(data[3]);
        return new FaceTexture(
            new Float2(Finite(data[0]), Finite(data[1])),
            new Float2(su, sv),
            Finite(data[4]),
            data[5] >= 0.5f ? TextureAlignment.Face : TextureAlignment.World);

        static float Finite(float v) => float.IsFinite(v) ? v : 0f;
        static float Clean(float v) => float.IsFinite(v) && MathF.Abs(v) > 1e-6f ? v : 1f;
    }

    /// <summary>Writes the mapping as <see cref="Stride"/> floats.</summary>
    public void Write(Span<float> data)
    {
        data[0] = Offset.X;
        data[1] = Offset.Y;
        data[2] = Scale.X;
        data[3] = Scale.Y;
        data[4] = Rotation;
        data[5] = Alignment == TextureAlignment.Face ? 1f : 0f;
    }

    /// <summary>
    /// The world directions U and V run along on a face with <paramref name="normal"/>, turned by
    /// <see cref="Rotation"/>, unscaled. U runs to the right and V up as the face is seen from the front, so a
    /// texture never shows mirrored.
    /// </summary>
    public (Float3 U, Float3 V) Axes(Float3 normal)
    {
        WorldAxes(normal, out Float3 u, out Float3 v);
        // Face aligned: up across the face's plane, and right of that seen from the front. A level face has no up
        // across it, and its world axes lie in it already.
        if (Alignment == TextureAlignment.Face && MathF.Abs(normal.Y) < 0.999f)
        {
            v = Float3.Normalize(Float3.UnitY - normal * normal.Y);
            u = Float3.Normalize(Float3.Cross(v, normal));
        }

        if (Rotation != 0f)
        {
            float r = Rotation * (MathF.PI / 180f);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            (u, v) = (u * c + v * s, v * c - u * s);
        }
        return (u, v);
    }

    /// <summary>The texture coordinates of a world point on a face whose axes are <paramref name="u"/> and <paramref name="v"/>.</summary>
    public Float2 Map(Float3 world, Float3 u, Float3 v)
        => new(Float3.Dot(world, u) / (Scale.X * UnitsPerTile) + Offset.X,
               Float3.Dot(world, v) / (Scale.Y * UnitsPerTile) + Offset.Y);

    /// <summary>
    /// The world axes a face projects along when world aligned, picked by the normal's largest component as Hammer
    /// does: walls run along the ground with V up, floors and ceilings run along X with V along Z.
    /// </summary>
    public static void WorldAxes(Float3 normal, out Float3 u, out Float3 v)
    {
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);
        if (ay >= ax && ay >= az)
        {
            u = Float3.UnitX;
            v = normal.Y >= 0f ? -Float3.UnitZ : Float3.UnitZ;
            return;
        }
        v = Float3.UnitY;
        // Right of the face seen from the front: up crossed with the outward axis.
        u = ax >= az
            ? (normal.X >= 0f ? -Float3.UnitZ : Float3.UnitZ)
            : (normal.Z >= 0f ? Float3.UnitX : -Float3.UnitX);
    }
}
