using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HCore.Modules.Base;

namespace HCore.Main.Vfs;

public sealed class HostFileSystem : IVirtualFileSystem
{
    public HostFileSystem(string rootPath, string? name = null, bool isReadOnly = false)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        Name = string.IsNullOrWhiteSpace(name) ? $"hostfs:{RootPath}" : name;
        IsReadOnly = isReadOnly;
        Root = new HostDirectory("/", null, RootPath, IsReadOnly);
    }

    public string RootPath { get; }
    public string Name { get; }
    public bool IsReadOnly { get; }
    public IVirtualDirectory Root { get; }
}

internal sealed class HostDirectory : VirtualNode, IVirtualDirectory
{
    private readonly string _physicalPath;
    private readonly bool _isReadOnly;
    private readonly object _syncRoot = new();

    internal HostDirectory(string name, IVirtualDirectory? parent, string physicalPath, bool isReadOnly)
        : base(name, parent)
    {
        _physicalPath = physicalPath;
        _isReadOnly = isReadOnly;
    }

    public IEnumerable<IVirtualNode> Enumerate()
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_physicalPath))
            {
                return Array.Empty<IVirtualNode>();
            }

            var directories = Directory.EnumerateDirectories(_physicalPath)
                .Select(path => (IVirtualNode)new HostDirectory(System.IO.Path.GetFileName(path), this, path, _isReadOnly));
            var files = Directory.EnumerateFiles(_physicalPath)
                .Select(path => (IVirtualNode)new HostFile(System.IO.Path.GetFileName(path), this, path, _isReadOnly));
            return directories.Concat(files).ToArray();
        }
    }

    public IEnumerable<IVirtualNode> EnumerateDirectories()
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_physicalPath))
            {
                return Array.Empty<IVirtualNode>();
            }

            return Directory.EnumerateDirectories(_physicalPath)
                .Select(path => (IVirtualNode)new HostDirectory(System.IO.Path.GetFileName(path), this, path, _isReadOnly))
                .ToArray();
        }
    }

    public IEnumerable<IVirtualNode> EnumerateFiles()
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_physicalPath))
            {
                return Array.Empty<IVirtualNode>();
            }

            return Directory.EnumerateFiles(_physicalPath)
                .Select(path => (IVirtualNode)new HostFile(System.IO.Path.GetFileName(path), this, path, _isReadOnly))
                .ToArray();
        }
    }

    public IVirtualDirectory? TryGetDirectory(string name)
    {
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);
        return Directory.Exists(path) ? new HostDirectory(safeName, this, path, _isReadOnly) : null;
    }

    public IVirtualDirectory GetDirectory(string name)
    {
        return TryGetDirectory(name) ?? throw new DirectoryNotFoundException($"Directory '{name}' not found in '{Path}'.");
    }

    public IVirtualFile? TryGetFile(string name)
    {
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);
        return File.Exists(path) ? new HostFile(safeName, this, path, _isReadOnly) : null;
    }

    public IVirtualFile GetFile(string name)
    {
        return TryGetFile(name) ?? throw new FileNotFoundException($"File '{name}' not found in '{Path}'.");
    }

    public IVirtualNode? TryGet(string name)
    {
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);

        if (Directory.Exists(path))
        {
            return new HostDirectory(safeName, this, path, _isReadOnly);
        }

        if (File.Exists(path))
        {
            return new HostFile(safeName, this, path, _isReadOnly);
        }

        return null;
    }

    public IVirtualDirectory CreateDirectory(string name)
    {
        EnsureWritable();
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);

        if (File.Exists(path))
        {
            throw new IOException($"Cannot create directory '{name}' because a file with the same name exists.");
        }

        Directory.CreateDirectory(path);
        return new HostDirectory(safeName, this, path, _isReadOnly);
    }

    public bool TryDelete(string name)
    {
        EnsureWritable();
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);

        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        Directory.Delete(path);
        return true;
    }

    public IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default)
    {
        EnsureWritable();
        var safeName = ValidateSingleSegment(name);
        var path = CombineSafe(_physicalPath, safeName);

        if (Directory.Exists(path))
        {
            throw new IOException($"Cannot create file '{name}' because a directory with the same name exists.");
        }

        if (File.Exists(path) && !overwrite)
        {
            throw new IOException($"File '{name}' already exists.");
        }

        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        if (!initialData.IsEmpty)
        {
            stream.Write(initialData);
        }

        return new HostFile(safeName, this, path, _isReadOnly);
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("Filesystem is read-only.");
        }
    }

    private static string ValidateSingleSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        }

        if (name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
        {
            throw new ArgumentException($"Invalid path segment '{name}'.", nameof(name));
        }

        return name;
    }

    private static string CombineSafe(string basePath, string childName)
    {
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, childName));
        var root = EnsureTrailingSeparator(System.IO.Path.GetFullPath(basePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Path traversal outside filesystem root is not allowed.");
        }

        return candidate;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? path
            : path + System.IO.Path.DirectorySeparatorChar;
    }
}

internal sealed class HostFile : VirtualNode, IVirtualFile
{
    private readonly string _physicalPath;
    private readonly bool _isReadOnly;

    internal HostFile(string name, IVirtualDirectory parent, string physicalPath, bool isReadOnly)
        : base(name, parent)
    {
        _physicalPath = physicalPath;
        _isReadOnly = isReadOnly;
    }

    public Stream GetStream(FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite)
    {
        if (_isReadOnly && (access & FileAccess.Write) != 0)
        {
            throw new InvalidOperationException("Filesystem is read-only.");
        }

        if (!File.Exists(_physicalPath) && mode == FileMode.Open)
        {
            throw new FileNotFoundException($"File '{Path}' not found.");
        }

        if (mode == FileMode.CreateNew && File.Exists(_physicalPath))
        {
            throw new IOException("File already exists.");
        }

        var share = access == FileAccess.Read ? FileShare.ReadWrite : FileShare.Read;
        return new FileStream(_physicalPath, mode, access, share);
    }

    public byte[] ReadAllBytes()
    {
        if (!File.Exists(_physicalPath))
        {
            throw new FileNotFoundException($"File '{Path}' not found.");
        }

        return File.ReadAllBytes(_physicalPath);
    }

    public string ReadString(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(ReadAllBytes());
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("Filesystem is read-only.");
        }

        using var stream = new FileStream(_physicalPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(data);
    }
}