using HCore.Modules.Base;

namespace HCore.Packages.DemoPkg;

/// <summary>
/// Demo oneshot command — echoes arguments and VFS state to prove the
/// spawn/run/kill pipeline works end-to-end.
/// </summary>
public sealed class DemoImplement : BaseImplement, IOneshotCommand
{
    private string[] _args = [];

    public void SetArguments(string[] args) => _args = args;

    public void Run()
    {
        Console.WriteLine($"demo: hello from {InstanceName}");
        Console.WriteLine($"demo: got {_args.Length} arguments:");
        for (int i = 0; i < _args.Length; i++)
            Console.WriteLine($"  [{i}] = {_args[i]}");
        Console.WriteLine($"demo: VFS working directory = {Vfs.WorkingDirectory}");
        Console.WriteLine($"demo: / exists = {Vfs.Exists("/")}");
    }
}
