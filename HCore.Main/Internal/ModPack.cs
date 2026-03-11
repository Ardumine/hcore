using HCore.Modules.Base;
using System.Runtime.Loader;

namespace HCore.Main.Internal;

public class ModPack
{
    public required ModPackInfo ModPackInfo {get; init;}

    public required AssemblyLoadContext AssemblyLoadContext { get; init; }
    
    /// <summary>
    /// The descriptors given by the modules themselves
    /// </summary>
    public required List<IModuleDescriptor> DeclaredDecriptors { get; init; }
}