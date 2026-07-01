using HCore.Main.Vfs;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// The kernel's process table and the broker for inter-module calls. It lives in
/// kernel space (HCore.Main) and is reached by user-space modules only through
/// the <see cref="IModuleHost"/> "system call" surface the kernel injects.
///
/// Instances are keyed by their INSTANCE name (their /proc identity):
///   * a top-level instance's name is chosen by whoever spawned it (no '/');
///   * a child's name is the composite "&lt;owner&gt;/&lt;leaf&gt;", and its
///     <see cref="RunningInstance.ParentName"/> records the owning instance —
///     the single edge that makes /proc nesting and cascade kill authoritative.
/// </summary>
public sealed class ModuleHost : IModuleHost
{
    private readonly FileSystem _vfs;
    private readonly object _vfsProxyLock;
    private readonly IReadOnlyList<LoadedModuleDescriptor> _descriptors;
    private readonly Dictionary<Type, LoadedModuleDescriptor> _byImplType;

    private readonly Dictionary<string, RunningInstance> _instances = [];
    private readonly object _instancesLock = new();

    public ModuleHost(FileSystem vfs, object vfsProxyLock, IReadOnlyList<LoadedModuleDescriptor> descriptors)
    {
        _vfs = vfs;
        _vfsProxyLock = vfsProxyLock;
        _descriptors = descriptors;

        _byImplType = new Dictionary<Type, LoadedModuleDescriptor>();
        foreach (var descriptor in descriptors)
        {
            var implType = descriptor.DeclaredDescriptor.ImplementType;
            if (!_byImplType.TryAdd(implType, descriptor))
            {
                throw new InvalidOperationException(
                    $"Implementation type '{implType.FullName}' is declared by both " +
                    $"'{_byImplType[implType].DeclaredDescriptor.Name}' and '{descriptor.DeclaredDescriptor.Name}' — " +
                    "SpawnChild<TImpl> requires a unique implementation type per module.");
            }
        }
    }

    // --- IModuleHost: the system calls available to user-space modules ---

    /// <summary>
    /// LOOK UP an already-running instance by its /proc path (or bare instance
    /// name) and return it as <typeparamref name="T"/>. Never creates anything —
    /// it only needs the interface because the object already exists.
    /// </summary>
    public T GetModuleInterface<T>(string instancePath) where T : IModule
    {
        var instanceName = InstanceNameFromPath(instancePath);
        lock (_instancesLock)
        {
            if (!_instances.TryGetValue(instanceName, out var instance))
            {
                throw new InvalidOperationException(
                    $"No running instance at '/proc/{instanceName}'. Spawn it first.");
            }

            return Cast<T>(instance.Instance, instanceName);
        }
    }

    /// <summary>
    /// CREATE a new top-level instance of a module (by module name) under a
    /// chosen instance name, without running it. This is the only operation
    /// that resolves the concrete implementation type by NAME (see
    /// <see cref="SpawnChildByType{TImpl}"/> for the by-type equivalent).
    /// </summary>
    public T Spawn<T>(string moduleName, string instanceName) where T : IModule
    {
        RejectSlash(instanceName, "Instance name");

        lock (_instancesLock)
        {
            if (_instances.ContainsKey(instanceName))
            {
                throw new InvalidOperationException($"An instance named '{instanceName}' is already running.");
            }

            var instance = Create(FindDescriptor(moduleName), instanceName, parentName: null);
            _instances[instanceName] = instance;
            return Cast<T>(instance.Instance, instanceName);
        }
    }

    // These three only make sense through an owner-bound host: the raw kernel
    // ModuleHost has no instance of its own to be the "owner" of a child. Every
    // created instance (see Create) is instead injected with a ScopedModuleHost,
    // which is what actually implements them (via the internal *Core methods).
    public TImpl SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule => throw NotScoped();

    public T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule => throw NotScoped();

    public void KillChild(string leafName) => throw NotScoped();

    /// <summary>
    /// Privileged cascade kill of ANY instance by /proc path — unrestricted,
    /// unlike the owner-scoped <see cref="KillChildCore"/>. Reaps the target and
    /// every transitive descendant, leaf-first.
    /// </summary>
    public void Kill(string instancePath)
    {
        var name = InstanceNameFromPath(instancePath);
        List<BaseImplement> reaped;
        lock (_instancesLock)
        {
            if (!_instances.ContainsKey(name))
            {
                throw new InvalidOperationException($"No running instance at '/proc/{name}'.");
            }

            reaped = KillLocked(name);
        }

        NotifyKilled(reaped);
    }

    private static InvalidOperationException NotScoped() => new(
        "This operation requires an owner-bound module host — call it through a module's own injected Host, not the kernel's raw ModuleHost.");

    // --- kernel-internal helpers reached only through ScopedModuleHost ---

    /// <summary>
    /// Create a child of <paramref name="ownerName"/>, resolved by concrete
    /// implementation type (the primary <c>SpawnChild</c> form — typed init,
    /// no cast, no magic string).
    /// </summary>
    internal TImpl SpawnChildByType<TImpl>(string ownerName, string leafName, Action<TImpl>? init) where TImpl : IModule
    {
        if (!_byImplType.TryGetValue(typeof(TImpl), out var descriptor))
        {
            throw new InvalidOperationException($"No module registered with implementation type '{typeof(TImpl).FullName}'.");
        }

        var instance = SpawnChildCore(ownerName, descriptor, leafName,
            init is null ? null : baseInstance => init((TImpl)(object)baseInstance));
        return (TImpl)(object)instance;
    }

    /// <summary>
    /// Create a child of <paramref name="ownerName"/> by module NAME — the
    /// cross-package escape hatch when the caller cannot reference the child's
    /// concrete implementation type. Returns the child's interface.
    /// </summary>
    internal T SpawnChildByName<T>(string ownerName, string moduleName, string leafName, Action<T>? init) where T : IModule
    {
        var descriptor = FindDescriptor(moduleName);
        var instance = SpawnChildCore(ownerName, descriptor, leafName,
            init is null ? null : baseInstance => init(Cast<T>(baseInstance, $"{ownerName}/{leafName}")));
        return Cast<T>(instance, $"{ownerName}/{leafName}");
    }

    /// <summary>
    /// Shared machinery for both SpawnChild forms above.
    ///
    /// Concurrency: construct + attach while NOT holding the lock and NOT yet
    /// published, run <paramref name="init"/> unlocked (so a nested SpawnChild
    /// from within init can freely take the lock itself — the outer call has
    /// already released it), then re-acquire and re-check BOTH that the owner
    /// is still running AND the child name is still free before publishing.
    /// The owner re-check matters: without it, a concurrent Kill of the owner
    /// while init runs unlocked would let this child publish under an
    /// already-reaped parent — an orphan the cascade that just ran never saw,
    /// invisible in intent but not in /proc (nested rendering tolerates bare
    /// intermediate directories, so a dead node like this could resurface).
    /// </summary>
    private BaseImplement SpawnChildCore(string ownerName, LoadedModuleDescriptor descriptor, string leafName, Action<BaseImplement>? init)
    {
        RejectSlash(leafName, "Child name");
        var childName = $"{ownerName}/{leafName}";

        RunningInstance child;
        lock (_instancesLock)
        {
            if (!_instances.ContainsKey(ownerName))
            {
                throw new InvalidOperationException($"Owner instance '{ownerName}' is not running.");
            }

            if (_instances.ContainsKey(childName))
            {
                throw new InvalidOperationException($"An instance named '{childName}' is already running.");
            }

            child = Create(descriptor, childName, ownerName);
        }

        init?.Invoke(child.Instance);

        lock (_instancesLock)
        {
            if (!_instances.ContainsKey(ownerName))
            {
                throw new InvalidOperationException(
                    $"Owner instance '{ownerName}' was killed while child '{leafName}' was being initialized; discarding the child.");
            }

            if (_instances.ContainsKey(childName))
            {
                throw new InvalidOperationException($"An instance named '{childName}' is already running.");
            }

            _instances[childName] = child;
        }

        return child.Instance;
    }

    /// <summary>Owner-scoped kill: throws unless <paramref name="leafName"/> is actually a child of <paramref name="ownerName"/>.</summary>
    internal void KillChildCore(string ownerName, string leafName)
    {
        var childName = $"{ownerName}/{leafName}";
        List<BaseImplement> reaped;
        lock (_instancesLock)
        {
            if (!_instances.TryGetValue(childName, out var child) || child.ParentName != ownerName)
            {
                throw new InvalidOperationException($"'{leafName}' is not a child of '{ownerName}'.");
            }

            reaped = KillLocked(childName);
        }

        NotifyKilled(reaped);
    }

    /// <summary>
    /// Assumes <see cref="_instancesLock"/> is already held. Collects
    /// <paramref name="name"/> and every transitive descendant (matched via
    /// <see cref="RunningInstance.ParentName"/>), removes them all from
    /// <see cref="_instances"/>, and returns them leaf-first (deepest
    /// descendants first, the target itself last). Deliberately does NOT call
    /// <see cref="BaseImplement.OnKilled"/> here — that is module-authored code
    /// and, like <see cref="BaseImplement.DescribeForProc"/>, must never run
    /// while the process table is locked. Callers release the lock and invoke
    /// <see cref="NotifyKilled"/> on the returned snapshot afterwards.
    /// </summary>
    private List<BaseImplement> KillLocked(string name)
    {
        var levels = new List<string> { name };
        var frontier = new Queue<string>();
        frontier.Enqueue(name);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in _instances.Values.Where(ri => ri.ParentName == current))
            {
                levels.Add(child.InstanceName);
                frontier.Enqueue(child.InstanceName);
            }
        }

        // `levels` is root-first (breadth-first); reverse for a leaf-first reap order.
        levels.Reverse();

        var reaped = new List<BaseImplement>(levels.Count);
        foreach (var instanceName in levels)
        {
            if (_instances.Remove(instanceName, out var removed))
            {
                reaped.Add(removed.Instance);
            }
        }

        return reaped;
    }

    private static void NotifyKilled(IEnumerable<BaseImplement> reaped)
    {
        foreach (var instance in reaped)
        {
            instance.OnKilled();
        }
    }

    // --- shared low-level helpers ---

    /// <summary>Accept either a "/proc/&lt;name&gt;" path or a bare instance name.</summary>
    private static string InstanceNameFromPath(string instancePath)
    {
        var value = instancePath.Trim();
        if (value.StartsWith("/proc/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["/proc/".Length..];
        }
        else if (value.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Running instances live under /proc; '{instancePath}' is not a valid instance path.");
        }

        return value.Trim('/');
    }

    private static void RejectSlash(string name, string what)
    {
        if (name.Contains('/'))
        {
            throw new InvalidOperationException($"{what} '{name}' cannot contain '/'; nesting is only created via SpawnChild.");
        }
    }

    /// <summary>Snapshot of running instances, used to render /proc.</summary>
    public IReadOnlyList<RunningModuleInfo> GetRunningModules()
    {
        List<RunningInstance> snapshot;
        lock (_instancesLock)
        {
            snapshot = _instances.Values.ToList();
        }

        // DescribeForProc() is module code — never called while _instancesLock is held.
        return snapshot
            .Select(ri => new RunningModuleInfo(
                ri.InstanceName, ri.Descriptor.Name, ri.Descriptor.FriendlyName,
                ri.Descriptor.ImplementType, ri.Descriptor.InterfaceType, ri.Instance.DescribeForProc()))
            .ToArray();
    }

    private LoadedModuleDescriptor FindDescriptor(string moduleName)
        => _descriptors.FirstOrDefault(d => d.DeclaredDescriptor.Name == moduleName)
           ?? throw new InvalidOperationException($"No module registered with name '{moduleName}'.");

    private RunningInstance Create(LoadedModuleDescriptor loaded, string instanceName, string? parentName)
    {
        var descriptor = loaded.DeclaredDescriptor;
        if (Activator.CreateInstance(descriptor.ImplementType) is not BaseImplement instance)
        {
            throw new InvalidOperationException(
                $"Could not create instance for module '{descriptor.Name}' ({descriptor.ImplementType}).");
        }

        // Inject the kernel services — the module's only door back into the kernel.
        var workingDirectory = loaded.ParentModPack.ModPackInfo.Path;
        instance.AttachVfs(new ModuleFileSystemProxy(_vfs, _vfsProxyLock, workingDirectory));
        instance.AttachHost(new ScopedModuleHost(this, instanceName));
        instance.AttachInstanceName(instanceName);

        return new RunningInstance(instanceName, instance, descriptor, parentName);
    }

    private static T Cast<T>(BaseImplement instance, string id) where T : IModule
        => instance is T typed
            ? typed
            : throw new InvalidOperationException($"Instance '{id}' does not implement {typeof(T).FullName}.");

    private sealed record RunningInstance(string InstanceName, BaseImplement Instance, IModuleDescriptor Descriptor, string? ParentName);
}

/// <summary>Read-only view of one running instance, used to render /proc.</summary>
public sealed record RunningModuleInfo(
    string InstanceName, string ModuleName, string FriendlyName, Type ImplementType, Type InterfaceType, string? Details);
