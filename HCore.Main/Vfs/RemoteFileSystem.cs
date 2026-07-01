using AFCP;
using AFCP.Protocol;
using HCore.Modules.Base;
using System.Text;
using System.Threading.Channels;

namespace HCore.Main.Vfs;

/// <summary>
/// Implemented by a mount that can back a remote data-plane subscription. Lets
/// <see cref="Internal.DataHost"/> redirect an <c>IDataHost.Subscribe&lt;T&gt;</c>
/// on a mounted path to the remote peer without <c>DataHost</c> depending on any
/// AFCP type — it only sees this interface plus <see cref="IVirtualFileSystem"/>.
/// </summary>
internal interface IRemoteDataSource
{
    /// <summary>
    /// Subscribe to a facet on the remote peer. <paramref name="remotePath"/> is
    /// the path as the peer sees it (mount prefix already stripped, e.g.
    /// <c>/proc/lidar/scan_data</c>). Semantics mirror the local
    /// <c>IDataHost.Subscribe&lt;T&gt;</c>: single-consumer, ordered handler
    /// invocation, observable <see cref="ISubscription.State"/>.
    /// </summary>
    ISubscription SubscribeData<T>(
        string remotePath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class;
}

/// <summary>
/// A read-write <see cref="IVirtualFileSystem"/> backed by a remote AFCP peer.
/// Serves the peer's entire root tree (not just <c>/proc</c>): the peer's
/// <c>VfsAfcpProvider</c> proxies its kernel <see cref="FileSystem"/>, which
/// mounts <c>/proc</c>, <c>/etc</c>, <c>/dev</c>, etc. No capability model exists
/// yet (TODO.md §C3) — any mounting peer can write anywhere under the served
/// root, same trusted-LAN gap as <c>Kill</c>.
///
/// The tree is **lazy** (9P-style): each <see cref="RemoteDirectory"/> fetches its
/// entries via a fresh <see cref="AfcpClient.SyncAsync"/> on access, and each
/// <see cref="RemoteFile"/> fetches its bytes via <see cref="AfcpClient.ReadAsync"/>
/// on read — so <c>ls</c> walks into one directory at a time and <c>cat</c> always
/// sees the latest content (a live <c>/proc</c> facet is re-read on every access).
/// Nothing is cached. Writes (<see cref="AfcpClient.WriteAsync"/>,
/// <see cref="AfcpClient.MkDirAsync"/>, <see cref="AfcpClient.RemoveAsync"/>) are
/// whole-file, single round-trip operations — no chunked/streaming write (see
/// TODO.md §C7e).
/// </summary>
internal sealed class RemoteFileSystem : IVirtualFileSystem, IDisposable, IRemoteDataSource
{
    private readonly AfcpClient _client;
    private readonly Serializer _serializer;

    public string Name => "afcp-remote";
    public bool IsReadOnly => false;
    public string RemoteEndpoint { get; }

    public RemoteFileSystem(AfcpClient client, string mountPoint, Serializer serializer)
    {
        _client = client;
        _serializer = serializer;
        RemoteEndpoint = mountPoint;
    }

    public IVirtualDirectory Root => new RemoteDirectory("/", null, _client, "/");

    public ISubscription SubscribeData<T>(
        string remotePath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class
        => RemoteSubscription<T>.Start(_client, _serializer, remotePath, handler, onDisconnected);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// A lazy directory backed by a remote AFCP <see cref="SyncAsync"/>. Every
/// enumeration/lookup does a fresh round-trip so newly-spawned remote instances
/// appear immediately — matching <see cref="ProcFileSystem"/>'s live-view model.
/// </summary>
internal sealed class RemoteDirectory : VirtualNode, IVirtualDirectory
{
    private readonly AfcpClient _client;
    private readonly string _remotePath;

    public RemoteDirectory(string name, IVirtualDirectory? parent, AfcpClient client, string remotePath)
        : base(name, parent)
    {
        _client = client;
        _remotePath = remotePath;
    }

    private DirEntry[] Fetch()
    {
        var res = _client.SyncAsync(_remotePath).GetAwaiter().GetResult();
        return res.Entries;
    }

    private static string ChildRemotePath(string parentRemote, string name)
        => parentRemote == "/" ? $"/{name}" : $"{parentRemote}/{name}";

    public IEnumerable<IVirtualNode> Enumerate()
    {
        return Fetch().Select(ToNode);
    }

    public IEnumerable<IVirtualNode> EnumerateDirectories()
        => Enumerate().Where(n => n is IVirtualDirectory);

    public IEnumerable<IVirtualNode> EnumerateFiles()
        => Enumerate().Where(n => n is IVirtualFile);

    public IVirtualDirectory? TryGetDirectory(string name)
        => Fetch().FirstOrDefault(e => e.IsDirectory && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } entry
            ? (IVirtualDirectory)ToNode(entry)
            : null;

    public IVirtualDirectory GetDirectory(string name)
        => TryGetDirectory(name) ?? throw new DirectoryNotFoundException($"Directory '{name}' not found in '{Path}'.");

    public IVirtualFile? TryGetFile(string name)
        => Fetch().FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } entry
            ? (IVirtualFile)ToNode(entry)
            : null;

    public IVirtualFile GetFile(string name)
        => TryGetFile(name) ?? throw new FileNotFoundException($"File '{name}' not found in '{Path}'.");

    public IVirtualNode? TryGet(string name)
        => Fetch().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } entry
            ? ToNode(entry)
            : null;

    public IVirtualDirectory CreateDirectory(string name)
    {
        var childRemote = ChildRemotePath(_remotePath, name);
        var res = _client.MkDirAsync(childRemote).GetAwaiter().GetResult();
        if (!res.Success)
        {
            throw new IOException(res.Error ?? $"Failed to create directory '{childRemote}' on the remote peer.");
        }

        return new RemoteDirectory(name, this, _client, childRemote);
    }

    public bool TryDelete(string name)
    {
        var childRemote = ChildRemotePath(_remotePath, name);
        var res = _client.RemoveAsync(childRemote).GetAwaiter().GetResult();
        return res.Success;
    }

    public IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default)
    {
        var childRemote = ChildRemotePath(_remotePath, name);
        var res = _client.WriteAsync(childRemote, initialData.ToArray(), overwrite).GetAwaiter().GetResult();
        if (!res.Success)
        {
            throw new IOException(res.Error ?? $"Failed to write '{childRemote}' on the remote peer.");
        }

        return new RemoteFile(name, this, _client, childRemote);
    }

    private IVirtualNode ToNode(DirEntry entry)
    {
        var childRemote = ChildRemotePath(_remotePath, entry.Name);
        return entry.IsDirectory
            ? new RemoteDirectory(entry.Name, this, _client, childRemote)
            : new RemoteFile(entry.Name, this, _client, childRemote);
    }
}

/// <summary>
/// A file whose content is fetched from the remote AFCP peer on every read via
/// <see cref="AfcpClient.ReadAsync"/> — never cached, so <c>cat</c> on a live
/// <c>/proc</c> facet always reflects the latest frame. Writes
/// (<see cref="Write"/>, or a write-access <see cref="GetStream"/>) are buffered
/// locally and sent as a single whole-file <see cref="AfcpClient.WriteAsync"/>
/// round-trip — AFCP has no chunked/streaming write (TODO.md §C7e).
/// </summary>
internal sealed class RemoteFile : VirtualNode, IVirtualFile
{
    private readonly AfcpClient _client;
    private readonly string _remotePath;

    public RemoteFile(string name, IVirtualDirectory parent, AfcpClient client, string remotePath)
        : base(name, parent)
    {
        _client = client;
        _remotePath = remotePath;
    }

    private byte[] Fetch()
    {
        var res = _client.ReadAsync(_remotePath).GetAwaiter().GetResult();
        return res.Exists ? (res.Data ?? Array.Empty<byte>()) : throw new FileNotFoundException($"File '{Path}' not found on the remote peer.");
    }

    public Stream GetStream(FileMode mode = FileMode.Open, FileAccess access = FileAccess.Read)
    {
        if (access == FileAccess.Read)
        {
            return new MemoryStream(Fetch(), writable: false);
        }

        // Append and Open(OrCreate) preserve existing remote content (seeded
        // then overwritten from the start, mirroring a real file handle);
        // Create/CreateNew/Truncate start from an empty buffer. Either way the
        // whole buffer is sent back as one Write on Dispose.
        byte[] initial = Array.Empty<byte>();
        if (mode is FileMode.Append or FileMode.Open or FileMode.OpenOrCreate)
        {
            try { initial = Fetch(); }
            catch (FileNotFoundException) { /* nothing to seed; start empty */ }
        }

        var stream = new RemoteWriteStream(_client, _remotePath, initial);
        if (mode != FileMode.Append)
        {
            stream.Position = 0;
        }

        return stream;
    }

    public byte[] ReadAllBytes() => Fetch();

    public string ReadString(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(Fetch());
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var res = _client.WriteAsync(_remotePath, data.ToArray(), overwrite: true).GetAwaiter().GetResult();
        if (!res.Success)
        {
            throw new IOException(res.Error ?? $"Failed to write '{Path}' on the remote peer.");
        }
    }

    /// <summary>
    /// A <see cref="MemoryStream"/> that flushes its full contents to the remote
    /// peer as a single <see cref="AfcpClient.WriteAsync"/> call on
    /// <see cref="Dispose(bool)"/> — the mount-side half of AFCP's whole-file
    /// <c>Write</c> verb. Not safe to reuse after disposal (matches
    /// <see cref="MemoryStream"/>'s own contract).
    /// </summary>
    private sealed class RemoteWriteStream : MemoryStream
    {
        private readonly AfcpClient _client;
        private readonly string _remotePath;

        public RemoteWriteStream(AfcpClient client, string remotePath, byte[] initialContent)
        {
            _client = client;
            _remotePath = remotePath;
            if (initialContent.Length > 0)
            {
                Write(initialContent, 0, initialContent.Length);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var res = _client.WriteAsync(_remotePath, ToArray(), overwrite: true).GetAwaiter().GetResult();
                if (!res.Success)
                {
                    throw new IOException(res.Error ?? $"Failed to write '{_remotePath}' on the remote peer.");
                }
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Mount-side <see cref="ISubscription"/> that wraps a remote AFCP subscription and
/// makes it look like a local one. AFCP notify frames are dispatched on thread-pool
/// threads (see <c>MultiplexedConnection</c>), so this adapter funnels them through a
/// bounded <see cref="Channel{T}"/> + a single consumer loop — the user handler is
/// therefore invoked by exactly one thread at a time, matching the local
/// single-consumer contract. Wire-order vs enqueue-order under concurrent dispatch
/// stays observable through <see cref="DataEvent{T}.Sequence"/>, the same as local
/// overflow gaps.
/// </summary>
internal sealed class RemoteSubscription<T> : ISubscription where T : class
{
    // Stream default (DATA_PLANE_DECISIONS.md B3): bounded 64, drop-oldest.
    private const int ChannelBound = 64;

    private readonly AfcpClient _client;
    private readonly Serializer _serializer;
    private readonly Func<DataEvent<T>, CancellationToken, ValueTask> _handler;
    private readonly Action<DisconnectReason>? _onDisconnected;
    private readonly Channel<DataEvent<T>> _channel;
    private readonly CancellationTokenSource _cts = new();

    private IAfcpSubscription? _remote;
    private int _state = (int)SubscriptionState.Active;
    private DisconnectReason? _disconnectReason;
    private long _consumerSkippedCount;

    public SubscriptionState State => (SubscriptionState)Volatile.Read(ref _state);
    public DisconnectReason? DisconnectReason => _disconnectReason;
    public long ConsumerSkippedCount => Interlocked.Read(ref _consumerSkippedCount);

    private RemoteSubscription(
        AfcpClient client,
        Serializer serializer,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected)
    {
        _client = client;
        _serializer = serializer;
        _handler = handler;
        _onDisconnected = onDisconnected;
        _channel = Channel.CreateBounded<DataEvent<T>>(new BoundedChannelOptions(ChannelBound)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public static RemoteSubscription<T> Start(
        AfcpClient client,
        Serializer serializer,
        string remotePath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected)
    {
        var sub = new RemoteSubscription<T>(client, serializer, handler, onDisconnected);
        _ = Task.Run(sub.ConsumeAsync);

        // SubscribeAsync throws (AfcpException) if the peer rejects the facet;
        // let that propagate to the caller, matching the local "No data facet" throw.
        sub._remote = client.SubscribeAsync(
            remotePath,
            onEvent: sub.OnRemoteEvent,
            onProducerGone: () => sub.Trip(HCore.Modules.Base.DisconnectReason.ProducerKilled),
            onError: reason => sub.Trip(MapError(reason)))
            .GetAwaiter().GetResult();

        return sub;
    }

    private void OnRemoteEvent(EventNotify evt)
    {
        if (State != SubscriptionState.Active) return;

        T value;
        using (var ms = new MemoryStream(evt.Data ?? Array.Empty<byte>()))
        {
            value = _serializer.Deserialize<T>(ms);
        }

        var frame = new DataEvent<T>
        {
            Data = value,
            Sequence = evt.Sequence,
            InterFrameDelta = evt.HasInterFrameDelta ? evt.InterFrameDelta : null,
        };

        // Bounded, drop-oldest: a slow local handler drops the oldest queued frame
        // rather than blocking the notify thread — Sequence gaps stay observable.
        _channel.Writer.TryWrite(frame);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var frame in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await _handler(frame, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    Interlocked.Increment(ref _consumerSkippedCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed / tripped — normal shutdown.
        }
    }

    private void Trip(DisconnectReason reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)SubscriptionState.Tripped, (int)SubscriptionState.Active)
            != (int)SubscriptionState.Active)
        {
            return;
        }

        _disconnectReason = reason;
        _cts.Cancel();
        _channel.Writer.TryComplete();

        try { _onDisconnected?.Invoke(reason); }
        catch { /* consumer callback must not tear us down */ }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, (int)SubscriptionState.Disposed, (int)SubscriptionState.Active)
            != (int)SubscriptionState.Active)
        {
            // Already tripped or disposed; still ensure the loop and remote are torn down.
            _cts.Cancel();
            _channel.Writer.TryComplete();
            return;
        }

        _disconnectReason = HCore.Modules.Base.DisconnectReason.Disposed;
        _cts.Cancel();
        _channel.Writer.TryComplete();

        var remote = _remote;
        if (remote is not null && _client.IsConnected)
        {
            try { _client.UnsubscribeAsync(remote).GetAwaiter().GetResult(); }
            catch { /* best-effort unsubscribe */ }
        }
    }

    private static DisconnectReason MapError(string reason)
        => reason.Contains("overload", StringComparison.OrdinalIgnoreCase)
            ? HCore.Modules.Base.DisconnectReason.Overload
            : HCore.Modules.Base.DisconnectReason.HandlerException;
}
