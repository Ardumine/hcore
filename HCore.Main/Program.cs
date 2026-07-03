using HCore.Main.Vfs;
using System.Reflection;
using System.Runtime.Loader;
using HCore.Modules.Base;
using Logyt;
using HCore.Main.Internal;

namespace HCore.Main;

internal static class Program
{
    private static FileSystem _vfs = null!;
    private static readonly object _vfsModuleProxyLock = new();
    private static readonly List<ModPack> _existingModPacks = [];
    private static readonly List<LoadedModuleDescriptor> _loadedModuleDescriptors = [];
    private static ConsoleLogyt _kernelLog = null!;


    public static void Main()
    {
        // Create base logger
        _kernelLog = new ConsoleLogyt("HCore");

        _vfs = new FileSystem();

        _kernelLog.I("Starting...");
        Init();

        // The module host knows every loaded module and brokers references
        // between them. It is what a module talks to when it wants to call
        // another module.
        var dataHost = new DataHost(_vfs);
        var host = new ModuleHost(_vfs, _vfsModuleProxyLock, dataHost, _loadedModuleDescriptors);

        // Expose the running modules as a live /proc tree (like Linux/Plan 9).
        _vfs.Mount("/proc", new ProcFileSystem(host, dataHost));

        // Spawn the AFCP Nexus connector module (replaces the kernel-space bridge).
        // Nexus provides the IAfcpKernel shell-facing contract, receives privileged
        // driver doors via IDriverModule, and registers as the IRemoteMountHook so
        // the kernel transparently redirects remote subscribe/call through it.
        var nexus = host.Spawn<IAfcpKernel>("HCore.Packages.Nexus.Nexus", "nexus");
        if (nexus is IDriverModule driver)
            driver.Init(_vfs, dataHost, host);
        if (nexus is IRemoteMountHook hook)
        {
            host.RegisterRemoteMountHook(hook);
            dataHost.RegisterRemoteMountHook(hook);
        }
        host.RegisterKernelService("@afcp", nexus);

        // Register the composite VFS as a kernel service so privileged modules
        // (e.g. Nexus) can mount/unmount and navigate the full tree.
        host.RegisterKernelService("@vfs", _vfs);

        // The init module (PID 1) — the kernel spawns it explicitly, then runs it.
        var initModule = host.Spawn<IRunnable>("HCore.Packages.HInit.Init", "init");

        // Run init
        _kernelLog.I("Running init...");
        initModule.Run();

        // After the init module finishes, the HCore will stop
        _kernelLog.I("HCore done!");
    }

    static void Init()
    {
        var logyt = new ConsoleLogyt("Init");

        //Ex: mount fs, start mods

        // Mount root
        logyt.I("Mounting root...");
        _vfs.Mount("/", new HostFileSystem("/home/ardumine/hort/hcore/FS"));
        _vfs.Mount("/dev", new DeviceFileSystem());
        _vfs.Mount("/tmp", new MemoryFileSystem("tmpfs"));


        // Load ModPacks
        var modPacksInfo = ListModPacks("/packs");
        logyt.I($"Found {modPacksInfo.Count} modpacks!");

        logyt.I($"Loading all modpacks...");
        var loadedModuleDescriptors = LoadModPacksAndRegisterModules(modPacksInfo);
        logyt.I($"Registered {loadedModuleDescriptors.Count} modules!");

    }


    private static List<ModPackInfo> ListModPacks(string path = "/packs")
    {
        var modPackInfos = new List<ModPackInfo>();
        foreach (var dir in _vfs.GetDirectory(path).EnumerateDirectories())
        {
            string? dllName = "";
            string? pdbName = null;

            //mpd: mod pack descriptor
            var mpdPath = Path.Join(dir.Path, "mpd");
            if (!_vfs.Exists(mpdPath))
            {
                _kernelLog.W($"Skipping pack '{new DirectoryInfo(dir.Path).Name}': missing mpd descriptor");
                continue;
            }

            using (var fileStream = _vfs.OpenFileStream(mpdPath, FileMode.Open, FileAccess.Read))
            using (var streamReader = new StreamReader(fileStream))
            {

                try
                {
                    dllName = streamReader.ReadLine();
                    if (string.IsNullOrEmpty(dllName))
                    {
                        _kernelLog.W("Error reading mpd: dll name is null");
                        continue;
                    }
                }
                catch (Exception e)
                {
                    _kernelLog.Log(MessageType.Error, $"Error reading mpd dll name: {e.Message}");
                    continue;
                }

                try
                {
                    pdbName = streamReader.ReadLine();
                }
                catch (Exception e)
                {
                    _kernelLog.W($"Warning reading pdb: {e.Message}");
                    pdbName = null;
                }
            }
            modPackInfos.Add(new ModPackInfo
            {
                Name = new DirectoryInfo(dir.Path).Name,
                DllName = dllName!,
                PdbName = pdbName,
                Path = dir.Path
            });
        }

        return modPackInfos;
    }


    private static List<LoadedModuleDescriptor> LoadModPacksAndRegisterModules(IEnumerable<ModPackInfo> modPackInfos)
    {
        var loadedModuleDescriptors = new List<LoadedModuleDescriptor>();
        foreach (var modPackInfo in modPackInfos)
        {
            var modPackLoadContext = new ModPackAssemblyLoadContext(_vfs, modPackInfo.Path);

            // Discover available modules
            var modPack = new ModPack()
            {
                ModPackInfo = modPackInfo,
                AssemblyLoadContext = modPackLoadContext,
                DeclaredDecriptors = GetModuleDescriptorsFromModPack(modPackInfo, modPackLoadContext)
            };
            loadedModuleDescriptors.AddRange(RegisterModulesFromModPack(modPack));
        }
        return loadedModuleDescriptors;
    }

    private static IEnumerable<LoadedModuleDescriptor> RegisterModulesFromModPack(ModPack modPack)
    {
        var loadedModuleDescriptors = modPack.DeclaredDecriptors.Select(dd => new LoadedModuleDescriptor()
        {
            DeclaredDescriptor = dd,
            ParentModPack = modPack
        });
        _loadedModuleDescriptors.AddRange(loadedModuleDescriptors);

        return loadedModuleDescriptors;
    }

    private static List<IModuleDescriptor> GetModuleDescriptorsFromModPack(ModPackInfo modPackInfo, ModPackAssemblyLoadContext loadContext)
    {
        var descriptors = new List<IModuleDescriptor>();

        Assembly assembly;

        var moduleDllPath = Path.Combine(modPackInfo.Path, modPackInfo.DllName);
        using var moduleDllStream = _vfs.GetFile(moduleDllPath).GetStream(FileMode.Open, FileAccess.Read);

        // Check if no pdb file was provided
        if (modPackInfo.PdbName != null)
        {
            using var modulePdbStream = _vfs.GetFile(Path.Combine(modPackInfo.Path, modPackInfo.PdbName)).GetStream(FileMode.Open, FileAccess.Read);
            assembly = loadContext.LoadFromStream(moduleDllStream, modulePdbStream);
        }
        else
        {
            assembly = loadContext.LoadFromStream(moduleDllStream);
        }


        var types = assembly.GetTypes();

        foreach (var type in types)
            if (typeof(IModuleDescriptor).IsAssignableFrom(type))
                if (Activator.CreateInstance(type) is IModuleDescriptor result)
                    descriptors.Add(result);

        return descriptors;
    }

    //https://stackoverflow.com/questions/40384619/how-to-load-assembly-from-stream-in-net-core





}