using HCore.Modules.Base;
using HCore;

namespace HCore.Packages.Greeter
{
    public interface IGreeterModule : IRunnable
    {
    }

    public class GreeterModule : BaseImplement, IGreeterModule
    {
        public void Run()
        {
            Logger.I("Hello from the forged module!");
        }
    }

    public class GreeterDescriptor : IModuleDescriptor
    {
        public string Name => "Demo.Greeter";
        public string FriendlyName => "Demo Greeter";
        public Type InterfaceType => typeof(IGreeterModule);
        public Type ImplementType => typeof(GreeterModule);
    }
}
