using System.Text;
using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

/// <summary>
/// Name -> <see cref="ICommand"/> lookup. Case-insensitive on command name.
/// Also owns argument tokenization (quote-aware whitespace splitting), shared
/// by the REPL and the script runner.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ICommand> Commands => _commands.Values;

    public void Register(ICommand command) => _commands[command.Name] = command;

    public bool TryGet(string name, out ICommand? command) => _commands.TryGetValue(name, out command);

    /// <summary>
    /// Tokenize <paramref name="input"/> into argv: whitespace-separated,
    /// double-quoted segments kept as a single argument (quotes removed).
    /// Empty/whitespace input yields an empty list.
    /// </summary>
    public static List<string> ParseArguments(string input)
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
