using HCore.Modules.Base;

namespace HCore.Packages.TestDemo.Module1;

public class Module1Implement : BaseImplement, IModule1
{
    public void Func1()
    {
        Console.WriteLine($"Func1 was called!");
    }
}