using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

/// <summary>
/// <c>service &lt;start|stop|restart|status|list&gt; [name]</c> — reaches the
/// init module (the <see cref="IServiceManager"/> at <c>/proc/init</c>) to
/// manage <c>/etc/services</c> entries. If init is unreachable, prints an error.
/// </summary>
internal sealed class ServiceCommand : ICommand
{
    public string Name => "service";
    public string Description => "service <start|stop|restart|status|list> [name]   Manage /etc/services";

    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: service <start|stop|restart|status|list> [name]");

        IServiceManager manager;
        try
        {
            manager = ctx.Host.GetModuleInterface<IServiceManager>("init");
        }
        catch (Exception ex)
        {
            ctx.Out.WriteLine($"service: init manager unavailable: {ex.Message}");
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "start":
                ShellContext.RequireArgs(args, 3, "usage: service start <name>");
                ctx.Out.WriteLine($"{args[2]}: {manager.StartService(args[2])}");
                return;
            case "stop":
                ShellContext.RequireArgs(args, 3, "usage: service stop <name>");
                ctx.Out.WriteLine($"{args[2]}: {manager.StopService(args[2])}");
                return;
            case "restart":
                ShellContext.RequireArgs(args, 3, "usage: service restart <name>");
                ctx.Out.WriteLine($"{args[2]}: {manager.RestartService(args[2])}");
                return;
            case "status":
                ShellContext.RequireArgs(args, 3, "usage: service status <name>");
                ctx.Out.WriteLine($"{args[2]}: {manager.GetStatus(args[2])}");
                return;
            case "list":
                foreach (var info in manager.ListServices())
                {
                    ctx.Out.WriteLine($"{info.Name,-20} {info.Status}");
                }
                return;
            default:
                ctx.Out.WriteLine($"service: unknown sub-command '{sub}'");
                return;
        }
    }
}
