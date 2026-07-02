using HCore.Modules.Base;

namespace HCore.Packages.DemoPkg;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.DemoPkg.Mod";
    public string FriendlyName => "Demo Package";
    public Type ImplementType => typeof(DemoImplement);
    public Type InterfaceType => typeof(IOneshotCommand);
}
