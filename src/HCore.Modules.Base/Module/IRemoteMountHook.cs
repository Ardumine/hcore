using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace HCore.Modules.Base;

/// <summary>
/// Extension point registered by a remote-mount driver module (e.g. the AFCP
/// Nexus connector) so the kernel can transparently redirect subscribe/call
/// operations to the peer without naming any remote-VFS types.
///
/// <para><b>Consulted before</b> the local <c>/proc</c> parse in both
/// <see cref="IDataHost.Subscribe{T}"/> (Layer 2 — data-plane) and
/// <see cref="IModuleHost.GetModuleInterface{T}"/> (Layer 3 — MKCall).</para>
///
/// <para>No default implementation exists in the kernel — the field is
/// <c>null</c> until a driver module registers this hook. When <c>null</c>,
/// both paths fall through to their existing local-resolution logic.</para>
/// </summary>
public interface IRemoteMountHook
{
    /// <summary>
    /// Called by <see cref="IDataHost.Subscribe{T}"/> before the local facet
    /// lookup. Returns <c>null</c> if <paramref name="facetPath"/> does not
    /// resolve to a remote mount; otherwise returns a live subscription
    /// backed by the remote peer.
    /// </summary>
    ISubscription? TrySubscribeRemote<T>(
        string facetPath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class;

    /// <summary>
    /// Called by <see cref="IModuleHost.GetModuleInterface{T}"/> before the
    /// local instance lookup. Returns <c>null</c> if <paramref name="instancePath"/>
    /// does not resolve to a remote mount; otherwise returns a transparent
    /// MKCall proxy backed by the remote peer.
    /// </summary>
    [return: MaybeNull]
    T TryGetRemoteInterface<T>(string instancePath) where T : class, IModule;
}
