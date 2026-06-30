using HCore.Modules.Base;
using HCore.Packages.TestDemo.Module1;

namespace HCore.Packages.TestDemo.Module2;

public class Module2Implement : BaseImplement, IModule2
{
    public void Run()
    {
        Console.WriteLine($"Run Module 2!");

        // Look up the ALREADY-RUNNING Module1 by its /proc path and call it.
        //   - this is a pure lookup: Module2 holds only the IModule1 interface,
        //     so it could never construct Module1 — it can only find a running one;
        //   - Func1() is the message; whatever it returns would be plain data.
        // Module1 must already be spawned at /proc/module1 (e.g. via the shell:
        //   spawn HCore.Modules.TestDemo.Module1 module1).
        var module1 = Host.GetModuleInterface<IModule1>("/proc/module1");
        module1.Func1();
    }
}
