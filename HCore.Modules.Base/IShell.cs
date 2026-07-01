namespace HCore.Modules.Base;

/// <summary>
/// A module that presents an interactive command line (a shell) over the VFS.
/// Implemented by the shell package and driven either interactively through
/// <see cref="IRunnable.Run"/> (the REPL) or in batch through
/// <see cref="RunScript"/> (executing a script file line by line).
/// </summary>
public interface IShell : IRunnable
{
    /// <summary>
    /// Read <paramref name="path"/> from the VFS and execute each non-empty,
    /// non-comment (<c>#</c>) line through the same dispatch path as the REPL.
    /// Stops on the first failing line. Returns <c>true</c> if every line
    /// executed without throwing.
    /// </summary>
    bool RunScript(string path);
}
