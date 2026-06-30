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
    T GetModuleInterface<T>(string instancePath) where T : IModule;

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
}
