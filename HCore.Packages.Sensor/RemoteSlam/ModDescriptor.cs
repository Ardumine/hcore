using HCore.Modules.Base;

namespace HCore.Packages.Sensor.RemoteSlam;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Sensor.RemoteSlam";

    public string FriendlyName => "Demo remote SLAM consumer (AFCP subscribe-push)";
    public Type ImplementType => typeof(RemoteSlamImplement);

    public Type InterfaceType => typeof(IRemoteSlam);
}
