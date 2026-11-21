// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Prowl.Ember;
using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

/// <summary>
/// Finds, creates and runs every <see cref="GameSystem"/>. Built on first use, and rebuilt after a script reload
/// only when the set of system types changed. Each scene hears <see cref="GameSystem.OnSceneCreated"/> once per
/// build: as it is created, or from its own hot reload once it has migrated.
/// </summary>
internal static class GameSystemRegistry
{
    private readonly record struct Listener(GameSystem System, Action<SceneInstance> Action, int Order);

    private static readonly object s_lock = new();

    // Every GameSystem type, found again after a script reload.
    private static readonly ReloadCache<Type, Type[]> s_types = new(static _ => Discover());

    // Skipped by the hot reload walk: a reload disposes these and builds them again from the new types.
    [ReloadIgnore] private static Type[]? s_builtFrom;
    [ReloadIgnore] private static Dictionary<Type, GameSystem> s_systems = [];
    [ReloadIgnore] private static Listener[][] s_listeners = Freeze(NewLists());
    [ReloadIgnore] private static List<Listener>[]? s_building;

    // The scenes the current build has told about, held weakly so a scene nobody disposed can still be collected.
    [ReloadIgnore] private static ConditionalWeakTable<SceneInstance, object> s_told = new();

    // The built systems by slot, which build this is, and which slots each concrete component type is kept for.
    [ReloadIgnore] private static GameSystem[] s_slots = [];
    [ReloadIgnore] private static int s_build;
    [ReloadIgnore] private static Dictionary<Type, int[]> s_routes = [];

    // Every system is always present, so a missing one is a bug, not a state.
    public static T Get<T>() where T : GameSystem
    {
        lock (s_lock)
        {
            if (s_building != null)
                throw new InvalidOperationException($"'{NameOf(typeof(T))}' was asked for while the systems are being created. " +
                    "A system cannot reach another system from its constructor; do it from a listener or OnSceneCreated.");
            EnsureBuilt();
            return s_systems.TryGetValue(typeof(T), out GameSystem? system)
                ? (T)system
                : throw new InvalidOperationException($"The engine system '{NameOf(typeof(T))}' is not running. It failed to construct, see the log.");
        }
    }

    public static void Listen(GameSystem system, SceneStage stage, Action<SceneInstance> action, int order)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (s_building == null || s_systems.ContainsValue(system))
            throw new InvalidOperationException($"{system.GetType().Name} can only listen from its constructor.");

        // After every listener of the same or a lower order, so equal orders keep system and call order.
        List<Listener> listeners = s_building[(int)stage];
        int index = listeners.Count;
        while (index > 0 && listeners[index - 1].Order > order) index--;
        listeners.Insert(index, new Listener(system, action, order));
    }

    /// <summary>
    /// Rebuilds the systems if a script reload changed their types. The host calls it once right after a reload,
    /// before any scene migrates, so systems of replaced types are let go even when no scene is alive.
    /// </summary>
    public static void EnsureCurrent()
    {
        lock (s_lock) EnsureBuilt();
    }

    public static void SceneCreated(SceneInstance scene) => Tell(scene);

    /// <summary>
    /// Called by a scene's hot reload once it has migrated, before its components rejoin: its sets are emptied for
    /// the rejoining components to fill, and a rebuilt system hears of it.
    /// </summary>
    public static void SceneReloaded(SceneInstance scene)
    {
        scene.SystemSets = null;
        Tell(scene);
    }

    /// <summary>A component enabled in <paramref name="world"/>: it goes into the set of every system that iterates its type.</summary>
    public static void Joined(SceneInstance world, Component component)
    {
        int[] slots = RoutesFor(component.GetType());
        for (int i = 0; i < slots.Length; i++) SetAt(world, slots[i]).Add(component);
    }

    /// <summary>A component disabled in or leaving <paramref name="world"/>.</summary>
    public static void Left(SceneInstance world, Component component)
    {
        // Sets not built for this build are refilled from what has joined, which this no longer is.
        ComponentSet?[]? sets = world.SystemSets;
        if (sets == null || world.SystemSetsBuild != s_build) return;
        int[] slots = RoutesFor(component.GetType());
        for (int i = 0; i < slots.Length; i++) sets[slots[i]]?.Remove(component);
    }

    /// <summary>Hands <paramref name="system"/>'s components in <paramref name="scene"/> to <paramref name="action"/>.</summary>
    public static void Run<T>(SceneInstance scene, GameSystem system, ComponentsAction<T> action) where T : class
    {
        var set = (ComponentSet<T>)SetAt(scene, SlotOf(system));
        set.Begin();
        try { action(scene, new SceneComponents<T>(set.Items)); }
        finally { set.End(); }
    }

    public static ComponentSet<T> SetFor<T>(SceneInstance scene, GameSystem system) where T : class
        => (ComponentSet<T>)SetAt(scene, SlotOf(system));

    /// <summary><paramref name="system"/>'s state in <paramref name="scene"/>, made on first use and kept until the scene is disposed or the systems rebuild.</summary>
    public static T StateOf<T>(SceneInstance scene, GameSystem system) where T : class, new()
    {
        int slot = SlotOf(system);
        object?[]? states = scene.SystemStates;
        if (states == null || scene.SystemStatesBuild != s_build)
        {
            DropStates(scene);
            scene.SystemStates = states = new object?[s_slots.Length];
            scene.SystemStatesBuild = s_build;
        }

        object state = states[slot] ??= new T();
        return state as T ?? throw new InvalidOperationException(
            $"{NameOf(system.GetType())} keeps a {state.GetType().Name} per scene, so it cannot also keep a {typeof(T).Name}.");
    }

    // Disposes whatever state the scene kept for the systems, and lets it go.
    private static void DropStates(SceneInstance scene)
    {
        object?[]? states = scene.SystemStates;
        scene.SystemStates = null;
        if (states == null) return;
        foreach (object? state in states)
        {
            if (state is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception ex) { Debug.LogException($"[GameSystem] A {state.GetType().Name} kept for '{scene.Name}' threw while being disposed", ex); }
        }
    }

    // A system's slot, refused for a system the current build does not run, whose slot now belongs to another.
    private static int SlotOf(GameSystem system)
    {
        int slot = system.Slot;
        if ((uint)slot < (uint)s_slots.Length && ReferenceEquals(s_slots[slot], system)) return slot;
        throw new InvalidOperationException($"'{NameOf(system.GetType())}' is not a running system. It was replaced by a script reload, or never constructed.");
    }

    private static ComponentSet SetAt(SceneInstance scene, int slot)
    {
        ComponentSet?[]? sets = scene.SystemSets;
        if (sets == null || scene.SystemSetsBuild != s_build) sets = Refill(scene);
        return sets[slot] ??= s_slots[slot].NewSet();
    }

    // Fresh sets for this build, filled from every component that has joined the scene.
    private static ComponentSet?[] Refill(SceneInstance scene)
    {
        var sets = new ComponentSet?[s_slots.Length];
        scene.SystemSets = sets;
        scene.SystemSetsBuild = s_build;

        foreach (GameObject go in scene.Objects)
        {
            if (go.IsDisposed) continue;
            foreach (Component component in go._components)
            {
                if (ReferenceEquals(component, null) || component.IsDisposed || !ReferenceEquals(component._joinedWorld, scene)) continue;
                int[] slots = RoutesFor(component.GetType());
                for (int i = 0; i < slots.Length; i++) (sets[slots[i]] ??= s_slots[slots[i]].NewSet()).Add(component);
            }
        }
        return sets;
    }

    private static int[] RoutesFor(Type type)
    {
        if (s_routes.TryGetValue(type, out int[]? slots)) return slots;

        List<int> found = [];
        for (int i = 0; i < s_slots.Length; i++)
            if (s_slots[i].ComponentType is { } wanted && wanted.IsAssignableFrom(type))
                found.Add(i);
        return s_routes[type] = [.. found];
    }

    public static void SceneDestroyed(SceneInstance scene)
    {
        lock (s_lock)
        {
            s_told.Remove(scene);
            foreach (GameSystem system in s_systems.Values) Notify(system, scene, created: false);
            DropStates(scene);
        }
    }

    private static void Tell(SceneInstance scene)
    {
        lock (s_lock)
        {
            EnsureBuilt();
            if (!s_told.TryAdd(scene, s_lock)) return;
            foreach (GameSystem system in s_systems.Values) Notify(system, scene, created: true);
        }
    }

    /// <summary>Runs every listener of <paramref name="stage"/> for <paramref name="scene"/>. One that throws is reported once and does not stop the rest.</summary>
    public static void Signal(SceneInstance scene, SceneStage stage)
    {
        Listener[] listeners = s_listeners[(int)stage];
        for (int i = 0; i < listeners.Length; i++)
        {
            Listener listener = listeners[i];
            try { listener.Action(scene); }
            catch (Exception ex)
            {
                string name = NameOf(listener.System.GetType());
                Debug.LogErrorOnce($"GameSystem.{name}.{stage}.{ex.GetType().FullName}",
                    $"[GameSystem] '{name}' threw at {stage} and was skipped: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    // Scenes are told about a new build by SceneCreated or SceneReloaded, never from here: a scene mid reload
    // has not migrated yet, so a system looking at it now would see the old types.
    private static void EnsureBuilt()
    {
        Type[] types = s_types[typeof(GameSystem)];
        if (ReferenceEquals(types, s_builtFrom)) return;

        // A reload that left every system type as it was keeps the systems and their state.
        if (s_builtFrom != null && types.SequenceEqual(s_builtFrom))
        {
            s_builtFrom = types;
            return;
        }

        // State belongs to the systems it was kept for, so it goes with them.
        foreach (SceneInstance scene in SceneInstance.All) DropStates(scene);
        DisposeAll();
        s_builtFrom = types;
        s_told = new();
        Build(types);
    }

    private static void Build(Type[] types)
    {
        s_building = NewLists();
        List<GameSystem> slots = [];
        try
        {
            foreach (Type type in types)
            {
                ConstructorInfo? constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
                if (constructor == null)
                {
                    Debug.LogErrorOnce($"GameSystem.NoConstructor.{type.FullName}",
                        $"[GameSystem] '{NameOf(type)}' has no parameterless constructor, so it does not run.");
                    continue;
                }

                try
                {
                    var system = (GameSystem)constructor.Invoke(null);
                    system.Slot = slots.Count;
                    slots.Add(system);
                    s_systems[type] = system;
                }
                catch (Exception ex)
                {
                    // Whatever the failed constructor listened to goes with it.
                    foreach (List<Listener> listeners in s_building)
                        listeners.RemoveAll(listener => listener.System.GetType() == type);
                    Debug.LogException($"[GameSystem] '{NameOf(type)}' threw while being created, so it does not run",
                        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
                }
            }
            s_listeners = Freeze(s_building);
        }
        finally
        {
            s_building = null;
            s_slots = [.. slots];
            s_routes = [];
            s_build++;
        }
    }

    private static void DisposeAll()
    {
        foreach (GameSystem system in s_systems.Values)
        {
            try { system.Dispose(); }
            catch (Exception ex) { Debug.LogException($"[GameSystem] '{NameOf(system.GetType())}' threw while being disposed", ex); }
        }
        s_systems = [];
        s_listeners = Freeze(NewLists());
        s_slots = [];
        s_routes = [];
        s_build++;
    }

    private static void Notify(GameSystem system, SceneInstance scene, bool created)
    {
        try
        {
            if (created) system.OnSceneCreated(scene);
            else system.OnSceneDestroyed(scene);
        }
        catch (Exception ex)
        {
            string callback = created ? nameof(GameSystem.OnSceneCreated) : nameof(GameSystem.OnSceneDestroyed);
            Debug.LogException($"[GameSystem] '{NameOf(system.GetType())}' threw in {callback}", ex);
        }
    }

    private static Type[] Discover()
    {
        List<Type> found = [];
        foreach (Assembly assembly in RuntimeUtils.AssemblySource())
            foreach (Type type in RuntimeUtils.GetLoadableTypes(assembly))
                if (!type.IsAbstract && !type.ContainsGenericParameters && typeof(GameSystem).IsAssignableFrom(type))
                    found.Add(type);
        found.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return [.. found];
    }

    private static List<Listener>[] NewLists()
    {
        var lists = new List<Listener>[Enum.GetValues<SceneStage>().Length];
        for (int i = 0; i < lists.Length; i++) lists[i] = [];
        return lists;
    }

    private static Listener[][] Freeze(List<Listener>[] lists)
    {
        var frozen = new Listener[lists.Length][];
        for (int i = 0; i < lists.Length; i++) frozen[i] = [.. lists[i]];
        return frozen;
    }

    private static string NameOf(Type type) => type.FullName ?? type.Name;

    /// <summary>One system's components in one scene.</summary>
    internal abstract class ComponentSet
    {
        public abstract void Add(Component component);
        public abstract void Remove(Component component);
    }

    /// <summary>
    /// A dense array with O(1) add and swap remove. While a pass runs over it nothing moves: a removed entry is
    /// nulled and an added one waits, and both settle when the pass ends.
    /// </summary>
    internal sealed class ComponentSet<T> : ComponentSet where T : class
    {
        private T?[] _items = new T?[8];
        private int _count;
        private readonly Dictionary<T, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly List<T> _waiting = [];
        private int _passes;
        private bool _holes;

        public ReadOnlySpan<T?> Items => new(_items, 0, _count);

        public override void Add(Component component)
        {
            var item = (T)(object)component;
            if (_index.ContainsKey(item)) return;
            if (_passes > 0)
            {
                if (!_waiting.Contains(item)) _waiting.Add(item);
                return;
            }
            Append(item);
        }

        public override void Remove(Component component)
        {
            var item = (T)(object)component;
            if (_waiting.Remove(item) || !_index.Remove(item, out int at)) return;

            if (_passes > 0)
            {
                _items[at] = null;
                _holes = true;
                return;
            }

            int last = --_count;
            if (at != last)
            {
                T moved = _items[last]!;
                _items[at] = moved;
                _index[moved] = at;
            }
            _items[last] = null;
        }

        public void Begin() => _passes++;

        public void End()
        {
            if (--_passes > 0) return;

            if (_holes)
            {
                int kept = 0;
                for (int i = 0; i < _count; i++)
                {
                    T? item = _items[i];
                    if (item == null) continue;
                    _items[kept] = item;
                    _index[item] = kept++;
                }
                Array.Clear(_items, kept, _count - kept);
                _count = kept;
                _holes = false;
            }

            foreach (T item in _waiting)
                if (!_index.ContainsKey(item)) Append(item);
            _waiting.Clear();
        }

        private void Append(T item)
        {
            if (_count == _items.Length) Array.Resize(ref _items, _count * 2);
            _index[item] = _count;
            _items[_count++] = item;
        }
    }
}
