using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace HCore.Modules.Base;

/// <summary>
/// The kernel's composite VFS surface, exposed as a <c>@vfs</c> kernel-service
/// singleton. Implements <see cref="IModule"/> so it can be resolved through
/// <see cref="IModuleHost.GetModuleInterface{T}"/> (a technical constraint —
/// this is a kernel service, not a module instance).
///
/// <para>This is the AGGREGATE operations surface over the unified mount tree
/// (path-based), distinct from <see cref="IVirtualFileSystem"/> which is the
/// per-mount contract (node-based).</para>
///
/// <para>Currently unrestricted: any module that knows the <c>@vfs</c> name can
/// access the full mount table. A future capability model (TODO.md C3) would
/// gate mount/unmount separately from read-only VFS operations.</para>
/// </summary>
public interface IVfsKernel : IModule
{
    // Mount management
    void Mount(string mountPoint, IVirtualFileSystem fs);
    bool Unmount(string mountPoint);

    /// <summary>
    /// Resolve <paramref name="path"/> to the backing <see cref="IVirtualFileSystem"/>
    /// and the path as that filesystem sees it (mount prefix stripped).
    /// Returns <c>false</c> instead of throwing when no mount covers the path.
    /// </summary>
    bool TryResolveMount(string path, [MaybeNullWhen(false)] out IVirtualFileSystem fileSystem, out string remotePath);

    // VFS operations (used by the serve-side AFCP proxy)
    IEnumerable<string> ListDirectory(string path);
    IVirtualFile GetFile(string path);
    IVirtualFile CreateFile(string path, byte[]? contents = null, bool overwrite = true);
    void MkDir(string path);
    bool DeleteFile(string path);
    bool Exists(string path);
}
