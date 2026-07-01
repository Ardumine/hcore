namespace HCore.Modules.Base;

/// <summary>
/// One delivered frame, carrying the per-facet sequence number and the
/// publish-to-publish duration (DATA_PLANE_DESIGN.md Part VIII).
/// </summary>
/// <typeparam name="T">The frame type. Must live in a shared contract assembly
/// (e.g. <c>HCore.Modules.Base</c>) for cross-package subscribers — different
/// AssemblyLoadContexts see different <c>Type</c> objects.</typeparam>
public readonly struct DataEvent<T> where T : class
{
    /// <summary>The frame payload (zero-copy reference; treat as immutable).</summary>
    public T Data { get; init; }

    /// <summary>
    /// PER-FACET firing count (gap detection). A subscriber to
    /// <c>/proc/lidar/scan_data</c> sees a contiguous sequence on THAT stream;
    /// another facet on the same producer has its own independent counter.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Publish-to-publish duration in <see cref="System.Diagnostics.Stopwatch"/>
    /// ticks (a portable DURATION, not an absolute time). <c>null</c> on the very
    /// first frame. Earns its place as the rate-mismatch diagnostic: when a
    /// consumer is backed up and draining a queue, its measured inter-arrival
    /// times reflect its drain rate, not the publish rate — so this is the one
    /// signal that reveals sustained producer/consumer mismatch.
    /// </summary>
    public long? InterFrameDelta { get; init; }
}
