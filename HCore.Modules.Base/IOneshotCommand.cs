namespace HCore.Modules.Base;

/// <summary>
/// A module that is spawned, given arguments, run to completion, and killed —
/// the pattern for a one-shot shell command (analogous to a Unix process).
/// </summary>
/// <remarks>
/// <see cref="IRunnable.Run"/> is called after <see cref="SetArguments"/>.
/// The shell spawns this as a child, calls SetArguments with the full argv
/// (args[0] = command name), then Run, then KillChild in a finally block.
/// </remarks>
public interface IOneshotCommand : IRunnable
{
    void SetArguments(string[] args);
}
