using HCore.Modules.Base;

namespace HCore.Packages.HMount.Mount;

/// <summary>
/// The fstab mounter. On <see cref="IRunnable.Run"/> it reads <c>/etc/fstab</c>
/// and mounts every listed filesystem through the privileged <c>@vfs</c> kernel
/// service. Runs once at boot as a service, then stays resident in <c>/proc</c>.
/// </summary>
public interface IMount : IRunnable
{
    /// <summary>
    /// Parse and mount every entry in <paramref name="fstabPath"/> (defaults to
    /// <c>/etc/fstab</c>). Returns the number of filesystems successfully mounted.
    /// </summary>
    int MountAll(string fstabPath = "/etc/fstab");
}
