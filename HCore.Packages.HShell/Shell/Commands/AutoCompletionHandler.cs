using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

internal sealed class AutoCompletionHandler : IAutoCompleteHandler
{
    private readonly CommandRegistry _registry;
    private readonly IModuleFileSystem _vfs;

    public char[] Separators { get; set; } = { ' ' };

    private static readonly HashSet<string> PathCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "cat", "mkdir", "rm", "rmdir", "touch", "exists",
        "mv", "rename", "write", "append"
    };

    private static readonly Dictionary<string, string[]> SubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["service"] = new[] { "start", "stop", "restart", "status", "list" },
        ["afcp"] = new[] { "serve", "stop", "mount", "unmount", "status", "test" },
    };

    public AutoCompletionHandler(CommandRegistry registry, IModuleFileSystem vfs)
    {
        _registry = registry;
        _vfs = vfs;
    }

    public string[]? GetSuggestions(string text, int index)
    {
        var currentFragment = index < text.Length ? text[index..] : "";

        var tokens = CommandRegistry.ParseArguments(text);
        if (tokens.Count == 0)
        {
            return _registry.Commands.Select(c => c.Name).OrderBy(n => n).ToArray();
        }

        var currentTokenIndex = ComputeCurrentTokenIndex(text, tokens, currentFragment);

        if (currentTokenIndex == 0)
        {
            return _registry.Commands
                .Select(c => c.Name)
                .Where(n => n.StartsWith(currentFragment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .ToArray();
        }

        var commandName = tokens[0];

        if (currentTokenIndex == 1 && SubCommands.TryGetValue(commandName, out var subCmds))
        {
            return subCmds
                .Where(s => s.StartsWith(currentFragment, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (PathCommands.Contains(commandName))
        {
            return CompletePath(currentFragment);
        }

        return null;
    }

    private static int ComputeCurrentTokenIndex(string text, List<string> tokens, string currentFragment)
    {
        if (text.EndsWith(' ') && currentFragment.Length == 0)
        {
            return tokens.Count;
        }

        return tokens.Count - 1;
    }

    private string[]? CompletePath(string fragment)
    {
        try
        {
            var lastSlash = fragment.LastIndexOf('/');
            string dir;
            string prefix;
            string dirPrefix;

            if (lastSlash >= 0)
            {
                dir = lastSlash == 0 ? "/" : fragment[..lastSlash];
                prefix = fragment[(lastSlash + 1)..];
                dirPrefix = fragment[..(lastSlash + 1)];
            }
            else
            {
                dir = ".";
                prefix = fragment;
                dirPrefix = "";
            }

            return _vfs.ListDirectory(dir)
                .Where(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => dirPrefix + e)
                .OrderBy(e => e)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
