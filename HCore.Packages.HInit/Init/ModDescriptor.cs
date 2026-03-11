using HCore.Modules.Base;

namespace HCore.Packages.HInit.Init;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.HInit.Init";

    public string FriendlyName => "HInit init module";
    public Type ImplementType => typeof(InitImplement);

    public Type InterfaceType => typeof(IInit);
}