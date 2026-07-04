using HCore.Modules.Base;

namespace HCore.Packages.HMount.Mount;

/// <summary>
/// Reads <c>/etc/fstab</c> and mounts each entry via the <c>@vfs</c> kernel
/// service. The fstab format mirrors the classic Unix one — one entry per line,
/// whitespace-separated:
/// <code>
/// # &lt;source&gt;      &lt;mountpoint&gt;  &lt;type&gt;   &lt;options&gt;
/// /srv/data       /data         hostfs   rw
/// -               /scratch      memfs    rw
/// -               /dev2         devfs    ro
/// peer.local:9000 /other        nexus    rw
/// </code>
/// <para>Blank lines and lines starting with <c>#</c> are ignored. A source of
/// <c>-</c> or <c>none</c> means "no backing path" (memfs/devfs). Options is a
/// comma-separated list; recognised flags: <c>ro</c>/<c>rw</c> and
/// <c>replace</c> (replace an existing mount at the same point).</para>
///
/// <para>Local types (hostfs/memfs/devfs) are created through the <c>@vfs</c>
/// kernel service (<see cref="IVfsKernel.MountByType"/>). The <c>nexus</c> type is
/// a REMOTE mount — it carries no local filesystem and is driven through the
/// Nexus connector (<c>@afcp</c>, <see cref="IAfcpKernel.Mount"/>), which opens an
/// AFCP session to <c>host:port</c>.</para>
/// </summary>
public class MountImplement : BaseImplement, IMount
{
    private const string DefaultFstab = "/etc/fstab";

    public void Run() => MountAll();

    public int MountAll(string fstabPath = DefaultFstab)
    {
        if (!Vfs.FileExists(fstabPath))
        {
            Logger.W($"no fstab at {fstabPath} — nothing to mount");
            return 0;
        }

        var vfs = Host.GetModuleInterface<IVfsKernel>("@vfs");

        var mounted = 0;
        var lineNo = 0;
        foreach (var rawLine in Vfs.ReadAllText(fstabPath).Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                Logger.W($"fstab:{lineNo}: expected '<source> <mountpoint> <type> [options]', got '{line}'");
                continue;
            }

            var source = fields[0];
            var mountPoint = fields[1];
            var fsType = fields[2];
            var options = fields.Length > 3 ? fields[3] : string.Empty;

            var (readOnly, replace) = ParseOptions(options);
            var backingSource = IsNoSource(source) ? null : source;

            try
            {
                if (IsNexusType(fsType))
                {
                    // Remote mount: not a local IVirtualFileSystem — driven through
                    // the Nexus connector (@afcp), which opens an AFCP session to the
                    // peer. Source is 'host:port'.
                    var (peerHost, peerPort) = ParseHostPort(source);
                    var afcp = Host.GetModuleInterface<IAfcpKernel>("@afcp");
                    var status = afcp.Mount(peerHost, peerPort, mountPoint);
                    Logger.I($"mounted nexus '{peerHost}:{peerPort}' at {mountPoint}: {status}");
                }
                else
                {
                    vfs.MountByType(mountPoint, fsType, backingSource, readOnly, replace);
                    Logger.I($"mounted {fsType} '{backingSource ?? "-"}' at {mountPoint}{(readOnly ? " (ro)" : string.Empty)}");
                }

                mounted++;
            }
            catch (Exception ex)
            {
                Logger.E($"fstab:{lineNo}: mount {mountPoint} failed: {ex.Message}");
            }
        }

        Logger.I($"fstab: mounted {mounted} filesystem(s)");
        return mounted;
    }

    protected override string? DescribeForProc() => $"fstab:       {DefaultFstab}";

    private static bool IsNoSource(string source)
        => source is "-" || string.Equals(source, "none", StringComparison.OrdinalIgnoreCase);

    private static bool IsNexusType(string fsType)
        => string.Equals(fsType, "nexus", StringComparison.OrdinalIgnoreCase);

    private static (string Host, int Port) ParseHostPort(string source)
    {
        var sep = source.LastIndexOf(':');
        if (sep <= 0 || sep == source.Length - 1)
        {
            throw new FormatException($"nexus source must be 'host:port', got '{source}'.");
        }

        var host = source[..sep];
        if (!int.TryParse(source[(sep + 1)..], out var port))
        {
            throw new FormatException($"invalid port in nexus source '{source}'.");
        }

        return (host, port);
    }

    private static (bool ReadOnly, bool Replace) ParseOptions(string options)
    {
        var readOnly = false;
        var replace = false;

        foreach (var opt in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (opt.ToLowerInvariant())
            {
                case "ro":
                    readOnly = true;
                    break;
                case "rw":
                    readOnly = false;
                    break;
                case "replace":
                    replace = true;
                    break;
            }
        }

        return (readOnly, replace);
    }
}
