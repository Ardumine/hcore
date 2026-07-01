using System.IO;
using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

/// <summary>
/// The execution context handed to every command: the module's VFS and Host
/// syscall surfaces, the output writer, and an exit signal the REPL polls.
/// </summary>
public sealed class ShellContext
{
    public IModuleFileSystem Vfs { get; }
    public IModuleHost Host { get; }
    public TextWriter Out { get; }
    public bool ExitRequested { get; private set; }

    public ShellContext(IModuleFileSystem vfs, IModuleHost host, TextWriter? output = null)
    {
        Vfs = vfs;
        Host = host;
        Out = output ?? Console.Out;
    }

    public void RequestExit() => ExitRequested = true;

    public static void RequireArgs(IReadOnlyList<string> args, int minimum, string usage)
    {
        if (args.Count < minimum)
        {
            throw new InvalidOperationException(usage);
        }
    }
}

/// <summary>
/// A single shell command. Registered by name in a <see cref="CommandRegistry"/>
/// and dispatched for both the interactive REPL and <c>RunScript</c>.
/// </summary>
public interface ICommand
{
    string Name { get; }
    string Description { get; }
    void Execute(IReadOnlyList<string> args, ShellContext ctx);
}
