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

// --- Sync (list facets under a path prefix) ---

public sealed class SyncRequest
{
    public string PathPrefix { get; set; } = string.Empty;
}

public sealed class FacetInfo
{
    public string Path { get; set; } = string.Empty;
    public string ValueTypeFullName { get; set; } = string.Empty;
    public FacetPrimitiveKind Kind { get; set; }
}

public sealed class SyncResponse
{
    public FacetInfo[] Facets { get; set; } = Array.Empty<FacetInfo>();
}

// --- Read (snapshot) ---

public sealed class ReadRequest
{
    public string Path { get; set; } = string.Empty;
}

public sealed class ReadResponse
{
    /// <summary>The serialized current value, or null if nothing has been published yet.</summary>
    public byte[]? Data { get; set; }
    public string? ValueTypeFullName { get; set; }
    public long Sequence { get; set; }
    public long InterFrameDelta { get; set; }
    public bool HasInterFrameDelta { get; set; }
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
