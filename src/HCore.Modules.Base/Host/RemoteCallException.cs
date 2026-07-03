namespace HCore.Modules.Base;

/// <summary>
/// Thrown by a remote module-interface proxy (Layer 3 — MKCall) when a call
/// fails on the serving peer: the instance is gone, the method could not be
/// resolved, or the invoked method threw. The original exception type is not
/// reconstructed — <see cref="Exception.Message"/> carries the server-side
/// <c>"Type.FullName: Message"</c> string (typed wire errors are TODO.md §C7d).
///
/// A caller that wants to tolerate remote failures catches this; the local
/// direct-dispatch path never throws it (a proxy is returned only when the
/// instance path resolves to a remote mount).
/// </summary>
public class RemoteCallException : Exception
{
    public RemoteCallException(string message) : base(message) { }
    public RemoteCallException(string message, Exception innerException) : base(message, innerException) { }
}
