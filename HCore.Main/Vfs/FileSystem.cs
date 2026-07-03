using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HCore.Modules.Base;

namespace HCore.Main.Vfs;

public sealed class FileSystem
{
    private readonly List<MountEntry> _mounts = new();

    public void Mount(string mountPoint, IVirtualFileSystem fileSystem, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new ArgumentException("Mount point cannot be empty.", nameof(mountPoint));
        }

        if (!mountPoint.StartsWith('/'))
        {
            throw new ArgumentException("Mount point must be absolute.", nameof(mountPoint));
        }

        var normalized = PathHelpers.NormalizeAbsolute(mountPoint, "/");
        var segments = PathHelpers.NormalizeSegments(normalized);
        var entry = new MountEntry(normalized, segments, fileSystem);

        var existingIndex = _mounts.FindIndex(m => string.Equals(m.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (!replaceExisting)
            {
                throw new InvalidOperationException($"Mount point '{normalized}' is already in use.");
            }

            _mounts[existingIndex] = entry;
        }
        else
        {
            _mounts.Add(entry);
        }

        _mounts.Sort((a, b) => b.Segments.Length.CompareTo(a.Segments.Length));
    }

    /// <summary>
    /// Remove a previously mounted filesystem at <paramref name="mountPoint"/>.
    /// Returns false if no mount exists at that path. Used by the AFCP bridge's
    /// <c>unmount</c> to tear down a remote tree.
    /// </summary>
    public bool Unmount(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || !mountPoint.StartsWith('/'))
        {
            return false;
        }

        var normalized = PathHelpers.NormalizeAbsolute(mountPoint, "/");
        var index = _mounts.FindIndex(m => string.Equals(m.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        var entry = _mounts[index];
        _mounts.RemoveAt(index);
        return true;
    }

    public IVirtualDirectory MkDir(string path, string workingDirectory = "/")
    {
        var (mount, _, segments) = Resolve(path, workingDirectory);
        if (mount.FileSystem.IsReadOnly)
        {
            throw new InvalidOperationException($"Filesystem '{mount.FileSystem.Name}' is read-only.");
        }

        return EnsureDirectory(mount, segments);
    }

    public IVirtualFile CreateFile(string path, byte[]? contents = null, bool overwrite = true, string workingDirectory = "/")
    {
        var (mount, _, segments) = Resolve(path, workingDirectory);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Cannot create a file at a mount root.");
        }

        if (mount.FileSystem.IsReadOnly)
        {
            throw new InvalidOperationException($"Filesystem '{mount.FileSystem.Name}' is read-only.");
        }

        var parentSegments = segments.Length == 1 ? Array.Empty<string>() : segments[..^1];
        var parent = EnsureDirectory(mount, parentSegments);
        return parent.CreateFile(segments[^1], overwrite, contents ?? Array.Empty<byte>());
    }

    public Stream OpenFileStream(string path, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite, string workingDirectory = "/")
    {
        var (mount, absolute, segments) = Resolve(path, workingDirectory);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Path '{absolute}' does not point to a file.");
        }

        if ((access & FileAccess.Write) != 0 && mount.FileSystem.IsReadOnly)
        {
            throw new InvalidOperationException($"Filesystem '{mount.FileSystem.Name}' is read-only.");
        }

        var parentSegments = segments.Length == 1 ? Array.Empty<string>() : segments[..^1];
        var fileName = segments[^1];

        var mayCreateFile = mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append;
        var parent = mayCreateFile ? EnsureDirectory(mount, parentSegments) : FindDirectory(mount, parentSegments);

        if (parent is null)
        {
            throw new DirectoryNotFoundException($"Directory for '{absolute}' not found.");
        }

        var file = parent.TryGetFile(fileName);
        if (file is null)
        {
            if (!mayCreateFile || mode == FileMode.Truncate)
            {
                throw new FileNotFoundException($"File '{absolute}' not found.");
            }

            file = parent.CreateFile(fileName, overwrite: false);
        }

        return file.GetStream(mode, access);
    }

    public bool DeleteFile(string path, string workingDirectory = "/")
    {
        var (mount, _, segments) = Resolve(path, workingDirectory);
        if (segments.Length == 0)
        {
            return false;
        }

        if (mount.FileSystem.IsReadOnly)
        {
            throw new InvalidOperationException($"Filesystem '{mount.FileSystem.Name}' is read-only.");
        }

        var parentSegments = segments.Length == 1 ? Array.Empty<string>() : segments[..^1];
        var parent = FindDirectory(mount, parentSegments);
        return parent is not null && parent.TryDelete(segments[^1]);
    }

    public bool Exists(string path, string workingDirectory = "/")
    {
        var (mount, _, segments) = Resolve(path, workingDirectory);
        if (segments.Length == 0)
        {
            return true;
        }

        var parentSegments = segments.Length == 1 ? Array.Empty<string>() : segments[..^1];
        var parent = FindDirectory(mount, parentSegments);
        if (parent is null)
        {
            return false;
        }

        return parent.TryGet(segments[^1]) is not null;
    }

    public bool Move(string sourcePath, string destinationPath, bool overwrite = false, string workingDirectory = "/")
    {
        var sourceAbsolute = PathHelpers.NormalizeAbsolute(sourcePath, workingDirectory);
        var destinationAbsolute = PathHelpers.NormalizeAbsolute(destinationPath, workingDirectory);

        if (string.Equals(sourceAbsolute, destinationAbsolute, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (sourceMount, _, sourceSegments) = Resolve(sourceAbsolute, "/");
        var (destinationMount, _, destinationSegments) = Resolve(destinationAbsolute, "/");

        if (!ReferenceEquals(sourceMount, destinationMount))
        {
            throw new IOException("Cross-filesystem move is not supported.");
        }

        if (sourceMount.FileSystem.IsReadOnly)
        {
            throw new InvalidOperationException($"Filesystem '{sourceMount.FileSystem.Name}' is read-only.");
        }

        if (sourceSegments.Length == 0)
        {
            throw new InvalidOperationException("Cannot move a mount root.");
        }

        if (destinationSegments.Length == 0)
        {
            throw new InvalidOperationException("Cannot move to a mount root.");
        }

        if (IsPathInside(destinationAbsolute, sourceAbsolute))
        {
            throw new IOException("Cannot move a directory into itself.");
        }

        var sourceParentSegments = sourceSegments.Length == 1 ? Array.Empty<string>() : sourceSegments[..^1];
        var sourceParent = FindDirectory(sourceMount, sourceParentSegments);
        if (sourceParent is null)
        {
            return false;
        }

        var sourceName = sourceSegments[^1];
        var sourceNode = sourceParent.TryGet(sourceName);
        if (sourceNode is null)
        {
            return false;
        }

        var destinationParentSegments = destinationSegments.Length == 1 ? Array.Empty<string>() : destinationSegments[..^1];
        var destinationParent = FindDirectory(destinationMount, destinationParentSegments);
        if (destinationParent is null)
        {
            throw new DirectoryNotFoundException($"Directory for '{destinationAbsolute}' not found.");
        }

        var destinationName = destinationSegments[^1];
        var destinationNode = destinationParent.TryGet(destinationName);

        if (destinationNode is not null)
        {
            if (!overwrite)
            {
                return false;
            }

            if (destinationNode is IVirtualDirectory destinationDirectory && destinationDirectory.Enumerate().Any())
            {
                throw new IOException($"Destination directory '{destinationAbsolute}' is not empty.");
            }

            destinationParent.TryDelete(destinationName);
        }

        switch (sourceNode)
        {
            case IVirtualFile sourceFile:
                destinationParent.CreateFile(destinationName, overwrite: true, sourceFile.ReadAllBytes());
                break;
            case IVirtualDirectory sourceDirectory:
                var copiedDirectory = destinationParent.CreateDirectory(destinationName);
                CopyDirectoryRecursive(sourceDirectory, copiedDirectory);
                break;
            default:
                throw new IOException("Unsupported node type.");
        }

        return sourceParent.TryDelete(sourceName);
    }

    public bool Rename(string path, string newName, bool overwrite = false, string workingDirectory = "/")
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("New name cannot be empty.", nameof(newName));
        }

        if (newName == "." || newName == ".." || newName.IndexOf('/') >= 0 || newName.IndexOf('\\') >= 0)
        {
            throw new ArgumentException($"Invalid node name '{newName}'.", nameof(newName));
        }

        var absolute = PathHelpers.NormalizeAbsolute(path, workingDirectory);
        var segments = PathHelpers.NormalizeSegments(absolute);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Cannot rename a mount root.");
        }

        var parentPath = segments.Length == 1 ? "/" : "/" + string.Join('/', segments[..^1]);
        var destinationPath = parentPath == "/" ? $"/{newName}" : $"{parentPath}/{newName}";
        return Move(absolute, destinationPath, overwrite, "/");
    }

    public IVirtualDirectory GetDirectory(string path, string workingDirectory = "/")
    {
        var (mount, absolute, segments) = Resolve(path, workingDirectory);
        var directory = FindDirectory(mount, segments);
        if (directory is null)
        {
            throw new DirectoryNotFoundException($"Directory '{absolute}' not found.");
        }

        return directory;
    }

    public IVirtualFile GetFile(string path, string workingDirectory = "/")
    {
        var (mount, absolute, segments) = Resolve(path, workingDirectory);
        if (segments.Length == 0)
        {
            throw new FileNotFoundException($"Path '{absolute}' does not point to a file.");
        }

        var parentSegments = segments.Length == 1 ? Array.Empty<string>() : segments[..^1];
        var parent = FindDirectory(mount, parentSegments) ?? throw new DirectoryNotFoundException($"Directory '{absolute}' not found.");
        return parent.GetFile(segments[^1]);
    }

    public IEnumerable<string> ListDirectory(string path = ".", string workingDirectory = "/")
    {
        var (mount, absolute, segments) = Resolve(path, workingDirectory);
        var directory = FindDirectory(mount, segments);
        if (directory is null)
        {
            throw new DirectoryNotFoundException($"Directory '{absolute}' not found.");
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in directory.Enumerate())
        {
            var formatted = entry is IVirtualDirectory ? $"{entry.Name}/" : entry.Name;
            entries[entry.Name] = formatted;
        }

        var absoluteSegments = PathHelpers.NormalizeSegments(absolute);
        foreach (var childMount in GetDirectChildMountNames(absoluteSegments))
        {
            entries[childMount] = $"{childMount}/";
        }

        return entries.Values
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IVirtualDirectory EnsureDirectory(MountEntry mount, string[] segments)
    {
        var current = mount.FileSystem.Root;
        foreach (var segment in segments)
        {
            var next = current.TryGetDirectory(segment);
            if (next is null)
            {
                if (mount.FileSystem.IsReadOnly)
                {
                    throw new InvalidOperationException($"Filesystem '{mount.FileSystem.Name}' is read-only.");
                }

                current = current.CreateDirectory(segment);
            }
            else
            {
                current = next;
            }
        }

        return current;
    }

    private IVirtualDirectory? FindDirectory(MountEntry mount, string[] segments)
    {
        var current = mount.FileSystem.Root;
        foreach (var segment in segments)
        {
            current = current.TryGetDirectory(segment);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static void CopyDirectoryRecursive(IVirtualDirectory source, IVirtualDirectory destination)
    {
        foreach (var node in source.Enumerate())
        {
            switch (node)
            {
                case IVirtualFile file:
                    destination.CreateFile(file.Name, overwrite: true, file.ReadAllBytes());
                    break;
                case IVirtualDirectory directory:
                    var child = destination.CreateDirectory(directory.Name);
                    CopyDirectoryRecursive(directory, child);
                    break;
            }
        }
    }

    private static bool IsPathInside(string path, string potentialParent)
    {
        if (string.Equals(path, potentialParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = potentialParent.EndsWith('/') ? potentialParent : potentialParent + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve <paramref name="path"/> to the <see cref="IVirtualFileSystem"/> that
    /// backs it plus the path AS THAT FILESYSTEM SEES IT (the mount prefix stripped,
    /// e.g. <c>/other/proc/lidar</c> under mount <c>/other</c> → <c>/proc/lidar</c>).
    /// Returns <c>false</c> instead of throwing when no mount covers the path. Used
    /// by <see cref="Internal.DataHost"/> to detect a remote-mounted facet path and
    /// redirect the subscribe to the peer. Absolute paths only (working dir <c>/</c>).
    /// </summary>
    internal bool TryResolveMount(string path, out IVirtualFileSystem fileSystem, out string remotePath)
    {
        try
        {
            var (mount, _, relativeSegments) = Resolve(path, "/");
            fileSystem = mount.FileSystem;
            remotePath = relativeSegments.Length == 0 ? "/" : "/" + string.Join('/', relativeSegments);
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            fileSystem = null!;
            remotePath = string.Empty;
            return false;
        }
    }

    private (MountEntry mount, string absolutePath, string[] relativeSegments) Resolve(string path, string workingDirectory)
    {
        if (_mounts.Count == 0)
        {
            throw new InvalidOperationException("No filesystems have been mounted.");
        }

        var absolute = PathHelpers.NormalizeAbsolute(path, workingDirectory);
        var segments = PathHelpers.NormalizeSegments(absolute);

        foreach (var mount in _mounts)
        {
            if (PathHelpers.IsPrefix(mount.Segments, segments))
            {
                var relative = segments.Skip(mount.Segments.Length).ToArray();
                return (mount, absolute, relative);
            }
        }

        throw new DirectoryNotFoundException($"No mount point covers '{absolute}'.");
    }

    private IEnumerable<string> GetDirectChildMountNames(string[] parentSegments)
    {
        var childDepth = parentSegments.Length + 1;
        return _mounts
            .Where(m => m.Segments.Length == childDepth && PathHelpers.IsPrefix(parentSegments, m.Segments))
            .Select(m => m.Segments[^1]);
    }

    private sealed record MountEntry(string Path, string[] Segments, IVirtualFileSystem FileSystem);
}
