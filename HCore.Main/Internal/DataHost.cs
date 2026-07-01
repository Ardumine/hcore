using System.Diagnostics;
using HCore.Main.Vfs;
using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// The kernel-side data-plane broker. Maintains the per-facet registry, brokers
/// <see cref="IDataHost.ExposeData{T}"/> / <see cref="IDataHost.ReadData{T}"/> /
/// <see cref="IDataHost.Subscribe{T}"/>, and wires producer death
/// (<see cref="NotifyProducerKilled"/>) into the cascade-kill path.
///
/// Reaches modules only through the <see cref="IDataHost"/> surface; each created
/// instance is injected with a <see cref="ScopedDataHost"/> (owner-bound for
/// <c>ExposeData</c>, pass-through for <c>ReadData</c>/<c>Subscribe</c>).
/// </summary>
public sealed class DataHost
{
    private readonly Dictionary<(string Instance, string Facet), IFacet> _facets = new();
    private readonly object _lock = new();

    // The kernel VFS, consulted in Subscribe<T> to detect a remote-mounted facet
    // path and transparently redirect the subscribe to the peer (9P-style: remoteness
    // is a path prefix). One-way dependency; the VFS knows nothing about DataHost.
    private readonly FileSystem _vfs;

    public DataHost(FileSystem vfs)
    {
        _vfs = vfs;
    }

    public IExposedData<T> ExposeData<T>(
        string owner,
        string facetName,
        FacetKind kind,
        DispatchPolicy policy,
        int bound,
        Func<T, string>? formatter) where T : class
    {
        RejectSlash(facetName, "Facet name");
        var key = (owner, facetName);
        var logger = new ModuleLogger($"{owner}/{facetName}");
        var facet = new Facet<T>(owner, facetName, kind, policy, bound, formatter, logger);

        lock (_lock)
        {
            if (_facets.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"A data facet named '{facetName}' is already exposed on instance '{owner}'.");
            }

            _facets[key] = facet;
        }

        return facet;
    }

    public T? ReadData<T>(string facetPath) where T : class
    {
        var (instance, facetName) = ParseFacetPath(facetPath);
        IFacet? raw;
        lock (_lock)
        {
            _facets.TryGetValue((instance, facetName), out raw);
        }

        if (raw is null)
        {
            throw new InvalidOperationException($"No data facet at '{facetPath}'.");
        }

        if (raw.ValueType != typeof(T))
        {
            throw new InvalidOperationException(
                $"Facet '{facetPath}' exposes {raw.ValueType.FullName}, not {typeof(T).FullName}.");
        }

        return ((Facet<T>)raw).ReadCurrent();
    }

    public ISubscription Subscribe<T>(
        string facetPath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class
    {
        // Remoteness is a path prefix: if the path resolves to a remote AFCP mount,
        // redirect the subscribe to the peer transparently. Local /proc paths resolve
        // to ProcFileSystem (not an IRemoteDataSource) and fall through below.
        if (_vfs.TryResolveMount(facetPath, out var fs, out var remotePath)
            && fs is IRemoteDataSource remote)
        {
            return remote.SubscribeData<T>(remotePath, handler, onDisconnected);
        }

        var (instance, facetName) = ParseFacetPath(facetPath);
        IFacet? raw;
        lock (_lock)
        {
            _facets.TryGetValue((instance, facetName), out raw);
        }

        if (raw is null)
        {
            throw new InvalidOperationException(
                $"No data facet at '{facetPath}'. Subscribe after the producer has exposed it.");
        }

        if (raw.ValueType != typeof(T))
        {
            throw new InvalidOperationException(
                $"Facet '{facetPath}' exposes {raw.ValueType.FullName}, not {typeof(T).FullName}.");
        }

        return ((Facet<T>)raw).Subscribe(handler, onDisconnected);
    }

    /// <summary>
    /// Called by <c>ModuleHost</c> when an instance is reaped (cascade kill /
    /// direct kill). Removes every facet owned by that instance and fires
    /// <see cref="DisconnectReason.ProducerKilled"/> to all their subscribers —
    /// extending cascade kill across the subscriber tree.
    /// </summary>
    public void NotifyProducerKilled(string instanceName)
    {
        List<IFacet> toKill;
        lock (_lock)
        {
            toKill = _facets
                .Where(kv => kv.Key.Instance == instanceName)
                .Select(kv => kv.Value)
                .ToList();

            foreach (var f in toKill)
            {
                _facets.Remove((f.InstanceName, f.FacetName));
            }
        }

        foreach (var facet in toKill)
        {
            facet.NotifyProducerKilled();
        }
    }

    /// <summary>Snapshot of exposed facets for the synthetic <c>/proc</c> view.</summary>
    internal IReadOnlyList<FacetInfo> GetFacetsForProc()
    {
        lock (_lock)
        {
            return _facets.Values
                .Select(f => new FacetInfo(f.InstanceName, f.FacetName, f.FormatForCat, f.ValueType, f.Kind))
                .ToList();
        }
    }

    /// <summary>
    /// Snapshot of exposed facets for the AFCP provider — the raw facet objects,
    /// so the provider can read the formatted value and the type/kind metadata
    /// without going through the typed <see cref="ReadData{T}"/> path (which
    /// requires the caller to know the value type at compile time).
    /// </summary>
    internal IReadOnlyList<IFacet> GetFacetSnapshot()
    {
        lock (_lock)
        {
            return _facets.Values.ToList();
        }
    }

    /// <summary>
    /// Find a facet by its absolute <c>/proc/&lt;instance&gt;/&lt;facet&gt;</c> path
    /// and return its formatted current value (the producer's cat-formatter), or
    /// <c>null</c> if nothing has been published yet. Used by the AFCP provider's
    /// <c>Read</c> to send a pre-formatted snapshot over the wire without the
    /// consumer needing to deserialize the value type.
    /// </summary>
    internal string? ReadFormatted(string facetPath)
    {
        var (instance, facetName) = ParseFacetPath(facetPath);
        IFacet? raw;
        lock (_lock)
        {
            _facets.TryGetValue((instance, facetName), out raw);
        }

        return raw?.FormatForCat();
    }

    /// <summary>Find a facet by path, for the provider's type/kind metadata.</summary>
    internal IFacet? FindFacet(string facetPath)
    {
        var (instance, facetName) = ParseFacetPath(facetPath);
        lock (_lock)
        {
            _facets.TryGetValue((instance, facetName), out var raw);
            return raw;
        }
    }

    private static void RejectSlash(string name, string what)
    {
        if (name.Contains('/'))
        {
            throw new InvalidOperationException($"{what} '{name}' cannot contain '/'.");
        }
    }

    /// <summary>
    /// <c>/proc/&lt;instance&gt;/&lt;facet&gt;</c> → (instance, facet). The facet
    /// is the LAST segment; the instance is everything before it (instance names
    /// may be composite, e.g. <c>usb/device0</c>).
    /// </summary>
    internal static (string instance, string facet) ParseFacetPath(string facetPath)
    {
        var v = facetPath.Trim();
        if (v.StartsWith("/proc/", StringComparison.OrdinalIgnoreCase))
        {
            v = v["/proc/".Length..];
        }
        else if (v.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Data facets live under /proc; '{facetPath}' is not a valid facet path.");
        }

        v = v.TrimEnd('/');

        var lastSlash = v.LastIndexOf('/');
        if (lastSlash < 0)
        {
            throw new InvalidOperationException(
                $"Facet path '{facetPath}' must include an instance and a facet (e.g. /proc/lidar/scan_data).");
        }

        var instance = v[..lastSlash];
        var facet = v[(lastSlash + 1)..];
        if (instance.Length == 0 || facet.Length == 0)
        {
            throw new InvalidOperationException($"Invalid facet path '{facetPath}'.");
        }

        return (instance, facet);
    }
}

/// <summary>Non-generic facet view for the registry and <c>/proc</c> rendering.</summary>
internal interface IFacet
{
    string InstanceName { get; }
    string FacetName { get; }
    FacetKind Kind { get; }
    Type ValueType { get; }

    /// <summary>Formatted current value for <c>cat</c>, or <c>null</c> if nothing published yet.</summary>
    string? FormatForCat();

    /// <summary>
    /// Non-generic subscribe hook for callers that don't know the value type at
    /// compile time (the AFCP serve side). The handler receives the boxed value,
    /// plus the same <c>Sequence</c> and <c>InterFrameDelta</c> the typed
    /// <see cref="DataEvent{T}"/> carries. Bridges to the typed
    /// <c>Subscribe</c> — full breaker/threading/ProducerKilled semantics intact.
    /// </summary>
    ISubscription SubscribeRaw(
        Func<object, long, long?, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected);

    /// <summary>Fire <see cref="DisconnectReason.ProducerKilled"/> to every subscriber.</summary>
    void NotifyProducerKilled();
}

internal sealed record FacetInfo(string InstanceName, string FacetName, Func<string?> FormatForCat, Type ValueType, FacetKind Kind);
