namespace HCore.Modules.Base;

/// <summary>
/// The process / IPC "system call" surface a module uses to reach OTHER modules.
/// It is implemented in kernel space (HCore.Main) and injected into each module
/// on creation — the module's only door into the kernel for module management,
/// exactly like <see cref="IModuleFileSystem"/> is its door for files.
///
/// The two operations are deliberately distinct:
///   * <see cref="Spawn{T}"/>             — CREATE an instance. Needs the module
///     name, because only the kernel's descriptor registry knows the concrete
///     implementation type to construct.
///   * <see cref="GetModuleInterface{T}"/> — LOOK UP an already-running instance
///     by its /proc path. Needs only the interface, because the object already
///     exists; the caller can never create anything it only holds an interface to.
/// </summary>
public interface IModuleHost
{
    /// <summary>
    /// Look up an ALREADY-RUNNING instance by its <c>/proc</c> path (e.g.
    /// <c>"/proc/module1"</c>; a bare instance name like <c>"module1"</c> is also
    /// accepted) and return it as the interface <typeparamref name="T"/>.
    /// This never creates anything.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Nothing is running at that path, or the instance does not implement <typeparamref name="T"/>.
    /// </exception>
    T GetModuleInterface<T>(string instancePath) where T : class, IModule;

    /// <summary>
    /// Create a NEW instance of <paramref name="moduleName"/> under the chosen
    /// <paramref name="instanceName"/> (like exec). The instance is constructed
    /// and registered at <c>/proc/&lt;instanceName&gt;</c> but is NOT run — running
    /// (if the module is <see cref="IRunnable"/>) is a separate step. The same
    /// module may be spawned many times under different instance names.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// The module name is unknown, an instance with that name already exists, or
    /// the new instance does not implement <typeparamref name="T"/>.
    /// </exception>
    T Spawn<T>(string moduleName, string instanceName) where T : IModule;

    /// <summary>
    /// Create a CHILD of the calling module (the owner is implicit: whichever
    /// instance was injected with this <see cref="IModuleHost"/>), resolved by
    /// concrete implementation type <typeparamref name="TImpl"/>. The child's
    /// <paramref name="init"/> runs BEFORE it is published, so it is never
    /// observable half-built. Appears at <c>/proc/&lt;owner&gt;/&lt;leafName&gt;</c>;
    /// destroying the owner structurally reaps it.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// The owner is no longer running, a child named <paramref name="leafName"/>
    /// already exists, or <paramref name="leafName"/> contains '/'.
    /// </exception>
    TImpl SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule;

    /// <summary>
    /// Cross-package escape hatch for <see cref="SpawnChild{TImpl}"/>: create a
    /// child of the calling module by module NAME (like <see cref="Spawn{T}"/>)
    /// instead of by concrete type. Returns the child's interface, which
    /// therefore must live in a shared contract assembly (e.g. <c>HCore.Modules.Base</c>)
    /// for the caller to be able to use it at all.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// The module name is unknown, the owner is no longer running, a child named
    /// <paramref name="leafName"/> already exists, or the new instance does not
    /// implement <typeparamref name="T"/>.
    /// </exception>
    T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule;

    /// <summary>
    /// Kill a child OF THE CALLING module. Owner-scoped: throws if
    /// <paramref name="leafName"/> is not actually a child of this module.
    /// Cascades to the child's own descendants, leaf-first.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// No such child, or it is not owned by the calling module.
    /// </exception>
    void KillChild(string leafName);

    /// <summary>
    /// Privileged cascade kill of ANY instance by its <c>/proc</c> path (or bare
    /// instance name), regardless of ownership. Reaps the target and every
    /// transitive descendant, leaf-first. No capability model exists yet, so
    /// this is intentionally unrestricted — the shell's <c>kill</c> command uses
    /// it; a module using it on another module's subtree is a documented gap.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">Nothing is running at that path.</exception>
    void Kill(string instancePath);

    /// <summary>
    /// Non-generic instance lookup by /proc path (or bare name). Accepts the
    /// same path shapes as <see cref="GetModuleInterface{T}"/>. Kernel-space
    /// <c>@</c>-services are NOT reachable here.
    /// </summary>
    bool TryResolveInstance(string instancePath, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IModule instance);
}
