namespace HCore.Modules.Base;

/// <summary>
/// Lifecycle state of a named service, as tracked by the init module.
/// </summary>
public enum ServiceStatus
{
    Stopped,
    Running,
    Failed,
}

/// <summary>
/// A snapshot of one service entry under <c>/etc/services</c>.
/// </summary>
public sealed record ServiceInfo(string Name, ServiceStatus Status);

/// <summary>
/// The service-manager surface the init module exposes to other modules
/// (notably the shell's <c>service</c> command). Lives in the shared contract
/// assembly so a shell in a different package can reach init across the
/// assembly-load-context boundary.
/// </summary>
public interface IServiceManager : IModule
{
    /// <summary>
    /// Run the start script <c>/etc/services/&lt;name&gt;.svc</c>. By convention
    /// the script must spawn and run a module instance named exactly
    /// <paramref name="name"/> (the service's primary instance); the manager
    /// reports <see cref="ServiceStatus.Running"/> only if that instance exists
    /// in <c>/proc</c> after the script finishes.
    /// </summary>
    ServiceStatus StartService(string name);

    /// <summary>
    /// Kill the service's primary instance (and, by cascade, every descendant
    /// it owns). No-op (returns <see cref="ServiceStatus.Stopped"/>) if it is
    /// not running.
    /// </summary>
    ServiceStatus StopService(string name);

    /// <summary>
    /// Stop then start.
    /// </summary>
    ServiceStatus RestartService(string name);

    /// <summary>
    /// Current status of <paramref name="name"/>. A service with no script on
    /// disk is <see cref="ServiceStatus.Failed"/>.
    /// </summary>
    ServiceStatus GetStatus(string name);

    /// <summary>
    /// One entry per <c>*.svc</c> file under <c>/etc/services</c>, with its
    /// current status.
    /// </summary>
    IEnumerable<ServiceInfo> ListServices();
}
