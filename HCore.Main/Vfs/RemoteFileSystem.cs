using AFCP;
using AFCP.Protocol;
using System.Text;

namespace HCore.Main.Vfs;

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
internal sealed class RemoteFileSystem : IVirtualFileSystem, IDisposable
{
    private readonly AfcpClient _client;

    public string Name => "afcp-remote";
    public bool IsReadOnly => false;
    public string RemoteEndpoint { get; }

    public RemoteFileSystem(AfcpClient client, string mountPoint)
    {
        _client = client;
        RemoteEndpoint = mountPoint;
    }

    public IVirtualDirectory Root => new RemoteDirectory("/", null, _client, "/");

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
