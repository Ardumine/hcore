using HCore.Modules.Base;
using System;

namespace HCore.Packages.Ping
{
    public interface IPing : IRunnable
    {
    }

    public class PingImpl : BaseImplement, IPing
    {
        public void Run()
        {
            Logger.I("PONG-42-UNIQUE");
        }
    }

    public class PingDescriptor : IModuleDescriptor
    {
        public string Name => "Demo.Ping";
        public string FriendlyName => "Ping Demo";
        public Type ImplementType => typeof(PingImpl);
        public Type InterfaceType => typeof(IPing);
    }
}
