namespace HCore.Modules.Base;

/// <summary>
/// Base class for a module that owns child module instances. Hides the sub-host
/// wiring entirely — the author calls one verb; the kernel constructs the child,
/// wires its Vfs/Host/InstanceName, runs init before publish, and structurally
/// cascades the child's lifetime to this instance.
/// </summary>
public abstract class ContainerImplement : BaseImplement
{
	protected TImpl SpawnChild<TImpl>(string name, Action<TImpl>? init = null) where TImpl : IModule
		=> Host.SpawnChild(name, init);

	protected T SpawnChildByName<T>(string moduleName, string name, Action<T>? init = null) where T : IModule
		=> Host.SpawnChildByName(moduleName, name, init);

	protected void KillChild(string name) => Host.KillChild(name);
}
