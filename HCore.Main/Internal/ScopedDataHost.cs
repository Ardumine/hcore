using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// The per-module data-plane facade injected as a module's
/// <see cref="BaseImplement.Data"/>. Binds the owner's own instance name at
/// creation time, so <see cref="IDataHost.ExposeData{T}"/> registers a facet
/// under THIS instance (the module cannot forge another producer's path).
/// <see cref="IDataHost.ReadData{T}"/> / <see cref="IDataHost.Subscribe{T}"/>
/// are path-based and pass straight through to the kernel <see cref="DataHost"/>.
///
/// Mirrors <see cref="ScopedModuleHost"/>, which binds the owner for child ops.
/// </summary>
internal sealed class ScopedDataHost : IDataHost
{
    private readonly DataHost _kernel;
    private readonly string _owner;

    public ScopedDataHost(DataHost kernel, string owner)
    {
        _kernel = kernel;
        _owner = owner;
    }

    public IExposedData<T> ExposeData<T>(
        string facetName,
        FacetKind kind,
        DispatchPolicy policy = DispatchPolicy.Default,
        int bound = -1,
        Func<T, string>? formatter = null) where T : class
        => _kernel.ExposeData(_owner, facetName, kind, policy, bound, formatter);

    public T? ReadData<T>(string facetPath) where T : class
        => _kernel.ReadData<T>(facetPath);

    public ISubscription Subscribe<T>(
        string facetPath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected = null) where T : class
        => _kernel.Subscribe(facetPath, handler, onDisconnected);
}
