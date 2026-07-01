using HCore.Modules.Base;
using HCore.Packages.TestDemo.Module1;

namespace HCore.Packages.TestDemo.Module2;

public class Module2Implement : BaseImplement, IModule2
{
    /// <summary>
    /// VFS path that selects which IModule1 this module calls. If absent, defaults
    /// to the local <c>/proc/module1</c>. Pointing this at a remote mount (e.g.
    /// <c>/remote/proc/module1</c>) makes the very same <c>GetModuleInterface</c>
    /// call cross the wire via the AFCP Layer 3 MKCall proxy — the caller neither
    /// knows nor cares (9P-style transparency, like <c>RemoteSlam</c>'s subscribe
    /// target). A production deployment would take this from the config system
    /// (TODO.md §C4), which does not exist yet.
    /// </summary>
    public const string TargetFile = "/tmp/module2_target";

    private const string DefaultTarget = "/proc/module1";

    public void Run()
    {
        var target = DefaultTarget;
        try
        {
            if (Vfs.Exists(TargetFile))
            {
                var configured = Vfs.ReadAllText(TargetFile).Trim();
                if (configured.Length > 0) target = configured;
            }
        }
        catch
        {
            // Fall back to the default target on any read error.
        }

        Logger.I($"Module 2 started! (calling {target})");

        // Look up the ALREADY-RUNNING Module1 by its /proc path and call it.
        //   - this is a pure lookup: Module2 holds only the IModule1 interface,
        //     so it could never construct Module1 — it can only find a running one;
        //   - Func1() is the message; whatever it returns would be plain data.
        // When `target` resolves to a remote AFCP mount, GetModuleInterface returns
        // a RemoteModuleProxy<IModule1> and Func1() is marshalled over the wire
        // (Layer 3 — MKCall). Module1 must already be spawned at that path.
        var module1 = Host.GetModuleInterface<IModule1>(target);
        module1.Func1();
    }
}
