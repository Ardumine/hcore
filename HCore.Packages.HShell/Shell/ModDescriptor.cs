using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.HShell.Shell";
    public string FriendlyName => "HCore interactive shell";
    public Type ImplementType => typeof(ShellImplement);
    public Type InterfaceType => typeof(IShell);
}
