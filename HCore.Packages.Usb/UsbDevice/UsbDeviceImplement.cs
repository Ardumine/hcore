using HCore.Modules.Base;

namespace HCore.Packages.Usb.UsbDevice;

public sealed class UsbDeviceImplement : BaseImplement, IUsbDevice
{
    public string Serial { get; private set; } = "";
    public string Location { get; private set; } = "";

    internal void Init(string serial, string location)
    {
        Serial = serial;
        Location = location;
    }

    public byte[] Read(int len) => new byte[len];

    protected override string? DescribeForProc()
        => $"serial:     {Serial}\nlocation:   {Location}";
}
