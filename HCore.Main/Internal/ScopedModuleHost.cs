using HCore.Modules.Base;

namespace HCore.Main.Internal;

/// <summary>
/// The per-module facade injected as a module's <see cref="BaseImplement.Host"/>.
/// Binds the owner's own instance name at creation time, so a module can only
/// ever spawn/kill children under ITSELF — it has no way to forge or squat
/// another parent's subtree. This is what every created instance actually
/// receives (see <c>ModuleHost.Create</c>); the raw kernel <see cref="ModuleHost"/>
/// is never handed to a module directly.
/// </summary>
internal sealed class ScopedModuleHost : IModuleHost
{
    private readonly ModuleHost _kernel;
    private readonly string _owner;

    public ScopedModuleHost(ModuleHost kernel, string owner)
    {
        _kernel = kernel;
        _owner = owner;
    }

    public T GetModuleInterface<T>(string instancePath) where T : IModule
        => _kernel.GetModuleInterface<T>(instancePath);

    public T Spawn<T>(string moduleName, string instanceName) where T : IModule
        => _kernel.Spawn<T>(moduleName, instanceName);

    public TImpl SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule
        => _kernel.SpawnChildByType(_owner, leafName, init);

    public T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule
        => _kernel.SpawnChildByName(_owner, moduleName, leafName, init);

    public void KillChild(string leafName) => _kernel.KillChildCore(_owner, leafName);

    public void Kill(string instancePath) => _kernel.Kill(instancePath);
}
