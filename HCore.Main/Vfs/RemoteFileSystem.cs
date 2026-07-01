using AFCP;
using AFCP.Protocol;
using System.Text;

namespace HCore.Main.Vfs;

/// <summary>
/// A read-only <see cref="IVirtualFileSystem"/> backed by a remote AFCP peer.
/// Serves the peer's entire root tree (not just <c>/proc</c>): the peer's
/// <c>VfsAfcpProvider</c> proxies its kernel <see cref="FileSystem"/>, which
/// mounts <c>/proc</c>, <c>/etc</c>, <c>/dev</c>, etc.
///
/// The tree is **lazy** (9P-style): each <see cref="RemoteDirectory"/> fetches its
/// entries via a fresh <see cref="AfcpClient.SyncAsync"/> on access, and each
/// <see cref="RemoteFile"/> fetches its bytes via <see cref="AfcpClient.ReadAsync"/>
/// on read — so <c>ls</c> walks into one directory at a time and <c>cat</c> always
/// sees the latest content (a live <c>/proc</c> facet is re-read on every access).
/// Nothing is cached.
/// </summary>
internal sealed class RemoteFileSystem : IVirtualFileSystem, IDisposable
{
    private readonly AfcpClient _client;

    public string Name => "afcp-remote";
    public bool IsReadOnly => true;
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

    public IVirtualDirectory CreateDirectory(string name) => throw new InvalidOperationException("Filesystem is read-only.");
    public bool TryDelete(string name) => throw new InvalidOperationException("Filesystem is read-only.");
    public IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default) => throw new InvalidOperationException("Filesystem is read-only.");

    private IVirtualNode ToNode(DirEntry entry)
    {
        var childRemote = ChildRemotePath(_remotePath, entry.Name);
        return entry.IsDirectory
            ? new RemoteDirectory(entry.Name, this, _client, childRemote)
            : new RemoteFile(entry.Name, this, _client, childRemote);
    }
}

/// <summary>
/// A read-only file whose content is fetched from the remote AFCP peer on every
/// read via <see cref="AfcpClient.ReadAsync"/> — never cached, so <c>cat</c> on a
/// live <c>/proc</c> facet always reflects the latest frame.
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
        if (access != FileAccess.Read)
        {
            throw new InvalidOperationException("File is read-only.");
        }

        return new MemoryStream(Fetch(), writable: false);
    }

    public byte[] ReadAllBytes() => Fetch();

    public string ReadString(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(Fetch());
    }

    public void Write(ReadOnlySpan<byte> data) => throw new InvalidOperationException("File is read-only.");
}
