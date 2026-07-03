using HCore.Modules.Base;
using KASerializer;
using AFCP;
using AFCP.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;

namespace HCore.Packages.Nexus;

public sealed class AfcpImplement : BaseImplement, IAfcpKernel, IDriverModule, IRemoteMountHook
{
    private IKernelVfs _kernelVfs = null!;
    private IFacetView _facetView = null!;
    private IModuleResolver _moduleResolver = null!;
    private readonly KASerializer.Serializer _serializer = new();

    private AfcpServer? _server;
    private int _servingPort;
    private readonly Dictionary<string, RemoteFileSystem> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    void IDriverModule.Init(IKernelVfs kernelVfs, IFacetView facetView, IModuleResolver moduleResolver)
    {
        _kernelVfs = kernelVfs;
        _facetView = facetView;
        _moduleResolver = moduleResolver;
    }

    public string Serve(int port)
    {
        lock (_lock)
        {
            if (_server is not null)
                return $"already serving on port {_servingPort}; stop first.";

            var endpoint = new IPEndPoint(IPAddress.Any, port);
            var provider = new VfsAfcpProvider(_kernelVfs, _moduleResolver, _facetView, _serializer);
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
                return "not serving.";

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
                return $"mount point '{mountPoint}' is already in use; unmount first.";

            var client = new AfcpClient(_serializer);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            if (!IPAddress.TryParse(host, out var address))
            {
                var resolved = Dns.GetHostAddresses(host);
                address = resolved.Length > 0 ? resolved[0] : IPAddress.Loopback;
                endpoint = new IPEndPoint(address, port);
            }

            client.ConnectAsync(endpoint, "hcore").GetAwaiter().GetResult();
            var remote = new RemoteFileSystem(client, mountPoint, _serializer);
            _kernelVfs.Mount(mountPoint, remote);
            _mounts[mountPoint] = remote;
            return $"mounted {host}:{port} at {mountPoint}.";
        }
    }

    public string Unmount(string mountPoint)
    {
        lock (_lock)
        {
            if (!_mounts.Remove(mountPoint, out var remote))
                return $"no AFCP mount at '{mountPoint}'.";

            _kernelVfs.Unmount(mountPoint);
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
                lines.Add($"serving: port {_servingPort}");
            else
                lines.Add("serving: (stopped)");

            lines.Add($"mounts: {_mounts.Count}");
            foreach (var kv in _mounts)
                lines.Add($"  {kv.Key} -> {kv.Value.RemoteEndpoint}");

            return string.Join('\n', lines);
        }
    }

    public string SelfTest()
    {
        var sb = new StringBuilder();
        var port = 8765;
        var spawnedLidar = false;
        var spawnedConsumer = false;

        void Log(string s) { sb.AppendLine(s); Console.WriteLine($"[afcp-test] {s}"); }

        try
        {
            // 1. Ensure the lidar demo producer is running (spawn+run if absent).
            try
            {
                var lidar = Host.GetModuleInterface<IRunnable>("lidar");
                Log("lidar already running.");
            }
            catch
            {
                Log("spawning lidar...");
                var lidar = Host.Spawn<IRunnable>("HCore.Packages.Sensor.Lidar", "lidar");
                lidar.Run();
                spawnedLidar = true;
                Thread.Sleep(300);
                Log("lidar running.");
            }

            // 2. Serve.
            Log($"serve on {port}...");
            Log(Serve(port));

            // 3. Mount the local server back into our own VFS.
            Log("mount 127.0.0.1 -> /selftest...");
            Log(Mount("127.0.0.1", port, "/selftest"));

            // 4. ls /selftest
            Log("--- ls /selftest ---");
            foreach (var entry in _kernelVfs.ListDirectory("/selftest"))
                Log(entry);

            // 5. ls /selftest/etc
            Log("--- ls /selftest/etc ---");
            foreach (var entry in _kernelVfs.ListDirectory("/selftest/etc"))
                Log(entry);

            // 6. ls /selftest/proc/lidar
            Log("--- ls /selftest/proc/lidar ---");
            foreach (var entry in _kernelVfs.ListDirectory("/selftest/proc/lidar"))
                Log(entry);

            // 7. cat /selftest/proc/lidar/scan_data (twice)
            Log("--- cat /selftest/proc/lidar/scan_data ---");
            Log(_kernelVfs.GetFile("/selftest/proc/lidar/scan_data").ReadString().TrimEnd());
            Thread.Sleep(250);
            Log("--- cat again (fresh frame) ---");
            Log(_kernelVfs.GetFile("/selftest/proc/lidar/scan_data").ReadString().TrimEnd());

            // 8. MKCall (Layer 3) — reflection-based remote proxy
            Log("--- MKCall: resolve ILidar + build remote proxy (/selftest/proc/lidar) ---");
            var lidarIface = _moduleResolver.GetModuleInterfaceType("HCore.Packages.Sensor.Lidar")
                ?? throw new InvalidOperationException("Sensor lidar module not registered; cannot run MKCall self-test.");

            var remoteLidar = CreateReflectiveProxy(lidarIface, "/selftest/proc/lidar");
            Log("got remote proxy.");

            static object CallRemote(object proxy, Type iface, string method, params object[] args)
            {
                var argTypes = args.Length == 0 ? Type.EmptyTypes : args.Select(a => a.GetType()).ToArray();
                return iface.GetMethod(method, argTypes)!.Invoke(proxy, args)!;
            }

            Log("--- MKCall: SetFrameRate(50) ---");
            CallRemote(remoteLidar, lidarIface, "SetFrameRate", 50);
            Log("ok.");

            Log("--- MKCall: GetFrameRate() ---");
            var rate = (int)CallRemote(remoteLidar, lidarIface, "GetFrameRate");
            Log($"returned {rate}");
            if (rate != 50)
                throw new InvalidOperationException($"expected GetFrameRate()==50, got {rate}.");

            Log("--- MKCall: GetName() ---");
            var name = (string)CallRemote(remoteLidar, lidarIface, "GetName");
            Log($"returned '{name}'");
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("expected a non-empty name from GetName().");

            // Failing call on non-existent instance
            Log("--- MKCall: failing call on /selftest/proc/nope ---");
            var nope = CreateReflectiveProxy(lidarIface, "/selftest/proc/nope");
            try
            {
                CallRemote(nope, lidarIface, "GetFrameRate");
                throw new InvalidOperationException("expected a remote call on a missing instance to throw.");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is RemoteCallException rce)
            {
                Log($"correctly threw RemoteCallException: {rce.Message}");
            }

            // 9. Remote writes
            Log("--- mkdir /selftest/tmp/afcp_test ---");
            _kernelVfs.MkDir("/selftest/tmp/afcp_test");
            Log("ok.");

            Log("--- write /selftest/tmp/afcp_test/hello.txt ---");
            _kernelVfs.CreateFile("/selftest/tmp/afcp_test/hello.txt", Encoding.UTF8.GetBytes("hello from afcp"));
            var written = _kernelVfs.GetFile("/selftest/tmp/afcp_test/hello.txt").ReadString();
            Log($"read back: '{written}'");
            if (written != "hello from afcp")
                throw new InvalidOperationException($"write round-trip mismatch: got '{written}'.");

            Log("--- rm /selftest/tmp/afcp_test/hello.txt ---");
            if (!_kernelVfs.DeleteFile("/selftest/tmp/afcp_test/hello.txt"))
                throw new InvalidOperationException("delete of the scratch file failed.");

            if (_kernelVfs.Exists("/selftest/tmp/afcp_test/hello.txt"))
                throw new InvalidOperationException("scratch file still exists after delete.");

            Log("--- rmdir /selftest/tmp/afcp_test ---");
            if (!_kernelVfs.DeleteFile("/selftest/tmp/afcp_test"))
                throw new InvalidOperationException("delete of the scratch directory failed.");

            // 10. Subscribe-push (Layer 2) — raw client
            Log("--- subscribe /proc/lidar/scan_data (raw client) ---");
            var seqs = new List<long>();
            var dataNonEmpty = true;
            var probe = new AfcpClient(_serializer);
            try
            {
                probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), "selftest-probe").GetAwaiter().GetResult();
                var sub = probe.SubscribeAsync(
                    "/proc/lidar/scan_data",
                    onEvent: evt =>
                    {
                        lock (seqs)
                        {
                            seqs.Add(evt.Sequence);
                            if (evt.Data is null || evt.Data.Length == 0) dataNonEmpty = false;
                        }
                    }).GetAwaiter().GetResult();

                Thread.Sleep(400);
                probe.UnsubscribeAsync(sub).GetAwaiter().GetResult();
            }
            finally
            {
                probe.Dispose();
            }

            int frameCount;
            bool increasing;
            lock (seqs)
            {
                frameCount = seqs.Count;
                increasing = true;
                for (var i = 1; i < seqs.Count; i++)
                {
                    if (seqs[i] <= seqs[i - 1]) { increasing = false; break; }
                }
            }

            Log($"received {frameCount} pushed frames; increasing={increasing}; dataNonEmpty={dataNonEmpty}");
            if (frameCount < 2) throw new InvalidOperationException($"expected >=2 pushed frames, got {frameCount}.");
            if (!increasing) throw new InvalidOperationException("pushed frame sequence numbers not strictly increasing.");
            if (!dataNonEmpty) throw new InvalidOperationException("a pushed frame carried empty Data.");

            // 11. Transparent typed subscribe via demo consumer
            Log("--- transparent subscribe via demo consumer (/selftest/proc/lidar/scan_data) ---");
            _kernelVfs.CreateFile("/tmp/remote_slam_target", Encoding.UTF8.GetBytes("/selftest/proc/lidar/scan_data"));
            Host.Spawn<IRunnable>("HCore.Packages.Sensor.RemoteSlam", "rslam").Run();
            spawnedConsumer = true;
            Thread.Sleep(450);

            var status = _kernelVfs.GetFile("/proc/rslam/recv_status").ReadString().TrimEnd();
            Log($"consumer status: {status}");
            var received = ParseStatusLong(status, "received=");
            if (received < 2)
                throw new InvalidOperationException($"consumer received {received} frames over the mount, expected >=2.");

            // 12. ProducerKilled over the wire
            Log("--- kill lidar -> expect consumer ProducerKilled ---");
            Host.Kill("lidar");
            spawnedLidar = false;
            Thread.Sleep(300);

            var afterKill = _kernelVfs.GetFile("/proc/rslam/recv_status").ReadString().TrimEnd();
            Log($"consumer status after kill: {afterKill}");
            if (!afterKill.Contains("state=ProducerKilled"))
                throw new InvalidOperationException($"expected consumer state=ProducerKilled, got '{afterKill}'.");

            Log("--- SELFTEST PASSED ---");
        }
        catch (Exception ex)
        {
            Log($"SELFTEST FAILED: {ex}");
            throw;
        }
        finally
        {
            if (spawnedConsumer)
            {
                try { Host.Kill("rslam"); Log("killed rslam."); } catch { }
            }
            try { _kernelVfs.DeleteFile("/tmp/remote_slam_target"); } catch { }
            try { Log(Unmount("/selftest")); } catch { }
            try { Log(StopServe()); } catch { }
            if (spawnedLidar)
            {
                try { Host.Kill("lidar"); Log("killed lidar."); } catch { }
            }
        }

        return sb.ToString();
    }

    private static long ParseStatusLong(string status, string key)
    {
        var idx = status.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        var start = idx + key.Length;
        var end = start;
        while (end < status.Length && (char.IsDigit(status[end]) || (end == start && status[end] == '-'))) end++;
        return long.TryParse(status.AsSpan(start, end - start), out var value) ? value : -1;
    }

    /// <summary>Create a reflective RemoteModuleProxy for an interface type.</summary>
    internal static object CreateReflectiveProxy(Type interfaceType, string remotePath)
    {
        var proxyType = typeof(RemoteModuleProxy<>).MakeGenericType(interfaceType);
        var createMethod = proxyType.GetMethod(
            nameof(RemoteModuleProxy<IModule>.Create),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return createMethod.Invoke(null, new object[] { remotePath })!;
    }

    // IRemoteMountHook — called by the kernel before local /proc lookup.
    // The AFCP module checks whether the path falls under one of its mounts
    // and transparently redirects subscribe/mkcall to the peer.
    ISubscription? IRemoteMountHook.TrySubscribeRemote<T>(string facetPath, Func<DataEvent<T>, CancellationToken, ValueTask> handler, Action<DisconnectReason>? onDisconnected)
    {
        if (_kernelVfs.TryResolveMount(facetPath, out var fs, out var remotePath)
            && fs is IRemoteDataSource remote)
        {
            return remote.SubscribeData<T>(remotePath, handler, onDisconnected);
        }
        return null;
    }

    T IRemoteMountHook.TryGetRemoteInterface<T>(string instancePath)
    {
        // RemoteFileSystem is in this module, so we can create the proxy locally
        if (_kernelVfs.TryResolveMount(instancePath, out var fs, out var remotePath)
            && fs is RemoteFileSystem remote)
        {
            return RemoteModuleProxy<T>.Create(remote.Client, remotePath);
        }
        return default!;
    }
}
