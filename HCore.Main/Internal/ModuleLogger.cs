using HCore.Modules.Base;
using Logyt;

namespace HCore.Main.Internal;

/// <summary>
/// Kernel-side <see cref="IModuleLogger"/> adapter. Each module instance gets
/// one with description = instance name (e.g. "init", "init/console"), so log
/// tags are self-identifying. Because <see cref="Logyt.Logyt"/> now uses a
/// shared static <see cref="System.Diagnostics.Stopwatch"/>, every logger —
/// including the kernel's own "HCore" — shares one monotonic time origin.
/// </summary>
internal sealed class ModuleLogger : IModuleLogger
{
    private readonly ConsoleLogyt _logyt;

    public ModuleLogger(string description)
    {
        _logyt = new ConsoleLogyt(description);
        Description = description;
    }

    public string Description { get; }

    public void I(string message) => _logyt.I(message);

    public void W(string message) => _logyt.W(message);

    public void E(string message) => _logyt.Log(MessageType.Error, message);
}
