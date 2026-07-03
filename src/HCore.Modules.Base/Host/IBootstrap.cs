namespace HCore.Modules.Base;

/// <summary>
/// Bootstraps the runtime filesystem on first boot when <c>/packs/</c> is
/// missing essential packages. Runs before the normal <c>init</c> spawn.
/// </summary>
public interface IBootstrap
{
    /// <summary>
    /// Run bootstrap. Returns <c>true</c> if packages were fetched and
    /// installed; <c>false</c> if essential packages were already present
    /// (no-op). Throws on unrecoverable failure (network down, corrupt
    /// archive, etc.) — the kernel exits with a diagnostic message.
    /// </summary>
    bool Run(IModuleFileSystem vfs, IModuleLogger logger);
}
