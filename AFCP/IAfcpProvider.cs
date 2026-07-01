using AFCP.Protocol;

namespace AFCP;

/// <summary>
/// The server-side contract a host (the future HCore bridge module, or any other
/// AFCP consumer) implements to back an <see cref="AfcpServer"/>. The server
/// dispatches incoming AFCP requests to this provider and forwards the results
/// over the wire.
///
/// For subscriptions, the provider receives an <see cref="IAfcpSubscriptionSink"/>
/// and MUST push <see cref="EventNotify"/> frames through it as new data arrives,
/// and call <see cref="IAfcpSubscriptionSink.ProducerGone"/> when the producing
/// facet disappears. This is how live <c>Subscribe</c> push flows over the wire.
/// </summary>
public interface IAfcpProvider
{
    ConnectResponse Connect(ConnectRequest request);
    SyncResponse Sync(SyncRequest request);
    ReadResponse Read(ReadRequest request);
    SubscribeResponse Subscribe(SubscribeRequest request, IAfcpSubscriptionSink sink);
    void Unsubscribe(ulong subscriptionId);
}

/// <summary>
/// The handle a <see cref="IAfcpProvider"/> gets for an active subscription so it
/// can push data frames and lifecycle notifies back to the remote subscriber. The
/// <see cref="AfcpServer"/> implements this; each call maps to one Notify frame.
/// </summary>
public interface IAfcpSubscriptionSink
{
    /// <summary>Push a data frame to the subscriber.</summary>
    void Push(EventNotify evt);

    /// <summary>Signal that the producing facet is gone (killed/disconnected).</summary>
    void ProducerGone(ulong subscriptionId);

    /// <summary>Signal a subscription-level error (e.g. a remote breaker trip).</summary>
    void Error(ulong subscriptionId, string reason);
}
