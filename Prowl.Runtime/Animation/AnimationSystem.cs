// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Diagnostics;
using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

/// <summary>Ticks every enabled animator in a playing scene, between its Update and LateUpdate.</summary>
public sealed class AnimationSystem : GameSystem<Animator>
{
    public AnimationSystem() => Listen(SceneStage.AfterUpdate, Tick);

    private static void Tick(SceneInstance scene, SceneComponents<Animator> animators)
    {
        if (!scene.IsPlaying || animators.IsEmpty) return;
        using FrameStatScope stats = FrameStats.Animation.Scope();

        float deltaTime = Time.DeltaTime;
        foreach (Animator animator in animators)
        {
            try { animator.Tick(deltaTime); }
            catch (Exception ex) { Debug.LogException($"[Animator] '{animator.GameObject.Name}' threw while animating", ex); }
        }
    }
}
