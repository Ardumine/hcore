namespace HCore.Modules.Base;

/// <summary>
/// Minimal logging surface injected into every module instance, exactly like
/// <see cref="IModuleFileSystem"/> and <see cref="IModuleHost"/> — this is the
/// module's only path for structured log output. The kernel wires a logger with
/// description = instance name so all tags are self-identifying.
/// </summary>
public interface IModuleLogger
{
    string Description { get; }

    /// <summary>Log an info-level message.</summary>
    void I(string message);

    /// <summary>Log a warning-level message.</summary>
    void W(string message);

    /// <summary>Log an error-level message.</summary>
    void E(string message);
}
