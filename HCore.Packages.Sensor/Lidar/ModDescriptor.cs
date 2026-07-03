using HCore.Modules.Base;
using HCore.Modules.Robotics;

namespace HCore.Packages.Sensor.Lidar;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Sensor.Lidar";

    public string FriendlyName => "Demo lidar sensor (data producer)";
    public Type ImplementType => typeof(LidarImplement);

    public Type InterfaceType => typeof(ILidar);
}
