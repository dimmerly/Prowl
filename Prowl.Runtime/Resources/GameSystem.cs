// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

/// <summary>Points in each scene's tick an <see cref="GameSystem"/> can listen to.</summary>
public enum SceneStage
{
    /// <summary>Before component Update.</summary>
    BeforeUpdate,
    /// <summary>After component Update, before LateUpdate.</summary>
    AfterUpdate,
    /// <summary>Before component FixedUpdate.</summary>
    BeforeFixedUpdate,
    /// <summary>After component FixedUpdate and the physics step.</summary>
    AfterFixedUpdate,
}

/// <summary>
/// An engine wide module that hooks into every scene. Every non abstract subclass with a parameterless
/// constructor is created once by the engine, and recreated after a script reload that changed the system
/// types. A system that others reach declares <c>public static MySystem Instance => Get&lt;MySystem&gt;();</c>,
/// which cannot be used from another system's constructor. Per scene state lives in <see cref="StateOf{T}"/>,
/// and others reach it through an accessor the system writes, such as
/// <c>public static NavMeshWorld Of(SceneInstance scene) => Get&lt;NavigationSystem&gt;().StateOf&lt;NavMeshWorld&gt;(scene);</c>
/// </summary>
public abstract class GameSystem
{
    // This system's place in the current build, which indexes each scene's component sets.
    internal int Slot = -1;

    // The component type the engine keeps a set of for this system, or null for a system without one.
    internal virtual Type? ComponentType => null;

    internal virtual GameSystemRegistry.ComponentSet NewSet() => throw new InvalidOperationException($"{GetType().Name} iterates no component.");

    /// <summary>
    /// Runs <paramref name="action"/> for each scene at <paramref name="stage"/>, inside that scene's tick.
    /// Lower order runs first; equal orders run in system order (by type name), then in the order they were
    /// listened. Call from the constructor.
    /// </summary>
    protected void Listen(SceneStage stage, Action<SceneInstance> action, int order = 0) => GameSystemRegistry.Listen(this, stage, action, order);

    /// <summary>Called once per scene: as it is constructed, or after a rebuild, from the scene's own hot reload once it has migrated.</summary>
    protected internal virtual void OnSceneCreated(SceneInstance scene) { }

    /// <summary>Called as a scene is disposed. Drop anything kept for it.</summary>
    protected internal virtual void OnSceneDestroyed(SceneInstance scene) { }

    /// <summary>Called before the system is replaced after a script reload.</summary>
    public virtual void Dispose() { }

    /// <summary>The running system of type <typeparamref name="T"/>. Every system is always present; one that failed to construct throws here.</summary>
    protected static T Get<T>() where T : GameSystem => GameSystemRegistry.Get<T>();

    /// <summary>
    /// This system's state for <paramref name="scene"/>: made on first use, stored with the scene, and disposed (when
    /// <see cref="IDisposable"/>) as the scene is, or when a script reload rebuilds the systems. A reload that left the
    /// system types as they were keeps it. One state type per system.
    /// </summary>
    protected T StateOf<T>(SceneInstance scene) where T : class, new() => GameSystemRegistry.StateOf<T>(scene, this);
}

/// <summary>A scene's enabled <typeparamref name="TComponent"/>s, handed to a <see cref="GameSystem{TComponent}"/> listener.</summary>
public delegate void ComponentsAction<TComponent>(SceneInstance scene, SceneComponents<TComponent> components) where TComponent : class;

/// <summary>
/// The enabled components of one type in a scene, for a foreach. One that leaves the scene during the pass is
/// not handed over from then on.
/// </summary>
public readonly ref struct SceneComponents<T> where T : class
{
    private readonly ReadOnlySpan<T?> _items;

    internal SceneComponents(ReadOnlySpan<T?> items) => _items = items;

    public bool IsEmpty => _items.IsEmpty;

    public Enumerator GetEnumerator() => new(_items);

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<T?> _items;
        private int _index;

        internal Enumerator(ReadOnlySpan<T?> items)
        {
            _items = items;
            _index = -1;
        }

        public readonly T Current => _items[_index]!;

        // A slot is null when its component left mid pass, since nothing moves until the pass ends.
        public bool MoveNext()
        {
            while (++_index < _items.Length)
                if (_items[_index] is not null) return true;
            return false;
        }
    }
}

/// <summary>
/// A <see cref="GameSystem"/> that iterates one component type. The engine keeps a dense set of every enabled
/// <typeparamref name="TComponent"/> per scene as components enable and disable, so a listener gets them with no
/// lookup or filtering. Order is not join order.
/// </summary>
public abstract class GameSystem<TComponent> : GameSystem where TComponent : class
{
    internal sealed override Type ComponentType => typeof(TComponent);

    internal sealed override GameSystemRegistry.ComponentSet NewSet() => new GameSystemRegistry.ComponentSet<TComponent>();

    /// <summary>
    /// Runs <paramref name="action"/> for each scene at <paramref name="stage"/> with that scene's enabled components.
    /// A component that joins during the pass is handed over from the next one. Call from the constructor.
    /// </summary>
    protected void Listen(SceneStage stage, ComponentsAction<TComponent> action, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(action);
        Listen(stage, scene => GameSystemRegistry.Run(scene, this, action), order);
    }

    /// <summary>The enabled components in <paramref name="scene"/>, read outside a pass.</summary>
    internal SceneComponents<TComponent> ComponentsIn(SceneInstance scene) => new(GameSystemRegistry.SetFor<TComponent>(scene, this).Items);
}
