using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

internal sealed class PwdCommand : ICommand
{
    public string Name => "pwd";
    public string Description => "Print working directory";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx) => ctx.Out.WriteLine(ctx.Vfs.WorkingDirectory);
}

internal sealed class CdCommand : ICommand
{
    public string Name => "cd";
    public string Description => "cd <path>   Change working directory (defaults to /)";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx) => ctx.Vfs.SetWorkingDirectory(args.Count > 1 ? args[1] : "/");
}

internal sealed class LsCommand : ICommand
{
    public string Name => "ls";
    public string Description => "ls [path]   List directory entries (defaults to .)";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        foreach (var entry in ctx.Vfs.ListDirectory(args.Count > 1 ? args[1] : "."))
        {
            ctx.Out.WriteLine(entry);
        }
    }
}

internal sealed class CatCommand : ICommand
{
    public string Name => "cat";
    public string Description => "cat <file>   Print file contents";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: cat <file>");
        ctx.Out.WriteLine(ctx.Vfs.ReadAllText(args[1]));
    }
}

internal sealed class MkdirCommand : ICommand
{
    public string Name => "mkdir";
    public string Description => "mkdir <dir>   Create directory";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: mkdir <dir>");
        ctx.Vfs.CreateDirectory(args[1]);
    }
}

internal sealed class RmCommand : ICommand
{
    public string Name => "rm";
    public string Description => "rm <file>   Remove file";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: rm <file>");
        if (!ctx.Vfs.DeleteFile(args[1]))
        {
            ctx.Out.WriteLine("rm: file not found");
        }
    }
}

internal sealed class RmDirCommand : ICommand
{
    public string Name => "rmdir";
    public string Description => "rmdir <dir>   Remove empty directory";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: rmdir <dir>");
        if (!ctx.Vfs.DeleteDirectory(args[1], recursive: false))
        {
            ctx.Out.WriteLine("rmdir: directory not found or not empty");
        }
    }
}

internal sealed class TouchCommand : ICommand
{
    public string Name => "touch";
    public string Description => "touch <file>   Create file if missing";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: touch <file>");
        ctx.Vfs.TouchFile(args[1]);
    }
}

internal sealed class ExistsCommand : ICommand
{
    public string Name => "exists";
    public string Description => "exists <path>   Print true/false";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 2, "usage: exists <path>");
        ctx.Out.WriteLine(ctx.Vfs.Exists(args[1]) ? "true" : "false");
    }
}

internal sealed class MvCommand : ICommand
{
    public string Name => "mv";
    public string Description => "mv <source> <destination>   Move file or directory";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 3, "usage: mv <source> <destination>");
        if (!ctx.Vfs.Move(args[1], args[2]))
        {
            ctx.Out.WriteLine("mv: source not found or destination exists");
        }
    }
}

internal sealed class RenameCommand : ICommand
{
    public string Name => "rename";
    public string Description => "rename <path> <new-name>   Rename file or directory";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 3, "usage: rename <path> <new-name>");
        if (!ctx.Vfs.Rename(args[1], args[2]))
        {
            ctx.Out.WriteLine("rename: source not found or destination exists");
        }
    }
}

internal sealed class WriteCommand : ICommand
{
    public string Name => "write";
    public string Description => "write <file> <text>   Overwrite file with text";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 3, "usage: write <file> <text>");
        ctx.Vfs.WriteAllText(args[1], string.Join(' ', args.Skip(2)), append: false);
    }
}

internal sealed class AppendCommand : ICommand
{
    public string Name => "append";
    public string Description => "append <file> <text>   Append text to file";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        ShellContext.RequireArgs(args, 3, "usage: append <file> <text>");
        ctx.Vfs.WriteAllText(args[1], string.Join(' ', args.Skip(2)), append: true);
    }
}

internal sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "Clear the terminal";
    public void Execute(IReadOnlyList<string> args, ShellContext ctx) => Console.Clear();
}
