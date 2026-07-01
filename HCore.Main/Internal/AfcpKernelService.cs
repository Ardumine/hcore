using AFCP;
using AFCP.Protocol;
using HCore.Main.Vfs;
using HCore.Modules.Base;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
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
/// documented trusted-LAN gap as <c>Kill</c>. Subscribe-push (Layer 2) is now
/// wired: <c>VfsAfcpProvider.Subscribe</c> backs a live facet subscription via
/// <see cref="DataHost"/> and streams <c>EventNotify</c> frames to the peer.
/// MKCall (Layer 3) is wired too: <c>VfsAfcpProvider.Call</c> resolves a remote
/// instance via <see cref="ModuleHost"/> and invokes the named method through a
/// compiled <c>FastMethodInfo</c> delegate (cached per type+method).
/// </summary>
internal sealed class AfcpKernelService : IAfcpKernel
{
    private readonly FileSystem _vfs;
    private readonly ModuleHost _moduleHost;
    private readonly DataHost _dataHost;
    private readonly Serializer _serializer = new();

    private AfcpServer? _server;
    private int _servingPort;
    private readonly Dictionary<string, RemoteFileSystem> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AfcpKernelService(FileSystem vfs, ModuleHost moduleHost, DataHost dataHost)
    {
        _vfs = vfs;
        _moduleHost = moduleHost;
        _dataHost = dataHost;
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
            var provider = new VfsAfcpProvider(_vfs, _moduleHost, _dataHost, _serializer);
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
            var remote = new RemoteFileSystem(client, mountPoint, _serializer);
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
        var spawnedConsumer = false;

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

            // 8. C7c — MKCall (Layer 3). Get a remote ILidar proxy through the
            // mount and call methods on it. HCore.Main cannot reference the
            // Sensor package, so the interface Type is resolved from the
            // descriptor registry and the proxy is built / invoked reflectively
            // — the wire path is identical to a compile-time-typed call.
            Log("--- MKCall: resolve ILidar + build remote proxy (/selftest/proc/lidar) ---");
            var lidarIface = _moduleHost.GetModuleInterfaceType("HCore.Packages.Sensor.Lidar")
                ?? throw new InvalidOperationException("Sensor lidar module not registered; cannot run MKCall self-test.");
            var remoteLidar = _moduleHost.GetRemoteModuleInterface(lidarIface, "/selftest/proc/lidar");
            Log("got remote proxy.");

            static object CallRemote(object proxy, Type iface, string method, params object[] args)
            {
                var argTypes = args.Length == 0 ? Type.EmptyTypes : args.Select(a => a.GetType()).ToArray();
                return iface.GetMethod(method, argTypes)!.Invoke(proxy, args)!;
            }

            Log("--- MKCall: SetFrameRate(50) (void + int arg) ---");
            CallRemote(remoteLidar, lidarIface, "SetFrameRate", 50);
            Log("ok.");

            Log("--- MKCall: GetFrameRate() (int return) ---");
            var rate = (int)CallRemote(remoteLidar, lidarIface, "GetFrameRate");
            Log($"returned {rate}");
            if (rate != 50)
            {
                throw new InvalidOperationException($"expected GetFrameRate()==50, got {rate}.");
            }

            Log("--- MKCall: GetName() (string return) ---");
            var name = (string)CallRemote(remoteLidar, lidarIface, "GetName");
            Log($"returned '{name}'");
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("expected a non-empty name from GetName().");
            }

            // A proxy to a non-existent instance is created eagerly (no instance
            // check at creation time — the server only learns the path on first
            // call); the first call must surface the server-side "not found" as a
            // RemoteCallException.
            Log("--- MKCall: failing call on /selftest/proc/nope ---");
            var nope = _moduleHost.GetRemoteModuleInterface(lidarIface, "/selftest/proc/nope");
            try
            {
                CallRemote(nope, lidarIface, "GetFrameRate");
                throw new InvalidOperationException("expected a remote call on a missing instance to throw.");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is RemoteCallException rce)
            {
                Log($"correctly threw RemoteCallException: {rce.Message}");
            }

            // 9. C7a — remote writes: mkdir, write, cat back, delete, confirm gone.
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

            // 10. C7b — subscribe-push over the wire (raw client). Prove the server
            // pushes live EventNotify frames with advancing sequence and real bytes.
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

                Thread.Sleep(400); // lidar publishes every 100ms -> expect ~4 frames
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

            // 11. C7b transparent typed path — spawn the demo consumer, point it at the
            // MOUNTED facet, and confirm it receives typed ScanFrame frames via the
            // ordinary Data.Subscribe<T> (the consumer never knows the facet is remote).
            Log("--- transparent subscribe via demo consumer (/selftest/proc/lidar/scan_data) ---");
            _vfs.CreateFile("/tmp/remote_slam_target", Encoding.UTF8.GetBytes("/selftest/proc/lidar/scan_data"));
            _moduleHost.Spawn<IRunnable>("HCore.Packages.Sensor.RemoteSlam", "rslam").Run();
            spawnedConsumer = true;
            Thread.Sleep(450);

            var status = _vfs.GetFile("/proc/rslam/recv_status").ReadString().TrimEnd();
            Log($"consumer status: {status}");
            var received = ParseStatusLong(status, "received=");
            if (received < 2)
            {
                throw new InvalidOperationException($"consumer received {received} frames over the mount, expected >=2.");
            }

            // 12. ProducerKilled over the wire — kill the lidar, confirm the consumer's
            // remote subscription trips with ProducerKilled (sink.ProducerGone path).
            Log("--- kill lidar -> expect consumer ProducerKilled ---");
            _moduleHost.Kill("lidar");
            spawnedLidar = false; // reaped here; don't double-kill in finally
            Thread.Sleep(300);

            var afterKill = _vfs.GetFile("/proc/rslam/recv_status").ReadString().TrimEnd();
            Log($"consumer status after kill: {afterKill}");
            if (!afterKill.Contains("state=ProducerKilled"))
            {
                throw new InvalidOperationException($"expected consumer state=ProducerKilled, got '{afterKill}'.");
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
            if (spawnedConsumer)
            {
                try { _moduleHost.Kill("rslam"); Log("killed rslam."); } catch { }
            }
            try { _vfs.DeleteFile("/tmp/remote_slam_target"); } catch { }
            try { Log(Unmount("/selftest")); } catch { }
            try { Log(StopServe()); } catch { }
            if (spawnedLidar)
            {
                try { _moduleHost.Kill("lidar"); Log("killed lidar."); } catch { }
            }
        }

        return sb.ToString();
    }

    /// <summary>Extract the integer following <paramref name="key"/> in a status line
    /// like <c>received=5 lastSeq=42 state=Active</c>, or -1 if absent.</summary>
    private static long ParseStatusLong(string status, string key)
    {
        var idx = status.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        var start = idx + key.Length;
        var end = start;
        while (end < status.Length && (char.IsDigit(status[end]) || (end == start && status[end] == '-'))) end++;
        return long.TryParse(status.AsSpan(start, end - start), out var value) ? value : -1;
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
/// <see cref="Subscribe"/> is the exception to the pure-VFS proxy: it backs a
/// live push stream via <see cref="DataHost"/> (facets are not files), streaming
/// serialized frames to the peer through the <see cref="IAfcpSubscriptionSink"/>.
/// </summary>
internal sealed class VfsAfcpProvider : IAfcpProvider
{
    private readonly FileSystem _vfs;
    private readonly ModuleHost _moduleHost;
    private readonly DataHost _dataHost;
    private readonly Serializer _serializer;

    // Active server-side subscriptions, keyed by the id handed to the peer. Each
    // wraps a local DataHost subscription that pushes EventNotify frames through
    // the peer's sink. Disposed on Unsubscribe or when the connection closes.
    private readonly Dictionary<ulong, ISubscription> _subscriptions = new();
    private readonly object _subLock = new();
    private ulong _nextSubscriptionId;

    // Layer 3 — MKCall: compiled invokers keyed by (declaring type, method name,
    // parameter type names). Resolved once per (type, method) pair on first call,
    // then reused — no per-call reflection or string-to-Type resolution. The key
    // is a struct with sequence-equal string-array semantics (arrays don't
    // override Equals/GetHashCode). V2's equivalent was a per-path
    // ReturnValueDelegate[] indexed by a uint method id; V3 keys by name +
    // param-type names instead (overload-safe, no shared enumeration contract).
    private readonly ConcurrentDictionary<CallKey, FastMethodInfo> _callCache = new();

    public VfsAfcpProvider(FileSystem vfs, ModuleHost moduleHost, DataHost dataHost, Serializer serializer)
    {
        _vfs = vfs;
        _moduleHost = moduleHost;
        _dataHost = dataHost;
        _serializer = serializer;
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
        var facet = _dataHost.FindFacet(request.Path);
        if (facet is null)
        {
            return new SubscribeResponse
            {
                Accepted = false,
                Error = $"No data facet at '{request.Path}'."
            };
        }

        var valueType = facet.ValueType;
        var typeName = valueType.AssemblyQualifiedName;

        // Reserve the id before subscribing so a frame that arrives synchronously
        // during SubscribeRaw already sees a stable SubscriptionId.
        ulong id;
        lock (_subLock)
        {
            id = ++_nextSubscriptionId;
        }

        ISubscription sub;
        try
        {
            sub = facet.SubscribeRaw(
                (value, sequence, delta, ct) =>
                {
                    byte[] data;
                    using (var ms = new MemoryStream())
                    {
                        _serializer.Serialize(ms, value, valueType);
                        data = ms.ToArray();
                    }

                    sink.Push(new EventNotify
                    {
                        SubscriptionId = id,
                        Sequence = sequence,
                        InterFrameDelta = delta ?? 0,
                        HasInterFrameDelta = delta.HasValue,
                        Data = data,
                        ValueTypeFullName = typeName
                    });
                    return ValueTask.CompletedTask;
                },
                reason =>
                {
                    if (reason == DisconnectReason.ProducerKilled)
                    {
                        sink.ProducerGone(id);
                    }
                    else if (reason != DisconnectReason.Disposed)
                    {
                        sink.Error(id, reason.ToString());
                    }

                    lock (_subLock) { _subscriptions.Remove(id); }
                });
        }
        catch (Exception ex)
        {
            return new SubscribeResponse { Accepted = false, Error = ex.Message };
        }

        lock (_subLock) { _subscriptions[id] = sub; }

        return new SubscribeResponse
        {
            Accepted = true,
            SubscriptionId = id,
            ValueTypeFullName = typeName
        };
    }

    public void Unsubscribe(ulong subscriptionId)
    {
        ISubscription? sub;
        lock (_subLock) { _subscriptions.Remove(subscriptionId, out sub); }
        sub?.Dispose();
    }

    public CallResponse Call(CallRequest request)
    {
        try
        {
            if (!_moduleHost.TryResolveInstance(request.InstancePath, out var instance))
            {
                return new CallResponse
                {
                    Success = false,
                    Error = $"No running instance at '{request.InstancePath}'."
                };
            }

            var declaringType = instance.GetType();
            var key = new CallKey(declaringType, request.MethodName, request.ParamTypeNames);

            var invoker = _callCache.GetOrAdd(key, k =>
            {
                var (paramTypes, resolveErr) = ResolveParamTypes(k.ParamTypeNames);
                if (resolveErr is not null)
                {
                    throw new InvalidOperationException(resolveErr);
                }

                var method = k.DeclaringType.GetMethod(
                    k.MethodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: paramTypes!,
                    modifiers: null);

                if (method is null)
                {
                    throw new MissingMethodException(
                        k.DeclaringType.FullName, $"{k.MethodName}({string.Join(", ", k.ParamTypeNames)})");
                }

                return new FastMethodInfo(method);
            });

            var result = invoker.Invoke(instance, request.Args);
            return new CallResponse { Success = true, ReturnValue = result };
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap the real exception thrown by the method itself.
            var real = ex.InnerException ?? ex;
            return new CallResponse { Success = false, Error = $"{real.GetType().FullName}: {real.Message}" };
        }
        catch (Exception ex)
        {
            return new CallResponse { Success = false, Error = $"{ex.GetType().FullName}: {ex.Message}" };
        }
    }

    /// <summary>
    /// Resolve each parameter type name (assembly-qualified) to a <see cref="Type"/>.
    /// Returns an error string instead of throwing so the caller can fold it into
    /// a <see cref="CallResponse"/> without a try/catch around the cache miss path.
    /// </summary>
    private static (Type[]? Types, string? Error) ResolveParamTypes(string[] names)
    {
        if (names.Length == 0) return (Type.EmptyTypes, null);

        var types = new Type[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var t = Type.GetType(names[i]);
            if (t is null)
            {
                return (null, $"Could not resolve parameter type '{names[i]}'. Is its assembly loaded on the serving peer?");
            }
            types[i] = t;
        }
        return (types, null);
    }

    /// <summary>
    /// Cache key for the MKCall invoker. <see cref="string"/>[] does not override
    /// <see cref="object.Equals(object?)"/>, so this struct implements sequence
    /// equality + a precomputed hash (built once over the param names at key
    /// construction). Two requests for the same (type, method, param signature)
    /// hit the same <see cref="FastMethodInfo"/>.
    /// </summary>
    private readonly struct CallKey : IEquatable<CallKey>
    {
        public Type DeclaringType { get; }
        public string MethodName { get; }
        public string[] ParamTypeNames { get; }
        private readonly int _hashCode;

        public CallKey(Type declaringType, string methodName, string[] paramTypeNames)
        {
            DeclaringType = declaringType;
            MethodName = methodName;
            ParamTypeNames = paramTypeNames;

            var h = new HashCode();
            h.Add(declaringType);
            h.Add(methodName, StringComparer.Ordinal);
            for (var i = 0; i < paramTypeNames.Length; i++)
            {
                h.Add(paramTypeNames[i], StringComparer.Ordinal);
            }
            _hashCode = h.ToHashCode();
        }

        public bool Equals(CallKey other)
        {
            if (!ReferenceEquals(DeclaringType, other.DeclaringType)
                || !string.Equals(MethodName, other.MethodName, StringComparison.Ordinal))
            {
                return false;
            }
            if (ParamTypeNames.Length != other.ParamTypeNames.Length) return false;
            for (var i = 0; i < ParamTypeNames.Length; i++)
            {
                if (!string.Equals(ParamTypeNames[i], other.ParamTypeNames[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is CallKey other && Equals(other);
        public override int GetHashCode() => _hashCode;
    }
}
