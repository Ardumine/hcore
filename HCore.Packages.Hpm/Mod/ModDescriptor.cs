using HCore.Modules.Base;

namespace HCore.Packages.Hpm.Mod;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Hpm.Mod";
    public string FriendlyName => "HCore Package Manager";
    public Type ImplementType => typeof(HpmImplement);
    public Type InterfaceType => typeof(IOneshotCommand);
}
