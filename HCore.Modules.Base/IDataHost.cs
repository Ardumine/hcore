namespace HCore.Modules.Base;

/// <summary>
/// The data-plane "system call" surface a module uses to EXPOSE its own data
/// facets and to READ/SUBSCRIBE to other modules' facets. It is implemented in
/// kernel space (HCore.Main) and injected into each module on creation as
/// <see cref="BaseImplement.Data"/> — exactly like <see cref="IModuleFileSystem"/>
/// is the file-call door and <see cref="IModuleHost"/> is the process/IPC door.
///
/// One injected handle per concern (DATA_PLANE_DECISIONS.md B1): data lives
/// behind its own capability, not bolted onto <see cref="IModuleHost"/>.
///
/// A facet is addressed as <c>/proc/&lt;instance&gt;/&lt;facetName&gt;</c>. The
/// producer exposes a facet under its OWN instance (the owner is implicit via
/// the injected <see cref="ScopedDataHost"/>); a consumer reaches any facet by
/// path through <see cref="ReadData{T}"/> (snapshot) or <see cref="Subscribe{T}"/>
/// (push). Same address, different verb.
/// </summary>
public interface IDataHost
{
    /// <summary>
    /// Register a data facet under THIS module's instance at
    /// <c>/proc/&lt;me&gt;/&lt;facetName&gt;</c> and return a handle the module
    /// pushes frames to. The facet name cannot contain '/'.
    /// </summary>
    /// <param name="facetName">Leaf name of the facet (no '/').</param>
    /// <param name="kind">Cell (latest-value, coalesce) or Stream (ordered queue).</param>
    /// <param name="policy">Dispatch policy; <see cref="DispatchPolicy.Default"/>
    /// infers from <paramref name="kind"/> (Cell→Coalesce, Stream→OrderedQueue).</param>
    /// <param name="bound">Per-subscriber queue bound. <c>-1</c>/0 = per-kind default
    /// (Cell=1, Stream=64). Ignored for <see cref="DispatchPolicy.WaitForAll"/>.</param>
    /// <param name="formatter">Optional <c>cat</c> formatter (defaults to
    /// <see cref="object.ToString"/>). The producer owns its inspection rendering.</param>
    IExposedData<T> ExposeData<T>(
        string facetName,
        FacetKind kind,
        DispatchPolicy policy = DispatchPolicy.Default,
        int bound = -1,
        Func<T, string>? formatter = null) where T : class;

    /// <summary>
    /// One-shot SNAPSHOT of a facet's most-recent published value (the "cat" path).
    /// Non-draining: it peeks at the latest value, never consumes from a queue.
    /// Returns <c>null</c> if no value has been published yet. Throws if no facet
    /// exists at <paramref name="facetPath"/> or the facet's type is not
    /// <typeparamref name="T"/>.
    /// </summary>
    T? ReadData<T>(string facetPath) where T : class;

    /// <summary>
    /// Subscribe to a facet's push stream. Returns a handle whose
    /// <see cref="ISubscription.State"/> is always observable (the mandatory
    /// signal); <paramref name="onDisconnected"/> is the optional interruption.
    /// </summary>
    /// <param name="facetPath">e.g. <c>"/proc/lidar/scan_data"</c>.</param>
    /// <param name="handler">Invoked per frame on a thread-pool worker. Receives
    /// the per-subscriber cancellation token (tripped on dispose/disconnect).</param>
    /// <param name="onDisconnected">Optional callback fired on
    /// <see cref="DisconnectReason"/> (breaker trip / handler failure / producer
    /// killed). The signal is mandatory regardless: poll
    /// <see cref="ISubscription.State"/>.</param>
    ISubscription Subscribe<T>(
        string facetPath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected = null) where T : class;

    /// <summary>
    /// Find a facet by its absolute <c>/proc/&lt;instance&gt;/&lt;facet&gt;</c> path.
    /// Returns <c>null</c> if no facet exists at that path. Used by the serve-side
    /// AFCP provider and the <c>/proc</c> file system for facet listing.
    /// </summary>
    IFacet? FindFacet(string facetPath);
}

/// <summary>
/// The primitive a facet exposes. Determines the default dispatch policy AND the
/// handler-exception policy (DATA_PLANE_DESIGN.md Parts III–V).
/// </summary>
public enum FacetKind
{
    /// <summary>
    /// Latest-value: holds the current value; subscribe = on-change. Default
    /// dispatch = coalesce-to-newest (drop intermediates). Handler exception =
    /// one-strike-and-out.
    /// </summary>
    Cell,

    /// <summary>
    /// Ordered sequence: don't drop frames. Default dispatch = bounded ordered
    /// queue, drop-oldest on overflow. Handler exception = tolerate-and-continue,
    /// trip only on sustained throws.
    /// </summary>
    Stream,
}

/// <summary>
/// How <see cref="IExposedData{T}"/> fans a published frame out to subscribers
/// (DATA_PLANE_DESIGN.md Part V).
/// </summary>
public enum DispatchPolicy
{
    /// <summary>Infer from <see cref="FacetKind"/> (Cell→<see cref="Coalesce"/>, Stream→<see cref="OrderedQueue"/>).</summary>
    Default,

    /// <summary>Option 1 — blocking backpressure: <c>Publish</c> waits for every handler. Opt-in, never default.</summary>
    WaitForAll,

    /// <summary>Option 2 — fire-and-forget, coalesce to newest (cell). Default for <see cref="FacetKind.Cell"/>.</summary>
    Coalesce,

    /// <summary>Option 3 — fire-and-forget, ordered bounded queue, drop-oldest (stream). Default for <see cref="FacetKind.Stream"/>.</summary>
    OrderedQueue,

    /// <summary>Option 4 — fire-and-forget, parallel unordered, bounded (independent items).</summary>
    ParallelUnordered,
}

internal sealed class EmptyDataHost : IDataHost
{
    public static EmptyDataHost Instance { get; } = new();

    public IExposedData<T> ExposeData<T>(string facetName, FacetKind kind, DispatchPolicy policy = DispatchPolicy.Default, int bound = -1, Func<T, string>? formatter = null) where T : class
        => throw NotAttached();

    public T? ReadData<T>(string facetPath) where T : class
        => throw NotAttached();

    public ISubscription Subscribe<T>(string facetPath, Func<DataEvent<T>, CancellationToken, ValueTask> handler, Action<DisconnectReason>? onDisconnected = null) where T : class
        => throw NotAttached();

    public IFacet? FindFacet(string facetPath) => throw NotAttached();

    private static InvalidOperationException NotAttached() => new("Data host is not attached.");
}
