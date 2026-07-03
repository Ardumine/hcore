namespace HCore.Modules.Base;

/// <summary>
/// The kernel-space AFCP bridge contract. Lives in the shared contract assembly
/// so the shell package (which cannot reference AFCP or the kernel) can drive the
/// bridge purely through this interface, the same way <see cref="IServiceManager"/>
/// lets the shell reach init.
///
/// The implementation (<c>AfcpKernelService</c>) is kernel-space: it lives in
/// HCore.Main, references AFCP directly, and is registered as a named kernel
/// service under <c>@afcp</c>. The shell reaches it via
/// <c>Host.GetModuleInterface&lt;IAfcpKernel&gt;("@afcp")</c>.
///
/// All methods return a human-readable status string (suitable for direct shell
/// echo) and throw on hard failure — matching the <see cref="IServiceManager"/>
/// convention.
/// </summary>
public interface IAfcpKernel : IModule
{
    /// <summary>Start serving the local /proc tree over AFCP on <paramref name="port"/>.</summary>
    string Serve(int port);

    /// <summary>Stop the AFCP server, dropping all peer sessions.</summary>
    string StopServe();

    /// <summary>Mount the remote peer's tree at <paramref name="mountPoint"/> (e.g. /other).</summary>
    string Mount(string host, int port, string mountPoint);

    /// <summary>Unmount a previously mounted remote tree.</summary>
    string Unmount(string mountPoint);

    /// <summary>Report serving port and active mounts.</summary>
    string Status();

    /// <summary>
    /// Run a full loopback self-test in this one instance: spawn the lidar demo,
    /// serve on a loopback port, mount the remote tree at <c>/selftest</c>, then
    /// <c>ls</c> + <c>cat</c> through the VFS to verify serve + Sync + Read +
    /// RemoteFileSystem end-to-end. Cleans up (unmount, stop, kill lidar) and
    /// returns the captured output.
    /// </summary>
    string SelfTest();
}
