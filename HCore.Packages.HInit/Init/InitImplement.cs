using HCore.Modules.Base;

namespace HCore.Packages.HInit.Init;

/// <summary>
/// PID 1. On <see cref="Run"/> it boots every <c>/etc/services/*.svc</c> script
/// through a worker shell, then spawns the interactive console shell and blocks
/// on it until it exits (which ends the kernel). Also implements
/// <see cref="IServiceManager"/> so the shell's <c>service</c> command can
/// start/stop/restart/status/list services at runtime.
/// </summary>
public class InitImplement : ContainerImplement, IInit, IServiceManager
{
    private const string ServicesDir = "/etc/services";

    private IShell? _workerShell;

    public void Run()
    {
        Logger.I("HCore init starting.");

        Vfs.SetWorkingDirectory("/");

        // Worker shell used purely for RunScript — never enters its REPL.
        _workerShell = SpawnChildByName<IShell>("HCore.Packages.HShell.Shell", "svc", null);

        BootServices();

        // Interactive console shell — blocks until the user exits.
        var console = SpawnChildByName<IShell>("HCore.Packages.HShell.Shell", "console", null);
        console.Run();
    }

    protected override string? DescribeForProc()
    {
        var count = 0;
        try { count = CountServiceScripts(); } catch { /* /etc/services missing */ }
        return $"services:    {count} script(s) under {ServicesDir}";
    }

    // ── boot ────────────────────────────────────────────────────────────────

    private void BootServices()
    {
        foreach (var name in EnumerateServiceNames())
        {
            var status = StartService(name);
            Logger.I($"service {name}: {status}");
        }
    }

    // ── IServiceManager ─────────────────────────────────────────────────────

    public ServiceStatus StartService(string name)
    {
        var script = ScriptPath(name);
        if (!Vfs.FileExists(script))
        {
            return ServiceStatus.Failed;
        }

        if (IsRunning(name))
        {
            return ServiceStatus.Running;
        }

        var worker = GetWorkerShell();
        var ok = worker.RunScript(script);
        return ok && IsRunning(name) ? ServiceStatus.Running : ServiceStatus.Failed;
    }

    public ServiceStatus StopService(string name)
    {
        if (!IsRunning(name))
        {
            return ServiceStatus.Stopped;
        }

        try
        {
            Host.Kill(name);
        }
        catch (Exception ex)
        {
            Logger.E($"service: stop '{name}' failed: {ex.Message}");
            return ServiceStatus.Failed;
        }

        return ServiceStatus.Stopped;
    }

    public ServiceStatus RestartService(string name)
    {
        StopService(name);
        return StartService(name);
    }

    public ServiceStatus GetStatus(string name)
    {
        if (!Vfs.FileExists(ScriptPath(name)))
        {
            return ServiceStatus.Failed;
        }

        return IsRunning(name) ? ServiceStatus.Running : ServiceStatus.Stopped;
    }

    public IEnumerable<ServiceInfo> ListServices()
    {
        foreach (var name in EnumerateServiceNames())
        {
            yield return new ServiceInfo(name, IsRunning(name) ? ServiceStatus.Running : ServiceStatus.Stopped);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private bool IsRunning(string name) => Vfs.Exists("/proc/" + name);

    private static string ScriptPath(string name) => $"{ServicesDir}/{name}.svc";

    private IShell GetWorkerShell()
    {
        if (_workerShell is null)
        {
            _workerShell = SpawnChildByName<IShell>("HCore.Packages.HShell.Shell", "svc", null);
        }
        return _workerShell;
    }

    private IEnumerable<string> EnumerateServiceNames()
    {
        string[] entries;
        try
        {
            entries = Vfs.ListDirectory(ServicesDir).ToArray();
        }
        catch (Exception ex)
        {
            Logger.W($"service: cannot read {ServicesDir}: {ex.Message}");
            yield break;
        }

        foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.EndsWith(".svc"))
            {
                yield return entry[..^4];
            }
        }
    }

    private int CountServiceScripts() => EnumerateServiceNames().Count();
}
