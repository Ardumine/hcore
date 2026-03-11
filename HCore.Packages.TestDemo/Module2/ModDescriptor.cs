using HCore.Modules.Base;

namespace HCore.Packages.TestDemo.Module2;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.TestDemo.Module2";

    public string FriendlyName => "Demo module 2";
    public Type ImplementType => typeof(Module2Implement);

    public Type InterfaceType => typeof(IModule2);
}