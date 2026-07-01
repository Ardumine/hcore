using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

internal sealed class SpawnCommand : ICommand
{
    public string Name => "spawn";
    public string Description => "spawn <module> <instance>   Create a new instance (does not run it)";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 3, "usage: spawn <module-name> <instance-name>");
        ctx.Host.Spawn<IModule>(args[1], args[2]);
        ctx.Out.WriteLine($"spawned '{args[2]}' from '{args[1]}' (at /proc/{args[2]})");
    }
}

internal sealed class RunCommand : ICommand
{
    public string Name => "run";
    public string Description => "run <instance>   Run an already-spawned instance by /proc path";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: run <module-name>");
        var module = ctx.Host.GetModuleInterface<IRunnable>(args[1]);
        module.Run();
    }
}

internal sealed class KillCommand : ICommand
{
    public string Name => "kill";
    public string Description => "kill <instance>   Kill an instance and its children (privileged)";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: kill <instance>");
        ctx.Host.Kill(args[1]);
        ctx.Out.WriteLine($"killed '{args[1]}'");
    }
}
