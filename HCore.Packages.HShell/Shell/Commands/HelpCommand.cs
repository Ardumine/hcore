using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

internal sealed class HelpCommand : ICommand
{
    private readonly CommandRegistry _registry;

    public HelpCommand(CommandRegistry registry) => _registry = registry;

    public string Name => "help";
    public string Description => "Show this help";

    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ctx.Out.WriteLine("Available commands:");
        foreach (var command in _registry.Commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Out.WriteLine($"  {command.Description}");
        }
    }
}

internal sealed class ExitCommand : ICommand
{
    public string Name => "exit";
    public string Description => "exit   Exit the shell";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ctx.Out.WriteLine("Shell exit.");
        ctx.RequestExit();
    }
}
