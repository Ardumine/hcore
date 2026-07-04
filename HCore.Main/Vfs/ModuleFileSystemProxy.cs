using HCore.Modules.Base;
using System.Text;

namespace HCore.Main.Vfs;

public sealed class ModuleFileSystemProxy : IModuleFileSystem
{
    private readonly FileSystem _fileSystem;
    private readonly object _sync;

    public string WorkingDirectory { get; private set; }

    public ModuleFileSystemProxy(FileSystem fileSystem, object sync, string initialWorkingDirectory = "/")
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        WorkingDirectory = "/";
        SetWorkingDirectory(initialWorkingDirectory);
    }

    public string ResolvePath(string path)
    {
        lock (_sync)
        {
            return PathHelpers.NormalizeAbsolute(path, WorkingDirectory);
        }
    }

    public void SetWorkingDirectory(string path)
    {
        lock (_sync)
        {
            var target = PathHelpers.NormalizeAbsolute(path, WorkingDirectory);
            _fileSystem.GetDirectory(target, WorkingDirectory);
            WorkingDirectory = target;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_sync)
        {
            _fileSystem.MkDir(path, WorkingDirectory);
        }
    }

    public Stream OpenFileStream(string path, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite)
    {
        lock (_sync)
        {
            return _fileSystem.OpenFileStream(path, mode, access, WorkingDirectory);
        }
    }

    public bool DeleteFile(string path)
    {
        lock (_sync)
        {
            return _fileSystem.DeleteFile(path, WorkingDirectory);
        }
    }

    public bool DeleteDirectory(string path, bool recursive = false)
    {
        lock (_sync)
        {
            var targetAbsolute = PathHelpers.NormalizeAbsolute(path, WorkingDirectory);
            var segments = PathHelpers.NormalizeSegments(targetAbsolute);
            if (segments.Length == 0)
            {
                return false;
            }

            var parentPath = segments.Length == 1 ? "/" : "/" + string.Join('/', segments[..^1]);
            var nodeName = segments[^1];
            var parent = _fileSystem.GetDirectory(parentPath, WorkingDirectory);
            var target = parent.TryGet(nodeName) as IVirtualDirectory;
            if (target is null)
            {
                return false;
            }

            if (!recursive && target.Enumerate().Any())
            {
                return false;
            }

            if (recursive)
            {
                DeleteDirectoryContents(target);
            }

            return parent.TryDelete(nodeName);
        }
    }

    public bool Exists(string path)
    {
        lock (_sync)
        {
            return _fileSystem.Exists(path, WorkingDirectory);
        }
    }

    public bool Copy(string sourcePath, string destinationPath, bool overwrite = false)
    {
        lock (_sync)
        {
            return _fileSystem.Copy(sourcePath, destinationPath, overwrite, WorkingDirectory);
        }
    }

    public bool Move(string sourcePath, string destinationPath, bool overwrite = false)
    {
        lock (_sync)
        {
            return _fileSystem.Move(sourcePath, destinationPath, overwrite, WorkingDirectory);
        }
    }

    public bool Rename(string path, string newName, bool overwrite = false)
    {
        lock (_sync)
        {
            return _fileSystem.Rename(path, newName, overwrite, WorkingDirectory);
        }
    }

    public bool FileExists(string path)
    {
        lock (_sync)
        {
            try
            {
                _fileSystem.GetFile(path, WorkingDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool DirectoryExists(string path)
    {
        lock (_sync)
        {
            try
            {
                _fileSystem.GetDirectory(path, WorkingDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public IEnumerable<string> ListDirectory(string path = ".")
    {
        lock (_sync)
        {
            return _fileSystem.ListDirectory(path, WorkingDirectory)
                .ToArray()
                .AsEnumerable();
        }
    }

    public string ReadAllText(string path)
    {
        lock (_sync)
        {
            using var stream = _fileSystem.OpenFileStream(path, FileMode.Open, FileAccess.Read, WorkingDirectory);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            return reader.ReadToEnd();
        }
    }

    public void WriteAllText(string path, string contents, bool append = false)
    {
        lock (_sync)
        {
            var fileMode = append ? FileMode.Append : FileMode.Create;
            using var stream = _fileSystem.OpenFileStream(path, fileMode, FileAccess.Write, WorkingDirectory);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(contents);
        }
    }

    public void TouchFile(string path)
    {
        lock (_sync)
        {
            using var stream = _fileSystem.OpenFileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, WorkingDirectory);
        }
    }

    private static void DeleteDirectoryContents(IVirtualDirectory directory)
    {
        foreach (var node in directory.Enumerate().ToArray())
        {
            if (node is IVirtualDirectory childDirectory)
            {
                DeleteDirectoryContents(childDirectory);
            }

            directory.TryDelete(node.Name);
        }
    }

}
