using System.Reflection;
using HCore.Main.Vfs;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// Kernel-space implementation of the <c>@forge</c> service (<see cref="IForge"/>).
/// Bridges the VFS and the host filesystem for the build step, and hot-loads a
/// freshly built pack into the live <see cref="ModuleHost"/> descriptor table.
/// </summary>
public sealed class ForgeService : IForge
{
    private readonly FileSystem _vfs;
    private readonly string _fsRoot;
    private readonly ModuleHost _host;

    public ForgeService(FileSystem vfs, string fsRoot, ModuleHost host, string referenceDir)
    {
        _vfs = vfs;
        _fsRoot = Path.GetFullPath(fsRoot);
        _host = host;
        ReferenceDir = referenceDir;
    }

    public string ReferenceDir { get; }

    public string? ToHostPath(string vfsPath)
    {
        if (string.IsNullOrWhiteSpace(vfsPath) || !vfsPath.StartsWith('/'))
            return null;

        // Only the root mount is host-backed; these are synthetic / in-memory.
        foreach (var synthetic in new[] { "/proc", "/dev", "/tmp" })
        {
            if (vfsPath == synthetic || vfsPath.StartsWith(synthetic + "/", StringComparison.Ordinal))
                return null;
        }

        var relative = vfsPath.TrimStart('/');
        var host = Path.GetFullPath(Path.Combine(_fsRoot, relative));

        // Never allow escaping the FS root via '..'.
        if (host != _fsRoot && !host.StartsWith(_fsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        return host;
    }

    public IReadOnlyList<string> InstallPack(string packName)
    {
        var packPath = "/packs/" + packName;
        var mpdPath = Path.Combine(packPath, "mpd");
        if (!_vfs.Exists(mpdPath))
            throw new InvalidOperationException($"Pack '{packName}' has no mpd at {mpdPath}.");

        string dllName;
        using (var mpdStream = _vfs.OpenFileStream(mpdPath, FileMode.Open, FileAccess.Read))
        using (var reader = new StreamReader(mpdStream))
        {
            dllName = reader.ReadLine() ?? throw new InvalidOperationException($"Pack '{packName}' mpd is empty.");
        }

        var dllPath = Path.Combine(packPath, dllName);
        if (!_vfs.Exists(dllPath))
            throw new InvalidOperationException($"Pack '{packName}' DLL '{dllName}' not found.");

        var loadContext = new ModPackAssemblyLoadContext(_vfs, packPath);
        Assembly assembly;
        using (var dllStream = _vfs.GetFile(dllPath).GetStream(FileMode.Open, FileAccess.Read))
        {
            assembly = loadContext.LoadFromStream(dllStream);
        }

        var descriptors = new List<IModuleDescriptor>();
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IModuleDescriptor).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface
                && Activator.CreateInstance(type) is IModuleDescriptor d)
            {
                descriptors.Add(d);
            }
        }

        if (descriptors.Count == 0)
            throw new InvalidOperationException($"Pack '{packName}' declares no IModuleDescriptor.");

        var modPack = new ModPack
        {
            ModPackInfo = new ModPackInfo { Name = packName, DllName = dllName, PdbName = null, Path = packPath },
            AssemblyLoadContext = loadContext,
            DeclaredDecriptors = descriptors,
        };

        var names = new List<string>();
        foreach (var descriptor in descriptors)
        {
            _host.RegisterRuntimeDescriptor(new LoadedModuleDescriptor
            {
                DeclaredDescriptor = descriptor,
                ParentModPack = modPack,
            });
            names.Add(descriptor.Name);
        }

        return names;
    }
}
