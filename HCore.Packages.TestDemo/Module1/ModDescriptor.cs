using HCore.Modules.Base;

namespace HCore.Packages.TestDemo.Module1;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Modules.TestDemo.Module1";

    public string FriendlyName => "Demo module1";
    public Type ImplementType => typeof(Module1Implement);

    public Type InterfaceType => typeof(IModule1);
}