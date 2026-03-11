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