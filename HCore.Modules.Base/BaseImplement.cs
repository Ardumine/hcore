namespace HCore.Modules.Base;

/// <summary>
/// The code to implement the functions for the module.
/// </summary>
public abstract class BaseImplement : IModule
{
	public IModuleFileSystem Vfs { get; private set; } = EmptyModuleFileSystem.Instance;

	public void AttachVfs(IModuleFileSystem vfs)
	{
		Vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
	}

	/// <summary>
	/// The kernel surface this module uses to reach OTHER modules.
	/// Attached by the kernel at creation, exactly like <see cref="Vfs"/>.
	/// </summary>
	public IModuleHost Host { get; private set; } = EmptyModuleHost.Instance;

	public void AttachHost(IModuleHost host)
	{
		Host = host ?? throw new ArgumentNullException(nameof(host));
	}

	/// <summary>
	/// This instance's own /proc identity (e.g. "usb/device0"). Kernel-injected
	/// at creation, exactly like <see cref="Vfs"/>/<see cref="Host"/>.
	/// </summary>
	public string InstanceName { get; private set; } = "";

	public void AttachInstanceName(string name)
	{
		InstanceName = name ?? throw new ArgumentNullException(nameof(name));
	}

	/// <summary>
	/// Structured logger injected by the kernel, like <see cref="Vfs"/> and
	/// <see cref="Host"/>. Defaults to a no-op logger (never crashes if
	/// unwired, but produces no output in that case).
	/// </summary>
	public IModuleLogger Logger { get; private set; } = EmptyModuleLogger.Instance;

	public void AttachLogger(IModuleLogger logger)
	{
		Logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// The data-plane "system call" surface a module uses to expose its own data
	/// facets and to read/subscribe to other modules' facets. Attached by the
	/// kernel at creation, exactly like <see cref="Vfs"/>/<see cref="Host"/>.
	/// </summary>
	public IDataHost Data { get; private set; } = EmptyDataHost.Instance;

	public void AttachData(IDataHost data)
	{
		Data = data ?? throw new ArgumentNullException(nameof(data));
	}

	/// <summary>
	/// Called by the kernel when this instance is reaped (killed directly, or as
	/// part of a parent's cascade). Override to release resources; default is a
	/// no-op. Runs outside the kernel's process-table lock.
	/// </summary>
	protected internal virtual void OnKilled()
	{
	}

	/// <summary>
	/// Module-authored extra lines shown in this instance's <c>/proc/&lt;name&gt;/info</c>
	/// (e.g. a USB device's serial + location). Default is none.
	/// </summary>
	protected internal virtual string? DescribeForProc() => null;
}

internal sealed class EmptyModuleFileSystem : IModuleFileSystem
{
	public static EmptyModuleFileSystem Instance { get; } = new();

	public string WorkingDirectory => "/";

	public string ResolvePath(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public void SetWorkingDirectory(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public void CreateDirectory(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public Stream OpenFileStream(string path, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite)
		=> throw new InvalidOperationException("Module VFS is not attached.");

	public bool DeleteFile(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool DeleteDirectory(string path, bool recursive = false) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool FileExists(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool DirectoryExists(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public IEnumerable<string> ListDirectory(string path = ".") => throw new InvalidOperationException("Module VFS is not attached.");

	public string ReadAllText(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public void WriteAllText(string path, string contents, bool append = false) => throw new InvalidOperationException("Module VFS is not attached.");

	public void TouchFile(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool Exists(string path) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool Move(string sourcePath, string destinationPath, bool overwrite = false) => throw new InvalidOperationException("Module VFS is not attached.");

	public bool Rename(string path, string newName, bool overwrite = false) => throw new InvalidOperationException("Module VFS is not attached.");
}

internal sealed class EmptyModuleHost : IModuleHost
{
	public static EmptyModuleHost Instance { get; } = new();

	public T GetModuleInterface<T>(string instancePath) where T : class, IModule => throw NotAttached();

	public T Spawn<T>(string moduleName, string instanceName) where T : IModule => throw NotAttached();

	public TImpl SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule => throw NotAttached();

	public T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule => throw NotAttached();

	public void KillChild(string leafName) => throw NotAttached();

	public void Kill(string instancePath) => throw NotAttached();

	public bool TryResolveInstance(string instancePath, out IModule instance) => throw NotAttached();

	private static InvalidOperationException NotAttached() => new("Module host is not attached.");
}

internal sealed class EmptyModuleLogger : IModuleLogger
{
	public static EmptyModuleLogger Instance { get; } = new();

	public string Description => "";

	public void I(string message) { }
	public void W(string message) { }
	public void E(string message) { }
}