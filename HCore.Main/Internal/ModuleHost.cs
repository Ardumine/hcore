using HCore.Main.Vfs;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// The kernel's process table and the broker for inter-module calls. It lives in
/// kernel space (HCore.Main) and is reached by user-space modules only through
/// the <see cref="IModuleHost"/> "system call" surface the kernel injects.
///
/// Instances are keyed by their INSTANCE name (their /proc identity):
///   * a singleton's instance name is its module name;
///   * a spawned instance has a custom name chosen by the caller.
/// </summary>
public sealed class ModuleHost : IModuleHost
{
    private readonly FileSystem _vfs;
    private readonly object _vfsProxyLock;
    private readonly IReadOnlyList<LoadedModuleDescriptor> _descriptors;

    private readonly Dictionary<string, RunningInstance> _instances = [];
    private readonly object _instancesLock = new();

    public ModuleHost(FileSystem vfs, object vfsProxyLock, IReadOnlyList<LoadedModuleDescriptor> descriptors)
    {
        _vfs = vfs;
        _vfsProxyLock = vfsProxyLock;
        _descriptors = descriptors;
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

            return Cast<T>(instance, instanceName);
        }
    }

    /// <summary>
    /// CREATE a new instance of a module (by module name) under a chosen instance
    /// name, without running it. This is the only operation that resolves the
    /// concrete implementation type (via the descriptor registry).
    /// </summary>
    public T Spawn<T>(string moduleName, string instanceName) where T : IModule
    {
        lock (_instancesLock)
        {
            if (_instances.ContainsKey(instanceName))
            {
                throw new InvalidOperationException($"An instance named '{instanceName}' is already running.");
            }

            var instance = Create(FindDescriptor(moduleName), instanceName);
            _instances[instanceName] = instance;
            return Cast<T>(instance, instanceName);
        }
    }

    // --- kernel-internal helpers ---

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

    /// <summary>Snapshot of running instances, used to render /proc.</summary>
    public IReadOnlyList<RunningModuleInfo> GetRunningModules()
    {
        lock (_instancesLock)
        {
            return _instances.Values
                .Select(ri => new RunningModuleInfo(
                    ri.InstanceName, ri.Descriptor.Name, ri.Descriptor.FriendlyName,
                    ri.Descriptor.ImplementType, ri.Descriptor.InterfaceType))
                .ToArray();
        }
    }

    private LoadedModuleDescriptor FindDescriptor(string moduleName)
        => _descriptors.FirstOrDefault(d => d.DeclaredDescriptor.Name == moduleName)
           ?? throw new InvalidOperationException($"No module registered with name '{moduleName}'.");

    private RunningInstance Create(LoadedModuleDescriptor loaded, string instanceName)
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
        instance.AttachHost(this);

        return new RunningInstance(instanceName, instance, descriptor);
    }

    private static T Cast<T>(RunningInstance instance, string id) where T : IModule
        => instance.Instance is T typed
            ? typed
            : throw new InvalidOperationException($"Instance '{id}' does not implement {typeof(T).FullName}.");

    private sealed record RunningInstance(string InstanceName, BaseImplement Instance, IModuleDescriptor Descriptor);
}

/// <summary>Read-only view of one running instance, used to render /proc.</summary>
public sealed record RunningModuleInfo(
    string InstanceName, string ModuleName, string FriendlyName, Type ImplementType, Type InterfaceType);
