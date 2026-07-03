namespace HCore.Modules.Base;

/// <summary>
/// The kernel "forge" door — the capability that lets a (privileged) user-space
/// module build and hot-load new packages at runtime. Resolved as the kernel
/// service <c>@forge</c> via <see cref="IModuleHost.GetModuleInterface{T}"/>.
///
/// It bridges the two worlds the build step straddles: the VFS (where a module
/// authors source) and the real host filesystem (where <c>dotnet</c> compiles),
/// and it registers a freshly compiled pack into the live module table so it can
/// be spawned immediately — no reboot.
/// </summary>
public interface IForge : IModule
{
    /// <summary>
    /// Translate a VFS path to the real host-filesystem path backing it, or
    /// <c>null</c> if that subtree is not host-backed (e.g. <c>/proc</c>,
    /// <c>/dev</c>, <c>/tmp</c>). This is the minimal "FUSE-like" bridge that
    /// lets host tools such as <c>dotnet</c> operate on VFS-authored files.
    /// </summary>
    string? ToHostPath(string vfsPath);

    /// <summary>
    /// Host directory containing the shared contract assemblies
    /// (<c>HCore.Modules.Base.dll</c>, <c>HCore.Modules.Robotics.dll</c>) that a
    /// new package must compile against for cross-ALC type identity to hold.
    /// </summary>
    string ReferenceDir { get; }

    /// <summary>
    /// Load <c>/packs/&lt;packName&gt;</c> at runtime: read its <c>mpd</c>, load the
    /// DLL into a fresh collectible-free ALC, discover every
    /// <see cref="IModuleDescriptor"/>, and register them so they can be spawned
    /// immediately by name. Re-installing a pack whose modules were already
    /// registered replaces the old descriptors (new spawns use the new code).
    /// Returns the module names that are now registered.
    /// </summary>
    IReadOnlyList<string> InstallPack(string packName);
}
