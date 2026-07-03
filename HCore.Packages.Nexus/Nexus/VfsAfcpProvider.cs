using AFCP;
using AFCP.Protocol;
using HCore.Modules.Base;
using KASerializer;
using System.Collections.Concurrent;
using System.Reflection;

namespace HCore.Packages.Nexus;

/// <summary>
/// The <see cref="IAfcpProvider"/> backed by the kernel's VFS.
/// A generic VFS proxy: <see cref="Sync"/> lists any directory (via
/// <see cref="IKernelVfs.ListDirectory"/>, which includes child mounts like
/// <c>/proc</c>), <see cref="Read"/> returns any file's bytes (via
/// <see cref="IKernelVfs.GetFile"/>), and <see cref="Write"/>/<see cref="MkDir"/>/
/// <see cref="Remove"/> mutate the kernel VFS directly.
/// Because <c>/proc</c> is a live <see cref="ProcFileSystem"/> mount inside the
/// kernel VFS, facet files are rebuilt fresh on every <see cref="Read"/> — the
/// liveness is handled server-side for free, with no facet-specific protocol.
/// <see cref="Subscribe"/> is the exception to the pure-VFS proxy: it backs a
/// live push stream via <see cref="IFacetView"/> (facets are not files), streaming
/// serialized frames to the peer through the <see cref="IAfcpSubscriptionSink"/>.
/// </summary>
internal sealed class VfsAfcpProvider : IAfcpProvider
{
    private readonly IKernelVfs _vfs;
    private readonly IModuleResolver _moduleResolver;
    private readonly IFacetView _facetView;
    private readonly Serializer _serializer;

    // Active server-side subscriptions, keyed by the id handed to the peer. Each
    // wraps a local facet subscription that pushes EventNotify frames through
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

    public VfsAfcpProvider(IKernelVfs vfs, IModuleResolver moduleResolver, IFacetView facetView, Serializer serializer)
    {
        _vfs = vfs;
        _moduleResolver = moduleResolver;
        _facetView = facetView;
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
        var facet = _facetView.FindFacet(request.Path);
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
            if (!_moduleResolver.TryResolveInstance(request.InstancePath, out var instance))
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
