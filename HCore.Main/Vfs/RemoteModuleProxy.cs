using AFCP;
using AFCP.Protocol;
using HCore.Modules.Base;
using System.Collections.Concurrent;
using System.Reflection;

namespace HCore.Main.Vfs;

/// <summary>
/// A <see cref="DispatchProxy"/> that makes a remote module-interface call look
/// local. Returned by <c>ModuleHost.GetModuleInterface&lt;T&gt;</c> when the path
/// resolves to a remote AFCP mount (Layer 3 — MKCall). Each method invocation is
/// marshalled into a <see cref="CallRequest"/> (method name + parameter type
/// names + polymorphic <c>object[]</c> args), round-tripped over AFCP, and the
/// <see cref="CallResponse.ReturnValue"/> is handed back to the caller.
///
/// V3 deliberately proxies ONLY remote paths: a local <c>GetModuleInterface</c>
/// still returns the actual <c>BaseImplement</c>-derived object (zero-overhead
/// direct dispatch). V2 proxied everything for location transparency; V3 dropped
/// that (see docs/comparison/V2_V3_COMPARISON.md §5).
///
/// Method identification is by NAME + parameter-type full names (overload-safe,
/// stateless, no shared method-index contract — V2's <c>uint</c> index was
/// fragile). Per-<see cref="MethodInfo"/> signatures are cached so reflection
/// happens once per method, not per call. Invocation is synchronous (module
/// interface methods are synchronous in V3); the proxy blocks on the response
/// with <see cref="CancellationToken.None"/> (TODO.md §C7f — no mux-level
/// timeout or reconnect yet; trusted-LAN stance, same as <c>Kill</c>).
/// </summary>
internal class RemoteModuleProxy<T> : DispatchProxy where T : class, IModule
{
    private AfcpClient _client = null!;
    private string _remotePath = null!;

    // Per-T signature cache: MethodInfo -> (name, param type assembly-qualified names).
    // Static per closed generic type T, so all proxies of the same interface share it.
    private static readonly ConcurrentDictionary<MethodInfo, (string Name, string[] ParamTypes)> s_signatures = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new ArgumentNullException(nameof(targetMethod));
        }

        // DispatchProxy routes inherited object methods (ToString/Equals/GetHashCode)
        // through here too — never remote them; answer locally.
        if (targetMethod.DeclaringType == typeof(object))
        {
            return targetMethod.Name switch
            {
                "ToString" => $"{typeof(T).Name} (remote @ {_remotePath})",
                "Equals" => ReferenceEquals(this, args?.FirstOrDefault()),
                "GetHashCode" => _remotePath.GetHashCode(StringComparison.Ordinal),
                _ => null,
            };
        }

        var (name, paramTypes) = s_signatures.GetOrAdd(targetMethod, BuildSignature);

        var request = new CallRequest
        {
            InstancePath = _remotePath,
            MethodName = name,
            ParamTypeNames = paramTypes,
            Args = args ?? Array.Empty<object?>(),
        };

        CallResponse response;
        try
        {
            response = _client.CallAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not RemoteCallException)
        {
            throw new RemoteCallException($"transport error calling '{name}' on '{_remotePath}': {ex.Message}", ex);
        }

        if (!response.Success)
        {
            throw new RemoteCallException(response.Error.Length > 0
                ? response.Error
                : $"remote call to '{name}' on '{_remotePath}' failed (no error detail).");
        }

        return response.ReturnValue;
    }

    private static (string Name, string[] ParamTypes) BuildSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var names = new string[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            names[i] = parameters[i].ParameterType.AssemblyQualifiedName
                       ?? parameters[i].ParameterType.FullName
                       ?? parameters[i].ParameterType.Name;
        }
        return (method.Name, names);
    }

    /// <summary>
    /// Create a proxy for interface <typeparamref name="T"/> that marshals calls
    /// to <paramref name="remotePath"/> on the serving peer reached through
    /// <paramref name="client"/>. <paramref name="remotePath"/> is the path AS
    /// THE PEER SEES IT (mount prefix already stripped — mirrors Layer 2's
    /// <c>remotePath</c> convention).
    /// </summary>
    internal static T Create(AfcpClient client, string remotePath)
    {
        var proxy = (Create<T, RemoteModuleProxy<T>>() as RemoteModuleProxy<T>)!;
        proxy._client = client;
        proxy._remotePath = remotePath;
        return (proxy as T)!;
    }
}
