using HCore.Modules.Base;

namespace HCore.Packages.Usb.Usb;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Usb.Usb";

    public string FriendlyName => "Demo USB controller";
    public Type ImplementType => typeof(UsbModuleImplement);

    public Type InterfaceType => typeof(IUsb);
}
