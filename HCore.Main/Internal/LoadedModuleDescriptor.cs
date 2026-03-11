using HCore.Modules.Base;

namespace HCore.Main.Internal;


/// <summary>
/// This sits internally.
/// It represents a module that can be loaded.
/// Just like a Program Executable loaded in memory, that is not a process yet.
/// </summary>
public class LoadedModuleDescriptor
{
    /// <summary>
    /// The descriptor given by the module itself
    /// </summary>
    public required IModuleDescriptor DeclaredDescriptor { get; init; }
    
    /// <summary>
    /// The ModPack that has the module code.
    /// </summary>
    public required ModPack ParentModPack { get; init; }
}