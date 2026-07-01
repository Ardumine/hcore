namespace AFCP.Protocol;

// --- Connect (handshake) ---

public sealed class ConnectRequest
{
    public ushort ProtocolVersion { get; set; }
    public string PeerName { get; set; } = string.Empty;
}

public sealed class ConnectResponse
{
    public ushort ProtocolVersion { get; set; }
    public string PeerName { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string? Error { get; set; }
}

// --- Sync (list a directory) ---

public sealed class SyncRequest
{
    /// <summary>Absolute path on the peer, e.g. <c>/</c>, <c>/proc</c>, <c>/etc</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>One entry in a remote directory listing.</summary>
public sealed class DirEntry
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
}

public sealed class SyncResponse
{
    public DirEntry[] Entries { get; set; } = Array.Empty<DirEntry>();
}

// --- Read (file contents / facet snapshot) ---

public sealed class ReadRequest
{
    public string Path { get; set; } = string.Empty;
}

public sealed class ReadResponse
{
    /// <summary>The file's raw bytes (the server reads live from its VFS on every request).</summary>
    public byte[]? Data { get; set; }
    /// <summary>True if the path exists; false lets the client report "not found".</summary>
    public bool Exists { get; set; }
}

// --- Subscribe (push) ---

public sealed class SubscribeRequest
{
    public string Path { get; set; } = string.Empty;
}

public sealed class SubscribeResponse
{
    public ulong SubscriptionId { get; set; }
    public bool Accepted { get; set; }
    public string? Error { get; set; }
    /// <summary>The type of the facet's values, so the subscriber can pick a deserializer.</summary>
    public string? ValueTypeFullName { get; set; }
}

public sealed class UnsubscribeRequest
{
    public ulong SubscriptionId { get; set; }
}

// --- Notify-only (push) ---

public sealed class EventNotify
{
    public ulong SubscriptionId { get; set; }
    public long Sequence { get; set; }
    public long InterFrameDelta { get; set; }
    public bool HasInterFrameDelta { get; set; }
    /// <summary>Serialized value for this frame, or null for a "no current value" tick.</summary>
    public byte[]? Data { get; set; }
    public string? ValueTypeFullName { get; set; }
}

public sealed class ProducerGoneNotify
{
    public ulong SubscriptionId { get; set; }
}

public sealed class SubscriptionErrorNotify
{
    public ulong SubscriptionId { get; set; }
    public string? Reason { get; set; }
}
