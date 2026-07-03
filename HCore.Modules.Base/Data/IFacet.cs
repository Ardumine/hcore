using System;
using System.Threading;
using System.Threading.Tasks;

namespace HCore.Modules.Base;

/// <summary>
/// Non-generic facet view. Exposes the value type, kind, a formatted snapshot
/// for <c>cat</c>, and a non-generic <see cref="SubscribeRaw"/> for callers
/// that don't know the value type at compile time (the AFCP serve side).
/// </summary>
public interface IFacet
{
    string InstanceName { get; }
    string FacetName { get; }
    FacetKind Kind { get; }
    Type ValueType { get; }

    string? FormatForCat();

    ISubscription SubscribeRaw(
        Func<object, long, long?, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected);
}
