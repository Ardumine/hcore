using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HCore.Modules.Base;

public interface IVirtualFileSystem
{
    string Name { get; }
    bool IsReadOnly { get; }
    IVirtualDirectory Root { get; }
}

public interface IVirtualNode
{
    string Name { get; }
    IVirtualDirectory? Parent { get; }

    string Path => Parent is null ? "/" : Parent.Path == "/" ? $"/{Name}" : $"{Parent.Path}/{Name}";
}

public interface IVirtualDirectory : IVirtualNode
{
    IEnumerable<IVirtualNode> Enumerate();
    IEnumerable<IVirtualNode> EnumerateDirectories();
    IEnumerable<IVirtualNode> EnumerateFiles();

    IVirtualDirectory? TryGetDirectory(string name);
    IVirtualDirectory GetDirectory(string name);
    IVirtualFile? TryGetFile(string name);
    IVirtualFile GetFile(string name);
    IVirtualNode? TryGet(string name);
    IVirtualDirectory CreateDirectory(string name);
    bool TryDelete(string name);
    IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default);
}

public interface IVirtualFile : IVirtualNode
{
    Stream GetStream(FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite);
    byte[] ReadAllBytes();
    string ReadString(Encoding? encoding = null);
    void Write(ReadOnlySpan<byte> data);
}
