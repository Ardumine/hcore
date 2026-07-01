using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

/// <summary>
/// <c>afcp serve &lt;port&gt;</c> / <c>afcp stop</c> /
/// <c>afcp mount &lt;host&gt; &lt;port&gt; &lt;mountpoint&gt;</c> /
/// <c>afcp unmount &lt;mountpoint&gt;</c> / <c>afcp status</c> — drives the
/// kernel-space AFCP bridge (the <see cref="IAfcpKernel"/> at <c>@afcp</c>) to
/// expose the local /proc tree over the network and mount remote peers' trees.
///
/// Reaches the bridge purely through the shared <see cref="IAfcpKernel"/> contract
/// (no AFCP types leak into the shell package).
/// </summary>
internal sealed class AfcpCommand : ICommand
{
    public string Name => "afcp";
    public string Description =>
        "afcp serve <port> | stop | mount <host> <port> <mount> | unmount <mount> | status | test";

    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: afcp serve <port> | stop | mount <host> <port> <mount> | unmount <mount> | status");

        IAfcpKernel afcp;
        try
        {
            afcp = ctx.Host.GetModuleInterface<IAfcpKernel>("@afcp");
        }
        catch (Exception ex)
        {
            ctx.Out.WriteLine($"afcp: bridge unavailable: {ex.Message}");
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "serve":
                ShellContext.RequireArgs(args, 3, "usage: afcp serve <port>");
                ctx.Out.WriteLine(afcp.Serve(int.Parse(args[2])));
                return;
            case "stop":
                ctx.Out.WriteLine(afcp.StopServe());
                return;
            case "mount":
                ShellContext.RequireArgs(args, 5, "usage: afcp mount <host> <port> <mountpoint>");
                ctx.Out.WriteLine(afcp.Mount(args[2], int.Parse(args[3]), args[4]));
                return;
            case "unmount":
                ShellContext.RequireArgs(args, 3, "usage: afcp unmount <mountpoint>");
                ctx.Out.WriteLine(afcp.Unmount(args[2]));
                return;
            case "status":
                ctx.Out.WriteLine(afcp.Status());
                return;
            case "test":
                ctx.Out.WriteLine(afcp.SelfTest());
                return;
            default:
                ctx.Out.WriteLine($"afcp: unknown sub-command '{sub}'");
                return;
        }
    }
}
