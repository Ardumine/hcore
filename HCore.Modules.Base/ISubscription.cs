namespace HCore.Modules.Base;

/// <summary>
/// Handle returned by <see cref="IDataHost.Subscribe{T}"/>. The
/// <see cref="State"/> / <see cref="DisconnectReason"/> are ALWAYS observable
/// (the mandatory signal); the subscribe callback is the OPTIONAL interruption.
/// A consumer that never wired <c>onDisconnected</c> still discovers a trip on
/// its next interaction by polling <see cref="State"/> — no silent footgun.
/// </summary>
public interface ISubscription : IDisposable
{
    /// <summary>Always pullable. <see cref="SubscriptionState.Tripped"/> = the breaker fired.</summary>
    SubscriptionState State { get; }

    /// <summary>Why it tripped, or <c>null</c> while <see cref="SubscriptionState.Active"/>.</summary>
    DisconnectReason? DisconnectReason { get; }

    /// <summary>
    /// Frames skipped because the handler THREW (stream tolerate-and-continue).
    /// Kernel overflow drops are NOT counted here — they are observable as gaps
    /// in <see cref="DataEvent{T}.Sequence"/>.
    /// </summary>
    long ConsumerSkippedCount { get; }
}

public enum SubscriptionState
{
    /// <summary>Receiving frames.</summary>
    Active,

    /// <summary>Breaker tripped (overload / handler failure / producer killed). Dead; re-subscribe to resume.</summary>
    Tripped,

    /// <summary>Consumer disposed the handle.</summary>
    Disposed,
}

/// <summary>
/// Why a subscription disconnected (DATA_PLANE_DESIGN.md Part VII). Three trip
/// causes funnel into the same disconnect path, distinguished by this type so
/// the consumer can tell them apart.
/// </summary>
public enum DisconnectReason
{
    /// <summary>Sustained queue overflow (the original breaker case; stream facets only).</summary>
    Overload,

    /// <summary>Handler threw (cell: one-strike; stream: sustained throws).</summary>
    HandlerException,

    /// <summary>The producing instance was reaped (cascade kill / direct kill).</summary>
    ProducerKilled,

    /// <summary>The consumer disposed the subscription.</summary>
    Disposed,
}
