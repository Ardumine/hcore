using HCore.Modules.Base;

namespace HCore.Packages.Sensor.Slam;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Sensor.Slam";

    public string FriendlyName => "Demo SLAM consumer (data subscriber)";
    public Type ImplementType => typeof(SlamImplement);

    public Type InterfaceType => typeof(ISlam);
}
