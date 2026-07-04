using HCore.Modules.Base;

namespace HCore.Packages.HMount.Mount;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.HMount.Mount";

    public string FriendlyName => "fstab mounter";

    public Type ImplementType => typeof(MountImplement);

    public Type InterfaceType => typeof(IMount);
}
