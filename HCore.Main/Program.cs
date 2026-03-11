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


    public static void Main()
    {
        // Create base logger
        var logyt = new ConsoleLogyt("HCore");

        _vfs = new FileSystem();

        logyt.I("Starting...");
        Init();

        // Get the init module
        var initModule = GetAndCreateInitModule(_loadedModuleDescriptors.FirstOrDefault(lmd => lmd.DeclaredDescriptor.Name == "HCore.Modules.HInit.Init")!);// "/mods/HCore.Modules.TestDemo.Module2"
        
        // Run init
        logyt.I("Running init...");
        initModule.Run();

        // After the init module finishes, the HCore will stop
        logyt.I("HCore done!");
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
            //Todo: Handle if mpd does not exist
            using (var fileStream = _vfs.OpenFileStream(Path.Join(dir.Path, "mpd")))
            using (var streamReader = new StreamReader(fileStream))
            {

                try
                {
                    dllName = streamReader.ReadLine();
                    if (string.IsNullOrEmpty(dllName))
                    {
                        Console.WriteLine($"Error read dll: null");
                        continue;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error read dll: {e.Message} {e}");
                    continue;
                }

                try
                {
                    pdbName = streamReader.ReadLine();
                    if (string.IsNullOrEmpty(dllName))
                    {
                        Console.WriteLine($"Warn read pdb: null");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Warn read pdb: {e.Message} {e}");
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





    private static BaseImplement? CreateModuleInstance(Type implementType)
    {
        return Activator.CreateInstance(implementType) as BaseImplement;
    }

    private static void AttachModuleVfs(BaseImplement instance, LoadedModuleDescriptor loadedModuleDescriptor)
    {
        var moduleWorkingDirectory = loadedModuleDescriptor.ParentModPack.ModPackInfo.Path;
        var moduleVfs = new ModuleFileSystemProxy(_vfs, _vfsModuleProxyLock, moduleWorkingDirectory);
        instance.AttachVfs(moduleVfs);
    }

    private static IInitModule GetAndCreateInitModule(LoadedModuleDescriptor loadedModuleDescriptor)
    {
        // Get the module descriptor
        var modDesc = loadedModuleDescriptor.DeclaredDescriptor;

        // Initiate an instance of the module
        var instance = CreateModuleInstance(modDesc.ImplementType);

        if (instance == null)
            throw new Exception($"Could not create instance for {modDesc.ImplementType}");

        AttachModuleVfs(instance, loadedModuleDescriptor);

        if (instance is not IInitModule initModule)
            throw new Exception($"Module {modDesc.ImplementType} is not an init module");

        return initModule;
    }

    // This create a module.
    static void CreateModuleInstance(LoadedModuleDescriptor loadedModuleDescriptor)
    {

    }

}