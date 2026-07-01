using HCore.Modules.Base;
using HCore.Packages.HShell.Shell.Commands;

namespace HCore.Packages.HShell.Shell;

/// <summary>
/// Interactive command shell over the VFS. Driven two ways:
/// <list type="bullet">
/// <item><see cref="Run"/> — the interactive REPL (uses ReadLine for line editing).</item>
/// <item><see cref="RunScript"/> — batch execution of a VFS text file, line by line.</item>
/// </list>
/// Both share the same <see cref="CommandRegistry"/> / dispatch path.
/// </summary>
public class ShellImplement : BaseImplement, IShell
{
    private readonly CommandRegistry _registry = new();

    public ShellImplement()
    {
        RegisterBuiltins();
    }

    /// <summary>
    /// Interactive REPL. Blocks until <c>exit</c> (or EOF on stdin).
    /// </summary>
    public void Run()
    {
        var ctx = NewContext();
        Logger.I("HCore shell started.");
        Console.WriteLine("Type 'help' to list commands. Type 'exit' to quit.");

        ReadLine.HistoryEnabled = true;

        Vfs.SetWorkingDirectory("/");

        while (!ctx.ExitRequested)
        {
            // ReadLine (the editing library) throws when stdin is redirected
            // (no TTY) — fall back to plain Console.ReadLine so the shell still
            // works under pipes / automation.
            string? line;
            try
            {
                line = ReadLine.Read($"{Vfs.WorkingDirectory} $ ");
            }
            catch (InvalidOperationException)
            {
                line = Console.ReadLine();
                if (line is not null)
                {
                    Console.WriteLine($"{Vfs.WorkingDirectory} $ {line}");
                }
            }

            if (line is null)
            {
                break;
            }

            DispatchLine(ctx, line);
        }
    }

    /// <summary>
    /// Execute <paramref name="path"/> line by line. <c>#</c> starts a comment,
    /// blank lines are skipped. Stops on the first failing line (returns
    /// <c>false</c>) or if a command requests exit.
    /// </summary>
    public bool RunScript(string path)
    {
        var ctx = NewContext();

        string text;
        try
        {
            text = Vfs.ReadAllText(path);
        }
        catch (Exception ex)
        {
            ctx.Out.WriteLine($"script: cannot read '{path}': {ex.Message}");
            return false;
        }

        Vfs.SetWorkingDirectory("/");

        foreach (var raw in text.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }

            if (!DispatchLine(ctx, line))
            {
                return false;
            }

            if (ctx.ExitRequested)
            {
                break;
            }
        }

        return true;
    }

    private ShellContext NewContext() => new(Vfs, Host);

    /// <summary>
    /// Parse and execute one line. Returns <c>false</c> if the command threw.
    /// </summary>
    private bool DispatchLine(ShellContext ctx, string line)
    {
        var args = CommandRegistry.ParseArguments(line);
        if (args.Count == 0)
        {
            return true;
        }

        var name = args[0].ToLowerInvariant();
        if (!_registry.TryGet(name, out var command))
        {
            ctx.Out.WriteLine($"unknown command: {name}");
            return true;
        }

        try
        {
            command!.Execute(args, ctx);
            return true;
        }
        catch (Exception ex)
        {
            ctx.Out.WriteLine($"error: {ex.Message}");
            return false;
        }
    }

    private void RegisterBuiltins()
    {
        _registry.Register(new PwdCommand());
        _registry.Register(new CdCommand());
        _registry.Register(new LsCommand());
        _registry.Register(new CatCommand());
        _registry.Register(new MkdirCommand());
        _registry.Register(new RmCommand());
        _registry.Register(new RmDirCommand());
        _registry.Register(new TouchCommand());
        _registry.Register(new ExistsCommand());
        _registry.Register(new MvCommand());
        _registry.Register(new RenameCommand());
        _registry.Register(new WriteCommand());
        _registry.Register(new AppendCommand());
        _registry.Register(new ClearCommand());
        _registry.Register(new SpawnCommand());
        _registry.Register(new RunCommand());
        _registry.Register(new KillCommand());
        _registry.Register(new ServiceCommand());
        _registry.Register(new AfcpCommand());
        _registry.Register(new ExitCommand());
        _registry.Register(new HelpCommand(_registry));
    }
}
