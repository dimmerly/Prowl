// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Runtime.Test;

public class GameSystemTests : RuntimeTestBase
{
    // Discovered like any other system, so it runs for every scene in the suite. It only records the scenes a test watches.
    private sealed class RecordingSystem : GameSystem
    {
        public static RecordingSystem Instance => Get<RecordingSystem>();

        public static readonly HashSet<SceneInstance> Watched = new(ReferenceEqualityComparer.Instance);
        public static readonly List<string> Log = [];
        public static bool Throw;

        public RecordingSystem()
        {
            Listen(SceneStage.AfterUpdate, scene => Record(scene, "AfterUpdate"));
            Listen(SceneStage.AfterUpdate, scene => Record(scene, "AfterUpdate-1"), order: -1);
            Listen(SceneStage.AfterUpdate, scene => { if (Throw && Watched.Contains(scene)) throw new InvalidOperationException("boom"); }, order: -2);
            Listen(SceneStage.BeforeUpdate, scene => Record(scene, "BeforeUpdate"));
            Listen(SceneStage.BeforeFixedUpdate, scene => Record(scene, "BeforeFixedUpdate"));
            Listen(SceneStage.AfterFixedUpdate, scene => Record(scene, "AfterFixedUpdate"));
        }

        protected internal override void OnSceneCreated(SceneInstance scene) => Record(scene, "Created");

        protected internal override void OnSceneDestroyed(SceneInstance scene) => Record(scene, "Destroyed");

        private static void Record(SceneInstance scene, string entry)
        {
            if (Watched.Contains(scene)) Log.Add(entry);
        }
    }

    private sealed class PhaseProbe : Component
    {
        public override void Update() => RecordingSystem.Log.Add("Update");
        public override void LateUpdate() => RecordingSystem.Log.Add("LateUpdate");
    }

    // Hands over every enabled PhaseProbe, and records them for the scenes a test watches.
    private sealed class ProbeSystem : GameSystem<PhaseProbe>
    {
        public static ProbeSystem Instance => Get<ProbeSystem>();

        public static readonly HashSet<SceneInstance> Watched = new(ReferenceEqualityComparer.Instance);
        public static readonly List<PhaseProbe> Seen = [];
        public static Action<PhaseProbe>? OnPass;

        public ProbeSystem() => Listen(SceneStage.AfterUpdate, (scene, probes) =>
        {
            if (!Watched.Contains(scene)) return;
            foreach (PhaseProbe probe in probes)
            {
                Seen.Add(probe);
                OnPass?.Invoke(probe);
            }
        });

        public static Counter Of(SceneInstance scene) => Instance.StateIn(scene);

        public Counter StateIn(SceneInstance scene) => StateOf<Counter>(scene);

        public List<int> WrongStateIn(SceneInstance scene) => StateOf<List<int>>(scene);

        public static List<PhaseProbe> In(SceneInstance scene)
        {
            List<PhaseProbe> probes = [];
            foreach (PhaseProbe probe in Instance.ComponentsIn(scene)) probes.Add(probe);
            return probes;
        }
    }

    private sealed class Counter : IDisposable
    {
        public int Value;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static void ForceRebuild()
    {
        typeof(GameSystemRegistry).GetField("s_builtFrom", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        GameSystemRegistry.EnsureCurrent();
    }

    public GameSystemTests()
    {
        ProbeSystem.Watched.Clear();
        ProbeSystem.Seen.Clear();
        ProbeSystem.OnPass = null;
        RecordingSystem.Watched.Clear();
        RecordingSystem.Log.Clear();
        RecordingSystem.Throw = false;
    }

    public override void Dispose()
    {
        ProbeSystem.Watched.Clear();
        ProbeSystem.OnPass = null;
        RecordingSystem.Watched.Clear();
        RecordingSystem.Throw = false;
        base.Dispose();
    }

    [Fact]
    public void Systems_AreDiscoveredOnce()
    {
        CreateScene();
        RecordingSystem first = RecordingSystem.Instance;
        Assert.NotNull(first);
        Assert.NotNull(GameSystemRegistry.Get<AnimationSystem>());

        CreateScene();
        Assert.Same(first, RecordingSystem.Instance);
    }

    [Fact]
    public void Listeners_RunAtTheirStage_InOrder()
    {
        SceneInstance scene = CreateScene(enable: true);
        scene.Create("Probe").AddComponent<PhaseProbe>();
        Update(scene);
        RecordingSystem.Watched.Add(scene);
        RecordingSystem.Log.Clear();

        Tick(scene);

        Assert.Equal(["BeforeFixedUpdate", "AfterFixedUpdate", "BeforeUpdate", "Update", "AfterUpdate-1", "AfterUpdate", "LateUpdate"],
            RecordingSystem.Log);
    }

    [Fact]
    public void AThrowingListener_DoesNotStopTheRest()
    {
        SceneInstance scene = CreateScene(enable: true);
        RecordingSystem.Watched.Add(scene);
        RecordingSystem.Throw = true;

        Update(scene);

        Assert.Contains("AfterUpdate-1", RecordingSystem.Log);
        Assert.Contains("AfterUpdate", RecordingSystem.Log);
    }

    [Fact]
    public void ADestroyedScene_IsReported()
    {
        SceneInstance scene = CreateScene(enable: true);
        RecordingSystem.Watched.Add(scene);

        scene.Dispose();

        Assert.Contains("Destroyed", RecordingSystem.Log);
    }

    [Fact]
    public void AHotReload_ThatChangedTheTypes_RebuildsSystems_AndTellsEachSceneOnceItMigrated()
    {
        SceneInstance scene = CreateScene(enable: true);
        RecordingSystem.Watched.Add(scene);
        RecordingSystem before = RecordingSystem.Instance;

        // As if the reload replaced a system type: nothing matches what the systems were built from.
        typeof(GameSystemRegistry).GetField("s_builtFrom", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        GameSystemRegistry.EnsureCurrent();
        Assert.NotSame(before, RecordingSystem.Instance);
        Assert.DoesNotContain("Created", RecordingSystem.Log);

        scene.OnHotReload();
        Update(scene);

        Assert.Equal(["Created", "BeforeUpdate", "AfterUpdate-1", "AfterUpdate"], RecordingSystem.Log);
    }

    [Fact]
    public void AHotReload_ThatLeftTheTypes_KeepsTheSystems()
    {
        SceneInstance scene = CreateScene(enable: true);
        RecordingSystem.Watched.Add(scene);
        RecordingSystem before = RecordingSystem.Instance;

        // What a script reload does to the discovery cache: the next look finds the same types anew.
        object types = typeof(GameSystemRegistry).GetField("s_types", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        types.GetType().GetMethod("Reset")!.Invoke(types, null);
        GameSystemRegistry.EnsureCurrent();
        scene.OnHotReload();

        Assert.Same(before, RecordingSystem.Instance);
        Assert.DoesNotContain("Created", RecordingSystem.Log);
    }

    [Fact]
    public void AComponentSystem_IsHandedOnlyEnabledComponents()
    {
        SceneInstance scene = CreateScene(enable: true);
        PhaseProbe on = scene.Create("On").AddComponent<PhaseProbe>();
        PhaseProbe off = scene.Create("Off").AddComponent<PhaseProbe>();
        PhaseProbe gone = scene.Create("Gone").AddComponent<PhaseProbe>();
        off.Enabled = false;
        gone.GameObject.Dispose();
        ProbeSystem.Watched.Add(scene);

        Update(scene);

        Assert.Equal([on], ProbeSystem.Seen);
        Assert.Equal([on], ProbeSystem.In(scene));
    }

    [Fact]
    public void AComponentSystem_SurvivesTheWorldChangingMidPass()
    {
        SceneInstance scene = CreateScene(enable: true);
        PhaseProbe a = scene.Create("A").AddComponent<PhaseProbe>();
        PhaseProbe b = scene.Create("B").AddComponent<PhaseProbe>();
        PhaseProbe c = scene.Create("C").AddComponent<PhaseProbe>();
        PhaseProbe? late = null;
        ProbeSystem.Watched.Add(scene);

        // Whichever comes first destroys another and adds one: nothing is skipped, repeated, or handed over early.
        PhaseProbe? victim = null;
        ProbeSystem.OnPass = probe =>
        {
            ProbeSystem.OnPass = null;
            victim = probe == b ? c : b;
            victim.GameObject.Dispose();
            late = scene.Create("Late").AddComponent<PhaseProbe>();
        };
        Update(scene);

        Assert.Equal(2, ProbeSystem.Seen.Count);
        Assert.DoesNotContain(victim!, ProbeSystem.Seen);
        Assert.DoesNotContain(late!, ProbeSystem.Seen);

        ProbeSystem.Seen.Clear();
        Update(scene);
        Assert.Equal(3, ProbeSystem.Seen.Count);
        Assert.Contains(late!, ProbeSystem.Seen);
        Assert.DoesNotContain(victim!, ProbeSystem.Seen);
        Assert.Contains(a, ProbeSystem.Seen);
    }

    [Fact]
    public void AComponentSystem_KeepsItsSetRight_AsComponentsComeAndGo()
    {
        SceneInstance scene = CreateScene(enable: true);
        List<PhaseProbe> probes = [];
        for (int i = 0; i < 20; i++) probes.Add(scene.Create($"P{i}").AddComponent<PhaseProbe>());
        for (int i = 0; i < 20; i += 3) probes[i].Enabled = false;
        probes[1].GameObject.Dispose();
        probes[3].Enabled = true;

        HashSet<PhaseProbe> expected = [.. probes.FindAll(p => p.IsValid() && p.EnabledInHierarchy)];
        Assert.Equal(expected, new HashSet<PhaseProbe>(ProbeSystem.In(scene)));
    }

    [Fact]
    public void AHotReload_RefillsComponentSystems()
    {
        SceneInstance scene = CreateScene(enable: true);
        PhaseProbe probe = scene.Create("Probe").AddComponent<PhaseProbe>();

        typeof(GameSystemRegistry).GetField("s_builtFrom", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        GameSystemRegistry.EnsureCurrent();
        scene.OnHotReload();

        Assert.Equal([probe], ProbeSystem.In(scene));
    }

    [Fact]
    public void State_IsKeptPerScene()
    {
        SceneInstance a = CreateScene(enable: true);
        SceneInstance b = CreateScene(enable: true);

        ProbeSystem.Of(a).Value = 5;

        Assert.Same(ProbeSystem.Of(a), ProbeSystem.Of(a));
        Assert.Equal(5, ProbeSystem.Of(a).Value);
        Assert.NotSame(ProbeSystem.Of(a), ProbeSystem.Of(b));
    }

    [Fact]
    public void State_IsDisposedWithItsScene()
    {
        SceneInstance scene = CreateScene(enable: true);
        Counter state = ProbeSystem.Of(scene);

        scene.Dispose();

        Assert.True(state.Disposed);
    }

    [Fact]
    public void State_GoesWithTheSystems_WhenTheyRebuild()
    {
        SceneInstance scene = CreateScene(enable: true);
        ProbeSystem before = ProbeSystem.Instance;
        Counter state = ProbeSystem.Of(scene);

        ForceRebuild();

        Assert.True(state.Disposed);
        Assert.NotSame(state, ProbeSystem.Of(scene));
        // The replaced system no longer owns a slot, so it cannot reach the new build's state.
        Assert.Throws<InvalidOperationException>(() => before.StateIn(scene));
    }

    [Fact]
    public void State_SurvivesAReload_ThatLeftTheTypes()
    {
        SceneInstance scene = CreateScene(enable: true);
        Counter state = ProbeSystem.Of(scene);

        object types = typeof(GameSystemRegistry).GetField("s_types", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        types.GetType().GetMethod("Reset")!.Invoke(types, null);
        GameSystemRegistry.EnsureCurrent();
        scene.OnHotReload();

        Assert.Same(state, ProbeSystem.Of(scene));
        Assert.False(state.Disposed);
    }

    [Fact]
    public void State_IsOneTypePerSystem()
    {
        SceneInstance scene = CreateScene(enable: true);
        ProbeSystem.Of(scene);

        Assert.Throws<InvalidOperationException>(() => ProbeSystem.Instance.WrongStateIn(scene));
    }
}
