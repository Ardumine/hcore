using HCore.Modules.Base;
using System.Text;

namespace HCore.Packages.HInit.Init;

public class InitImplement : BaseImplement, IInit
{
    /// <summary>
    /// This will be ran as an init.
    /// </summary>
    public void Run()
    {
        //DEV: The terminal emulator was Vibe Coded (I just needed a fast solution to work with the VFS easily).
        Console.WriteLine("HCore shell started.");
        Console.WriteLine("Type 'help' to list commands. Type 'exit' to quit.");

        ReadLine.HistoryEnabled = true;

        Vfs.SetWorkingDirectory("/");

        while (true)
        {
            //Console.Write($"{Vfs.WorkingDirectory} $ ");
            var line = ReadLine.Read($"{Vfs.WorkingDirectory} $ ");
            if (line is null)
            {
                break;
            }

            var args = ParseArguments(line);
            if (args.Count == 0)
            {
                continue;
            }

            var command = args[0].ToLowerInvariant();
            try
            {
                if (command == "exit")
                {
                    Console.WriteLine("Shell exit.");
                    break;
                }

                ExecuteCommand(command, args);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }
    }

    private void ExecuteCommand(string command, IReadOnlyList<string> args)
    {
        switch (command)
        {
            case "help":
                PrintHelp();
                return;
            case "pwd":
                Console.WriteLine(Vfs.WorkingDirectory);
                return;
            case "cd":
                Vfs.SetWorkingDirectory(args.Count > 1 ? args[1] : "/");
                return;
            case "ls":
                ListDirectory(args.Count > 1 ? args[1] : ".");
                return;
            case "cat":
                RequireArgs(args, 2, "usage: cat <file>");
                Console.WriteLine(Vfs.ReadAllText(args[1]));
                return;
            case "mkdir":
                RequireArgs(args, 2, "usage: mkdir <dir>");
                Vfs.CreateDirectory(args[1]);
                return;
            case "rm":
                RequireArgs(args, 2, "usage: rm <file>");
                if (!Vfs.DeleteFile(args[1]))
                {
                    Console.WriteLine("rm: file not found");
                }

                return;
            case "rmdir":
                RequireArgs(args, 2, "usage: rmdir <dir>");
                if (!Vfs.DeleteDirectory(args[1], recursive: false))
                {
                    Console.WriteLine("rmdir: directory not found or not empty");
                }

                return;
            case "touch":
                RequireArgs(args, 2, "usage: touch <file>");
                Vfs.TouchFile(args[1]);
                return;
            case "exists":
                RequireArgs(args, 2, "usage: exists <path>");
                Console.WriteLine(Vfs.Exists(args[1]) ? "true" : "false");
                return;
            case "mv":
                RequireArgs(args, 3, "usage: mv <source> <destination>");
                if (!Vfs.Move(args[1], args[2]))
                {
                    Console.WriteLine("mv: source not found or destination exists");
                }

                return;
            case "rename":
                RequireArgs(args, 3, "usage: rename <path> <new-name>");
                if (!Vfs.Rename(args[1], args[2]))
                {
                    Console.WriteLine("rename: source not found or destination exists");
                }

                return;
            case "write":
                RequireArgs(args, 3, "usage: write <file> <text>");
                Vfs.WriteAllText(args[1], string.Join(' ', args.Skip(2)), append: false);
                return;
            case "append":
                RequireArgs(args, 3, "usage: append <file> <text>");
                Vfs.WriteAllText(args[1], string.Join(' ', args.Skip(2)), append: true);
                return;
            case "run":
                RequireArgs(args, 2, "usage: run <module-name>");
                RunModule(args[1]);
                return;
            case "spawn":
                RequireArgs(args, 3, "usage: spawn <module-name> <instance-name>");
                SpawnModule(args[1], args[2]);
                return;
            case "kill":
                RequireArgs(args, 2, "usage: kill <instance>");
                KillInstance(args[1]);
                return;
            case "clear":
                Console.Clear();
                return;
            default:
                Console.WriteLine($"unknown command: {command}");
                return;
        }
    }

    private void RunModule(string instancePath)
    {
        // Look up an ALREADY-SPAWNED instance by its /proc path and run it. The
        // shell knows only the shared IRunnable contract, not the concrete type.
        var module = Host.GetModuleInterface<IRunnable>(instancePath);
        module.Run();
    }

    private void SpawnModule(string moduleName, string instanceName)
    {
        // Create a NEW named instance (like exec) — but do NOT run it. This works
        // for any module, including services with no entry point. It becomes
        // visible at /proc/<instanceName>; use `run` to run it if it is runnable.
        Host.Spawn<IModule>(moduleName, instanceName);
        Console.WriteLine($"spawned '{instanceName}' from '{moduleName}' (at /proc/{instanceName})");
    }

    private void KillInstance(string instancePath)
    {
        // Privileged cascade kill — reaps the target and every descendant.
        Host.Kill(instancePath);
        Console.WriteLine($"killed '{instancePath}'");
    }

    private void ListDirectory(string path)
    {
        foreach (var entry in Vfs.ListDirectory(path))
        {
            Console.WriteLine(entry);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  help                        Show this help");
        Console.WriteLine("  exit                        Exit shell");
        Console.WriteLine("  pwd                         Print working directory");
        Console.WriteLine("  cd <path>                   Change working directory");
        Console.WriteLine("  ls [path]                   List directory entries");
        Console.WriteLine("  cat <file>                  Print file contents");
        Console.WriteLine("  mkdir <dir>                 Create directory");
        Console.WriteLine("  rm <file>                   Remove file");
        Console.WriteLine("  rmdir <dir>                 Remove empty directory");
        Console.WriteLine("  touch <file>                Create file if missing");
        Console.WriteLine("  exists <path>               Check whether file/dir exists");
        Console.WriteLine("  mv <source> <destination>   Move file or directory");
        Console.WriteLine("  rename <path> <new-name>    Rename file or directory");
        Console.WriteLine("  write <file> <text>         Overwrite file with text");
        Console.WriteLine("  append <file> <text>        Append text to file");
        Console.WriteLine("  spawn <module> <instance>   Create a new instance of a module (does not run it)");
        Console.WriteLine("  run <instance>              Run an already-spawned instance (e.g. /proc/m2)");
        Console.WriteLine("  kill <instance>             Kill an instance and its children");
        Console.WriteLine("  clear                       Clear terminal");
    }

    private static void RequireArgs(IReadOnlyList<string> args, int minimum, string usage)
    {
        if (args.Count < minimum)
        {
            throw new InvalidOperationException(usage);
        }
    }

    private static List<string> ParseArguments(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(ch);
        }

        if (builder.Length > 0)
        {
            result.Add(builder.ToString());
        }

        return result;
    }
}