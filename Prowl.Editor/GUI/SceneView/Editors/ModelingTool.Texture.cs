// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

using Face = Prowl.Vector.Geometry.GeometryData.Face;
using Vertex = Prowl.Vector.Geometry.GeometryData.Vertex;

namespace Prowl.Editor.GUI.SceneView.Editors;

// Texture alignment for the selected faces, Hammer's face edit in the Modeling bar: world or face aligned, shift,
// scale and turn, fit, and justify to an edge or the centre, each face on its own or the selection as one.
public sealed partial class ModelingTool
{
    // Whether Face mode's bar shows the texture controls instead of the operations.
    private bool _textureMode;

    // Fit and justify measure the whole selection as one surface, so a texture runs on across neighbouring faces,
    // rather than each face on its own.
    private static bool s_justifyAsOne = true;

    private enum TextureJustify { Fit, Left, Right, Top, Bottom, Center }

    private void DrawTexturePanel(Paper paper, string id)
    {
        FaceTexture t = EditableMesh.GetFaceTexture(_faces.First());

        BarIcon(paper, $"{id}_world", EditorIcons.Globe_I, "World aligned: projected along the world axes nearest each face, so walls of separate meshes line up",
            t.Alignment == TextureAlignment.World, true, () => EditTextures("Align Texture to World", x => x with { Alignment = TextureAlignment.World }));
        BarIcon(paper, $"{id}_face", EditorIcons.VectorSquare_I, "Face aligned: laid in each face's own plane, so slopes show the texture unstretched",
            t.Alignment == TextureAlignment.Face, true, () => EditTextures("Align Texture to Face", x => x with { Alignment = TextureAlignment.Face }));

        BarDivider(paper, $"{id}_d0");
        BarHint(paper, $"{id}_sl", "Shift");
        TextureField(paper, $"{id}_su", "U", EditorTheme.Red400, t.Offset.X, "0.###",
            v => EditTextures("Shift Texture", x => x with { Offset = new Float2(v, x.Offset.Y) }));
        TextureField(paper, $"{id}_sv", "V", EditorTheme.Green400, t.Offset.Y, "0.###",
            v => EditTextures("Shift Texture", x => x with { Offset = new Float2(x.Offset.X, v) }));
        BarHint(paper, $"{id}_kl", "Scale");
        TextureField(paper, $"{id}_ku", "U", EditorTheme.Red400, t.Scale.X, "0.###",
            v => { if (MathF.Abs(v) > 1e-4f) EditTextures("Scale Texture", x => x with { Scale = new Float2(v, x.Scale.Y) }); });
        TextureField(paper, $"{id}_kv", "V", EditorTheme.Green400, t.Scale.Y, "0.###",
            v => { if (MathF.Abs(v) > 1e-4f) EditTextures("Scale Texture", x => x with { Scale = new Float2(x.Scale.X, v) }); });
        BarHint(paper, $"{id}_rl", "Turn");
        TextureField(paper, $"{id}_r", "", EditorTheme.Ink300, t.Rotation, "0.##°",
            v => EditTextures("Turn Texture", x => x with { Rotation = v % 360f }));

        BarDivider(paper, $"{id}_d1");
        BarIcon(paper, $"{id}_fit", EditorIcons.Expand_I, "Fit: stretch the texture once across the faces", false, true, () => JustifyTextures(TextureJustify.Fit));
        BarIcon(paper, $"{id}_jl", EditorIcons.AlignLeft_I, "Justify left: start the texture at the faces' left edge", false, true, () => JustifyTextures(TextureJustify.Left));
        BarIcon(paper, $"{id}_jr", EditorIcons.AlignRight_I, "Justify right: end the texture at the faces' right edge", false, true, () => JustifyTextures(TextureJustify.Right));
        BarIcon(paper, $"{id}_jt", EditorIcons.ArrowUp_I, "Justify top: end the texture at the faces' top edge", false, true, () => JustifyTextures(TextureJustify.Top));
        BarIcon(paper, $"{id}_jb", EditorIcons.ArrowDown_I, "Justify bottom: start the texture at the faces' bottom edge", false, true, () => JustifyTextures(TextureJustify.Bottom));
        BarIcon(paper, $"{id}_jc", EditorIcons.AlignCenter_I, "Centre the texture on the faces", false, true, () => JustifyTextures(TextureJustify.Center));
        BarIcon(paper, $"{id}_one", EditorIcons.ObjectGroup_I,
            s_justifyAsOne ? "Fit and justify treat the selection as one surface. Click to treat each face on its own" : "Fit and justify treat each face on its own. Click to treat the selection as one surface",
            s_justifyAsOne, true, () => s_justifyAsOne = !s_justifyAsOne);
        BarIcon(paper, $"{id}_reset", EditorIcons.ArrowRotateLeft_I, "Reset the texture: world aligned, unturned, scale 1, no shift", false, true,
            () => EditTextures("Reset Texture", _ => FaceTexture.Default));
    }

    private static void TextureField(Paper paper, string id, string label, System.Drawing.Color color, float value, string format, Action<float> set)
    {
        using (paper.Row(id).Width(64).Height(BarItem - 4).Margin(2, 2, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
        {
            NumericFieldBuilder<float> field = Origami.NumericField<float>(paper, $"{id}_v", value, v => { if (float.IsFinite(v)) set(v); }).Format(format);
            if (label.Length > 0) field.Prefix(label, color);
            field.Show();
        }
    }

    /// <summary>Changes the mapping of every selected face.</summary>
    private void EditTextures(string name, Func<FaceTexture, FaceTexture> change)
    {
        if (!HasMesh || _faces.Count == 0) return;
        List<Face> faces = [.. _faces];
        RunOp(name, _ =>
        {
            foreach (Face f in faces) EditableMesh.SetFaceTexture(f, change(EditableMesh.GetFaceTexture(f)));
        });
    }

    /// <summary>
    /// Fits or justifies each selected face's texture to the extent of the faces along its own U and V: that face
    /// alone, or the whole selection when <see cref="s_justifyAsOne"/> is on. Fit scales so one repeat spans the
    /// extent; justify shifts so an edge of the texture meets that side of it.
    /// </summary>
    private void JustifyTextures(TextureJustify mode)
    {
        if (!HasMesh || _faces.Count == 0) return;
        Float4x4 l2w = _target!.Transform.LocalToWorldMatrix;
        List<Face> faces = [.. _faces];
        List<Float3> all = [.. faces.SelectMany(f => f.NeighborVertices()).Distinct().Select(v => Float4x4.TransformPoint(v.Point, l2w))];

        RunOp(mode == TextureJustify.Fit ? "Fit Texture" : "Justify Texture", _ =>
        {
            foreach (Face f in faces)
            {
                FaceTexture t = EditableMesh.GetFaceTexture(f);
                (Float3 u, Float3 v) = t.Axes(WorldNormal(f, l2w));
                IEnumerable<Float3> points = s_justifyAsOne ? all : f.NeighborVertices().Select(x => Float4x4.TransformPoint(x.Point, l2w));

                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (Float3 p in points)
                {
                    float pu = Float3.Dot(p, u), pv = Float3.Dot(p, v);
                    minU = MathF.Min(minU, pu); maxU = MathF.Max(maxU, pu);
                    minV = MathF.Min(minV, pv); maxV = MathF.Max(maxV, pv);
                }

                Float2 scale = t.Scale, offset = t.Offset;
                if (mode == TextureJustify.Fit)
                {
                    if (maxU - minU > 1e-4f) scale.X = MathF.CopySign((maxU - minU) / FaceTexture.UnitsPerTile, t.Scale.X);
                    if (maxV - minV > 1e-4f) scale.Y = MathF.CopySign((maxV - minV) / FaceTexture.UnitsPerTile, t.Scale.Y);
                }

                // World units per repeat along each axis, and so the coordinate a distance along it lands on.
                float tileU = scale.X * FaceTexture.UnitsPerTile, tileV = scale.Y * FaceTexture.UnitsPerTile;
                switch (mode)
                {
                    case TextureJustify.Fit:
                        offset = new Float2(-MathF.Min(minU / tileU, maxU / tileU), -MathF.Min(minV / tileV, maxV / tileV));
                        break;
                    case TextureJustify.Left: offset.X = -minU / tileU; break;
                    case TextureJustify.Right: offset.X = 1f - maxU / tileU; break;
                    case TextureJustify.Bottom: offset.Y = -minV / tileV; break;
                    case TextureJustify.Top: offset.Y = 1f - maxV / tileV; break;
                    case TextureJustify.Center:
                        offset = new Float2(0.5f - (minU + maxU) * 0.5f / tileU, 0.5f - (minV + maxV) * 0.5f / tileV);
                        break;
                }
                // A whole repeat either way looks the same, so keep the shift between 0 and 1.
                offset = new Float2(offset.X - MathF.Floor(offset.X), offset.Y - MathF.Floor(offset.Y));
                EditableMesh.SetFaceTexture(f, t with { Scale = scale, Offset = offset });
            }
        });
    }

    private static Float3 WorldNormal(Face f, Float4x4 l2w)
    {
        Float3 n = Float4x4.TransformNormal(f.Normal(), l2w);
        return Float3.LengthSquared(n) > 1e-12f ? Float3.Normalize(n) : Float3.UnitY;
    }
}
