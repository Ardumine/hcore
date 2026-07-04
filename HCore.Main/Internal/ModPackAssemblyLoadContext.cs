using System.Reflection;
using System.Runtime.Loader;
using HCore.Main.Vfs;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

public sealed class ModPackAssemblyLoadContext : AssemblyLoadContext
{
    private readonly FileSystem _vfs;
    private readonly string _modPackPath;

    // Guards promotion of a shared-contract assembly into the Default context so
    // two packages loading concurrently can't each promote their own copy.
    private static readonly object _contractLock = new();

    public ModPackAssemblyLoadContext(FileSystem vfs, string modPackPath)
        : base(isCollectible: false)
    {
        _vfs = vfs;
        _modPackPath = modPackPath;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Anything already resolved in the Default context has ONE identity shared
        // across every module load context (the kernel ABI + shared contracts).
        var sharedAssembly = FindInDefault(assemblyName);
        if (sharedAssembly is not null)
        {
            return sharedAssembly;
        }

        var dependencyPath = Path.Combine(_modPackPath, $"{assemblyName.Name}.dll");
        if (!_vfs.Exists(dependencyPath))
        {
            return null;
        }

        // Shared-contract assemblies (HCore.Modules.*) must have a single identity
        // across all packages — e.g. ILidar is produced by Sensor and consumed by
        // Nexus, and their calls only type-check if both see the same Type. The
        // kernel no longer references these directly, so we promote them into the
        // Default context on first load; later packages then reuse them above.
        if (IsSharedContract(assemblyName))
        {
            lock (_contractLock)
            {
                var raced = FindInDefault(assemblyName);
                if (raced is not null)
                    return raced;

                using var contractStream = _vfs.GetFile(dependencyPath).GetStream(FileMode.Open, FileAccess.Read);
                return Default.LoadFromStream(contractStream);
            }
        }

        // Private dependency: load into this package's own context.
        using var dependencyStream = _vfs.GetFile(dependencyPath).GetStream(FileMode.Open, FileAccess.Read);
        return LoadFromStream(dependencyStream);
    }

    private static Assembly? FindInDefault(AssemblyName assemblyName)
        => Default.Assemblies.FirstOrDefault(
            loaded => AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), assemblyName));

    // Contract assemblies share type identity across module boundaries. By
    // convention these are named HCore.Modules.* (the ABI + domain contracts
    // like HCore.Modules.Robotics).
    private static bool IsSharedContract(AssemblyName assemblyName)
        => assemblyName.Name is { } name
           && name.StartsWith("HCore.Modules.", StringComparison.Ordinal);
}
