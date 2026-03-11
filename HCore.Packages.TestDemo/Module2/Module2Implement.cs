using HCore.Modules.Base;

namespace HCore.Packages.TestDemo.Module2;

public class Module2Implement : BaseImplement, IModule2
{
    public void Run()
    {
        Console.WriteLine($"Run Module 2!");

        // Later this will call the Func1 on the Module1
    }
}