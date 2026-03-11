using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HCore.Main.Vfs;

public sealed class DeviceFileSystem : IVirtualFileSystem
{
    public DeviceFileSystem()
    {
        var root = new ReadOnlyVirtualDirectory("/");
        var devName = new ReadOnlyVirtualFile("devname", root, Encoding.UTF8.GetBytes("dev: sample device"));
        root.AddChild(devName);
        var random = new ReadOnlyVirtualFile("random", root, Encoding.UTF8.GetBytes("pseudo random"));
        root.AddChild(random);
        Root = root;
    }

    public string Name => "devfs";
    public bool IsReadOnly => true;
    public IVirtualDirectory Root { get; }
}

internal sealed class ReadOnlyVirtualDirectory : VirtualNode, IVirtualDirectory
{
    private readonly List<IVirtualNode> _children = new();

    public ReadOnlyVirtualDirectory(string name, IVirtualDirectory? parent = null)
        : base(name, parent)
    {
    }

    public void AddChild(IVirtualNode node)
    {
        if (!ReferenceEquals(node.Parent, this))
        {
            throw new InvalidOperationException("Child node must reference this directory as its parent.");
        }

        if (_children.Any(child => string.Equals(child.Name, node.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"A node named '{node.Name}' already exists in '{Path}'.");
        }

        _children.Add(node);
    }

    public IEnumerable<IVirtualNode> Enumerate() => _children.ToArray();

    public IEnumerable<IVirtualNode> EnumerateDirectories() => _children.OfType<IVirtualDirectory>().Cast<IVirtualNode>().ToArray();

    public IEnumerable<IVirtualNode> EnumerateFiles() => _children.OfType<IVirtualFile>().Cast<IVirtualNode>().ToArray();

    public IVirtualDirectory? TryGetDirectory(string name) => _children.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) as IVirtualDirectory;

    public IVirtualDirectory GetDirectory(string name) => TryGetDirectory(name) ?? throw new DirectoryNotFoundException($"Directory '{name}' not found in '{Path}'.");

    public IVirtualFile? TryGetFile(string name) => _children.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) as IVirtualFile;

    public IVirtualFile GetFile(string name) => TryGetFile(name) ?? throw new FileNotFoundException($"File '{name}' not found in '{Path}'.");

    public IVirtualNode? TryGet(string name) => _children.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    public IVirtualDirectory CreateDirectory(string name) => throw new InvalidOperationException("Filesystem is read-only.");

    public bool TryDelete(string name) => throw new InvalidOperationException("Filesystem is read-only.");

    public IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default) => throw new InvalidOperationException("Filesystem is read-only.");
}

internal sealed class ReadOnlyVirtualFile : VirtualNode, IVirtualFile
{
    private readonly byte[] _data;

    public ReadOnlyVirtualFile(string name, IVirtualDirectory parent, ReadOnlySpan<byte> data)
        : base(name, parent)
    {
        _data = data.ToArray();
    }

    public Stream GetStream(FileMode mode = FileMode.Open, FileAccess access = FileAccess.Read)
    {
        if (access != FileAccess.Read)
        {
            throw new InvalidOperationException("File is read-only.");
        }

        if (mode is not (FileMode.Open or FileMode.OpenOrCreate))
        {
            throw new InvalidOperationException("Read-only files support only open operations.");
        }

        return new MemoryStream(_data, writable: false);
    }

    public byte[] ReadAllBytes() => _data.ToArray();

    public string ReadString(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(_data);
    }

    public void Write(ReadOnlySpan<byte> data) => throw new InvalidOperationException("File is read-only.");
}
