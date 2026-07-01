namespace HCore.Modules.Base;

/// <summary>
/// A USB device child module (Design D demo). Lives in Base — not in the Usb
/// package — so a caller in a different package can hold it as more than the
/// empty <see cref="IModule"/> marker.
/// </summary>
public interface IUsbDevice : IModule
{
    string Serial { get; }
    string Location { get; }
    byte[] Read(int len);
}
