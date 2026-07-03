using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace HCore.Modules.Base;

/// <summary>
/// The VFS driver door — the mount-table + raw-node surface injected into a
/// filesystem driver module (e.g. the AFCP Nexus connector). Unlike
/// <see cref="IVfsKernel"/> (the <c>@vfs</c> kernel-service, which extends
/// <see cref="IModule"/>), this is a pure capability — it is injected directly,
/// not resolved by name.
///
/// <para>This mirrors the existing <see cref="IModuleFileSystem"/> /
/// <see cref="IModuleHost"/> pattern: user-space modules get the unprivileged
/// door (<see cref="IModuleFileSystem"/>), driver modules get the privileged
/// door (<see cref="IKernelVfs"/>).</para>
/// </summary>
public interface IKernelVfs
{
    // ── Mount table ──
    void Mount(string mountPoint, IVirtualFileSystem fs, bool replaceExisting = false);
    bool Unmount(string mountPoint);

    /// <summary>
    /// Resolve <paramref name="path"/> to the backing <see cref="IVirtualFileSystem"/>
    /// and the path as that filesystem sees it (mount prefix stripped).
    /// Returns <c>false</c> instead of throwing when no mount covers the path.
    /// </summary>
    bool TryResolveMount(string path, [MaybeNullWhen(false)] out IVirtualFileSystem fileSystem, out string remotePath);

    // ── Serve-side primitives (generic VFS proxy) ──

    /// <summary>
    /// List a directory. Entries with a trailing <c>/</c> are directories;
    /// bare names are files. Reflects child mounts.
    /// </summary>
    IEnumerable<string> ListDirectory(string path);

    /// <summary>Open a file for read.</summary>
    IVirtualFile GetFile(string path);

    /// <summary>Create or overwrite a file.</summary>
    void CreateFile(string path, byte[]? contents, bool overwrite = true);

    /// <summary>Create a directory (and any missing parents).</summary>
    void MkDir(string path);

    /// <summary>Delete a file or empty directory.</summary>
    bool DeleteFile(string path);
}
