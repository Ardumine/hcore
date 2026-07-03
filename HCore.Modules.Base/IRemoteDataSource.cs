namespace HCore.Modules.Base;

/// <summary>
/// Implemented by a remote VFS mount that can back a data-plane subscription.
/// <see cref="HCore.Modules.Base.IDataHost"/> checks for this after resolving a
/// mount to transparently redirect a <c>Subscribe&lt;T&gt;</c> to the remote peer.
/// </summary>
public interface IRemoteDataSource
{
    /// <summary>
    /// Subscribe to a facet on the remote peer. <paramref name="remotePath"/> is
    /// the path as the peer sees it (mount prefix already stripped).
    /// </summary>
    ISubscription SubscribeData<T>(
        string remotePath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class;
}

/// <summary>
/// Implemented by a remote VFS mount that can proxy inter-module calls to the
/// peer. <see cref="IModuleHost"/> checks for this after resolving a mount to
/// return a transparent MKCall proxy instead of a local instance.
/// </summary>
public interface IRemoteCallProvider
{
    /// <summary>
    /// Create a <see cref="DispatchProxy"/> for the module interface
    /// <typeparamref name="T"/> that marshals every method invocation to the
    /// remote instance at <paramref name="remotePath"/>.
    /// </summary>
    T CreateProxy<T>(string remotePath) where T : class, IModule;
}
