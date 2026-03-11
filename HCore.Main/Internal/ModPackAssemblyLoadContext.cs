using System.Reflection;
using System.Runtime.Loader;
using HCore.Main.Vfs;

namespace HCore.Main.Internal;

public sealed class ModPackAssemblyLoadContext : AssemblyLoadContext
{
    private readonly FileSystem _vfs;
    private readonly string _modPackPath;

    public ModPackAssemblyLoadContext(FileSystem vfs, string modPackPath)
        : base(isCollectible: false)
    {
        _vfs = vfs;
        _modPackPath = modPackPath;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var sharedAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(loaded => AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), assemblyName));

        if (sharedAssembly is not null)
        {
            return sharedAssembly;
        }

        var dependencyPath = Path.Combine(_modPackPath, $"{assemblyName.Name}.dll");
        if (!_vfs.Exists(dependencyPath))
        {
            return null;
        }

        using var dependencyStream = _vfs.GetFile(dependencyPath).GetStream(FileMode.Open, FileAccess.Read);
        return LoadFromStream(dependencyStream);
    }
}
