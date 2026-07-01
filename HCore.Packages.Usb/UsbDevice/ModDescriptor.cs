using HCore.Modules.Base;

namespace HCore.Packages.Usb.UsbDevice;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Usb.UsbDevice";

    public string FriendlyName => "Demo USB device";
    public Type ImplementType => typeof(UsbDeviceImplement);

    public Type InterfaceType => typeof(IUsbDevice);
}
