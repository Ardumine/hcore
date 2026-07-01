using AFCP;
using AFCP.Protocol;
using HCore.Main.Vfs;
using HCore.Modules.Base;
using System.Net;
using System.Text;

namespace HCore.Main.Internal;

/// <summary>
/// The kernel-space AFCP bridge. Implements <see cref="IAfcpKernel"/> (the
/// contract in HCore.Modules.Base) so the shell package can drive it without
/// referencing AFCP. Holds the <see cref="AfcpServer"/> (serve side) and the
/// active <see cref="RemoteFileSystem"/> mounts (mount side), both wired to the
/// kernel's <see cref="FileSystem"/>.
///
/// The serve side is a generic VFS proxy: <c>Sync</c> lists any directory via
/// <see cref="FileSystem.ListDirectory"/>, <c>Read</c> returns any file's bytes
/// via <see cref="FileSystem.GetFile"/>, and <c>Write</c>/<c>MkDir</c>/<c>Remove</c>
/// delegate to <see cref="FileSystem.CreateFile"/>/<see cref="FileSystem.MkDir"/>/
/// <see cref="FileSystem.DeleteFile"/>. Because the kernel <see cref="FileSystem"/>
/// already mounts the live <c>/proc</c> (via <see cref="ProcFileSystem"/>) alongside
/// <c>/etc</c>, <c>/dev</c>, <c>/packs</c>, etc., serving <c>/</c> exposes the
/// entire tree read-write — and <c>/proc</c> facets stay live because <c>ProcFileSystem</c>
/// rebuilds them on every server-side read. No capability model exists yet (see
/// TODO.md §C3): any mounting peer can write anywhere under the served root, same
/// documented trusted-LAN gap as <c>Kill</c>. Subscribe-push (Layer 2) and MKCall
/// (Layer 3) are deferred — the provider rejects Subscribe for now.
/// </summary>
internal sealed class AfcpKernelService : IAfcpKernel
{
    private readonly FileSystem _vfs;
    private readonly ModuleHost _moduleHost;
    private readonly Serializer _serializer = new();

    private AfcpServer? _server;
    private int _servingPort;
    private readonly Dictionary<string, RemoteFileSystem> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AfcpKernelService(FileSystem vfs, ModuleHost moduleHost)
    {
        _vfs = vfs;
        _moduleHost = moduleHost;
    }

    public string Serve(int port)
    {
        lock (_lock)
        {
            if (_server is not null)
            {
                return $"already serving on port {_servingPort}; stop first.";
            }

            var endpoint = new IPEndPoint(IPAddress.Any, port);
            var provider = new VfsAfcpProvider(_vfs);
            _server = new AfcpServer(endpoint, provider, _serializer);
            _server.Start();
            _servingPort = port;
            return $"serving on port {port}.";
        }
    }

    public string StopServe()
    {
        lock (_lock)
        {
            if (_server is null)
            {
                return "not serving.";
            }

            _server.Dispose();
            _server = null;
            var port = _servingPort;
            _servingPort = 0;
            return $"stopped serving on port {port}.";
        }
    }

    public string Mount(string host, int port, string mountPoint)
    {
        lock (_lock)
        {
            if (_mounts.ContainsKey(mountPoint))
            {
                return $"mount point '{mountPoint}' is already in use; unmount first.";
            }

            var client = new AfcpClient(_serializer);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            if (!IPAddress.TryParse(host, out var address))
            {
                var resolved = Dns.GetHostAddresses(host);
                address = resolved.Length > 0 ? resolved[0] : IPAddress.Loopback;
                endpoint = new IPEndPoint(address, port);
            }

            client.ConnectAsync(endpoint, "hcore").GetAwaiter().GetResult();
            var remote = new RemoteFileSystem(client, mountPoint);
            _vfs.Mount(mountPoint, remote);
            _mounts[mountPoint] = remote;
            return $"mounted {host}:{port} at {mountPoint}.";
        }
    }

    public string Unmount(string mountPoint)
    {
        lock (_lock)
        {
            if (!_mounts.Remove(mountPoint, out var remote))
            {
                return $"no AFCP mount at '{mountPoint}'.";
            }

            _vfs.Unmount(mountPoint);
            remote.Dispose();
            return $"unmounted {mountPoint}.";
        }
    }

    public string Status()
    {
        lock (_lock)
        {
            var lines = new List<string>();
            if (_server is not null)
            {
                lines.Add($"serving: port {_servingPort}");
            }
            else
            {
                lines.Add("serving: (stopped)");
            }

            lines.Add($"mounts: {_mounts.Count}");
            foreach (var kv in _mounts)
            {
                lines.Add($"  {kv.Key} -> {kv.Value.RemoteEndpoint}");
            }

            return string.Join('\n', lines);
        }
    }

    public string SelfTest()
    {
        var sb = new StringBuilder();
        var port = 8765;
        var spawnedLidar = false;

        void Log(string s) { sb.AppendLine(s); Console.WriteLine($"[afcp-test] {s}"); }

        try
        {
            // 1. Ensure the lidar demo producer is running (spawn+run if absent).
            try
            {
                var lidar = _moduleHost.GetModuleInterface<IRunnable>("lidar");
                Log("lidar already running.");
            }
            catch
            {
                Log("spawning lidar...");
                var lidar = _moduleHost.Spawn<IRunnable>("HCore.Packages.Sensor.Lidar", "lidar");
                lidar.Run();
                spawnedLidar = true;
                Thread.Sleep(300); // let it publish a frame
                Log("lidar running.");
            }

            // 2. Serve.
            Log($"serve on {port}...");
            Log(Serve(port));

            // 3. Mount the local server back into our own VFS.
            Log("mount 127.0.0.1 -> /selftest...");
            Log(Mount("127.0.0.1", port, "/selftest"));

            // 4. ls /selftest  — the whole root: proc, etc, dev, packs, tmp ...
            Log("--- ls /selftest ---");
            foreach (var entry in _vfs.ListDirectory("/selftest"))
            {
                Log(entry);
            }

            // 5. ls /selftest/etc  — a real host-FS directory
            Log("--- ls /selftest/etc ---");
            foreach (var entry in _vfs.ListDirectory("/selftest/etc"))
            {
                Log(entry);
            }

            // 6. ls /selftest/proc/lidar  — the live /proc facet view
            Log("--- ls /selftest/proc/lidar ---");
            foreach (var entry in _vfs.ListDirectory("/selftest/proc/lidar"))
            {
                Log(entry);
            }

            // 7. cat /selftest/proc/lidar/scan_data  (twice — should show fresh frames)
            Log("--- cat /selftest/proc/lidar/scan_data ---");
            Log(_vfs.GetFile("/selftest/proc/lidar/scan_data").ReadString().TrimEnd());
            Thread.Sleep(250);
            Log("--- cat again (fresh frame) ---");
            Log(_vfs.GetFile("/selftest/proc/lidar/scan_data").ReadString().TrimEnd());

            // 8. C7a — remote writes: mkdir, write, cat back, delete, confirm gone.
            // Scratch space under /tmp (MemoryFileSystem) so the self-test never
            // touches the real host FS.
            Log("--- mkdir /selftest/tmp/afcp_test ---");
            _vfs.MkDir("/selftest/tmp/afcp_test");
            Log("ok.");

            Log("--- write /selftest/tmp/afcp_test/hello.txt ---");
            _vfs.CreateFile("/selftest/tmp/afcp_test/hello.txt", Encoding.UTF8.GetBytes("hello from afcp"));
            var written = _vfs.GetFile("/selftest/tmp/afcp_test/hello.txt").ReadString();
            Log($"read back: '{written}'");
            if (written != "hello from afcp")
            {
                throw new InvalidOperationException($"write round-trip mismatch: got '{written}'.");
            }

            Log("--- rm /selftest/tmp/afcp_test/hello.txt ---");
            if (!_vfs.DeleteFile("/selftest/tmp/afcp_test/hello.txt"))
            {
                throw new InvalidOperationException("delete of the scratch file failed.");
            }

            if (_vfs.Exists("/selftest/tmp/afcp_test/hello.txt"))
            {
                throw new InvalidOperationException("scratch file still exists after delete.");
            }

            Log("--- rmdir /selftest/tmp/afcp_test ---");
            if (!_vfs.DeleteFile("/selftest/tmp/afcp_test"))
            {
                throw new InvalidOperationException("delete of the scratch directory failed.");
            }

            Log("--- SELFTEST PASSED ---");
        }
        catch (Exception ex)
        {
            Log($"SELFTEST FAILED: {ex}");
            throw;
        }
        finally
        {
            // Cleanup regardless of success/failure.
            try { Log(Unmount("/selftest")); } catch { }
            try { Log(StopServe()); } catch { }
            if (spawnedLidar)
            {
                try { _moduleHost.Kill("lidar"); Log("killed lidar."); } catch { }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// The <see cref="IAfcpProvider"/> backed by the kernel's <see cref="FileSystem"/>.
/// A generic VFS proxy: <see cref="Sync"/> lists any directory (via
/// <see cref="FileSystem.ListDirectory"/>, which includes child mounts like
/// <c>/proc</c>), <see cref="Read"/> returns any file's bytes (via
/// <see cref="FileSystem.GetFile"/>), and <see cref="Write"/>/<see cref="MkDir"/>/
/// <see cref="Remove"/> mutate the kernel <see cref="FileSystem"/> directly.
/// Because <c>/proc</c> is a live <see cref="ProcFileSystem"/> mount inside the
/// kernel VFS, facet files are rebuilt fresh on every <see cref="Read"/> — the
/// liveness is handled server-side for free, with no facet-specific protocol.
/// </summary>
internal sealed class VfsAfcpProvider : IAfcpProvider
{
    private readonly FileSystem _vfs;

    public VfsAfcpProvider(FileSystem vfs)
    {
        _vfs = vfs;
    }

    public ConnectResponse Connect(ConnectRequest request)
    {
        return new ConnectResponse
        {
            ProtocolVersion = ProtocolVersion.Current,
            PeerName = "hcore",
            Accepted = true
        };
    }

    public SyncResponse Sync(SyncRequest request)
    {
        var path = string.IsNullOrEmpty(request.Path) ? "/" : request.Path;
        var entries = new List<DirEntry>();

        try
        {
            // ListDirectory returns formatted names ("proc/", "demo.svc"); the
            // trailing slash marks a directory. It also folds in direct child
            // mount names (e.g. /proc, /dev, /tmp under /), so the remote tree
            // mirrors the full local mount layout.
            foreach (var entry in _vfs.ListDirectory(path))
            {
                var isDir = entry.EndsWith('/');
                var name = isDir ? entry[..^1] : entry;
                entries.Add(new DirEntry { Name = name, IsDirectory = isDir });
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Return an empty listing for a missing dir — the client sees an empty dir.
        }

        return new SyncResponse { Entries = entries.ToArray() };
    }

    public ReadResponse Read(ReadRequest request)
    {
        try
        {
            var bytes = _vfs.GetFile(request.Path).ReadAllBytes();
            return new ReadResponse { Data = bytes, Exists = true };
        }
        catch (Exception)
        {
            return new ReadResponse { Data = null, Exists = false };
        }
    }

    public WriteResponse Write(WriteRequest request)
    {
        try
        {
            _vfs.CreateFile(request.Path, request.Data ?? Array.Empty<byte>(), request.Overwrite);
            return new WriteResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new WriteResponse { Success = false, Error = ex.Message };
        }
    }

    public MkDirResponse MkDir(MkDirRequest request)
    {
        try
        {
            _vfs.MkDir(request.Path);
            return new MkDirResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new MkDirResponse { Success = false, Error = ex.Message };
        }
    }

    public RemoveResponse Remove(RemoveRequest request)
    {
        try
        {
            // DeleteFile deletes whatever node TryDelete finds — file or directory —
            // despite the name; there is no separate directory-delete path in FileSystem.
            var success = _vfs.DeleteFile(request.Path);
            return new RemoveResponse { Success = success };
        }
        catch (Exception ex)
        {
            return new RemoveResponse { Success = false, Error = ex.Message };
        }
    }

    public SubscribeResponse Subscribe(SubscribeRequest request, IAfcpSubscriptionSink sink)
    {
        // Layer 2 (live push) is deferred — reject for now.
        return new SubscribeResponse
        {
            Accepted = false,
            Error = "Subscribe is not supported in this build (Layer 1 only)."
        };
    }

    public void Unsubscribe(ulong subscriptionId)
    {
        // No-op: Subscribe is always rejected.
    }
}
