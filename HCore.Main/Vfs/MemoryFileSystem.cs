using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HCore.Modules.Base;

namespace HCore.Main.Vfs;

public sealed class MemoryFileSystem : IVirtualFileSystem
{
    public MemoryFileSystem(string name = "memfs")
    {
        Name = name;
        Root = new MemoryDirectory("/");
    }

    public string Name { get; }
    public bool IsReadOnly => false;
    public IVirtualDirectory Root { get; }
}

internal sealed class MemoryDirectory : VirtualNode, IVirtualDirectory
{
    private readonly Dictionary<string, IVirtualNode> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    public MemoryDirectory(string name, IVirtualDirectory? parent = null)
        : base(name, parent)
    {
    }

    public IEnumerable<IVirtualNode> Enumerate()
    {
        lock (_syncRoot)
        {
            return _children.Values.ToArray();
        }
    }

    public IEnumerable<IVirtualNode> EnumerateDirectories()
    {
        lock (_syncRoot)
        {
            return _children.Values.OfType<IVirtualDirectory>().Cast<IVirtualNode>().ToArray();
        }
    }

    public IEnumerable<IVirtualNode> EnumerateFiles()
    {
        lock (_syncRoot)
        {
            return _children.Values.OfType<IVirtualFile>().Cast<IVirtualNode>().ToArray();
        }
    }

    public IVirtualDirectory? TryGetDirectory(string name)
    {
        lock (_syncRoot)
        {
            return _children.TryGetValue(name, out var node) ? node as IVirtualDirectory : null;
        }
    }

    public IVirtualDirectory GetDirectory(string name)
    {
        return TryGetDirectory(name) ?? throw new DirectoryNotFoundException($"Directory '{name}' not found in '{Path}'.");
    }

    public IVirtualFile? TryGetFile(string name)
    {
        lock (_syncRoot)
        {
            return _children.TryGetValue(name, out var node) ? node as IVirtualFile : null;
        }
    }

    public IVirtualFile GetFile(string name)
    {
        return TryGetFile(name) ?? throw new FileNotFoundException($"File '{name}' not found in '{Path}'.");
    }

    public IVirtualNode? TryGet(string name)
    {
        lock (_syncRoot)
        {
            return _children.TryGetValue(name, out var node) ? node : null;
        }
    }

    public IVirtualDirectory CreateDirectory(string name)
    {
        lock (_syncRoot)
        {
            if (_children.TryGetValue(name, out var existing))
            {
                if (existing is IVirtualDirectory directory)
                {
                    return directory;
                }

                throw new IOException($"Cannot create directory '{name}' because a file with the same name exists.");
            }

            var child = new MemoryDirectory(name, this);
            _children[name] = child;
            return child;
        }
    }

    public bool TryDelete(string name)
    {
        lock (_syncRoot)
        {
            if (!_children.TryGetValue(name, out var node))
            {
                return false;
            }

            if (node is IVirtualDirectory directory && directory.Enumerate().Any())
            {
                return false;
            }

            return _children.Remove(name);
        }
    }

    public IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default)
    {
        lock (_syncRoot)
        {
            if (_children.TryGetValue(name, out var existing))
            {
                if (existing is IVirtualDirectory)
                {
                    throw new IOException($"Cannot create file '{name}' because a directory with the same name exists.");
                }

                if (!overwrite)
                {
                    throw new IOException($"File '{name}' already exists.");
                }

                _children.Remove(name);
            }

            var file = new MemoryFile(name, this, initialData);
            _children[name] = file;
            return file;
        }
    }
}

internal sealed class MemoryFile : VirtualNode, IVirtualFile
{
    private byte[] _data;
    private readonly object _syncRoot = new();

    internal MemoryFile(string name, IVirtualDirectory parent, ReadOnlySpan<byte> initialData)
        : base(name, parent)
    {
        _data = initialData.ToArray();
    }

    public Stream GetStream(FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite)
    {
        lock (_syncRoot)
        {
            if (mode == FileMode.CreateNew)
            {
                throw new IOException("File already exists.");
            }

            if (access == FileAccess.Read)
            {
                if (mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate)
                {
                    throw new IOException("Read-only stream cannot modify the file.");
                }

                return new MemoryStream(_data, writable: false);
            }

            if (mode is FileMode.Create or FileMode.Truncate)
            {
                _data = Array.Empty<byte>();
            }

            var seed = mode switch
            {
                FileMode.Create => Array.Empty<byte>(),
                FileMode.Truncate => Array.Empty<byte>(),
                _ => _data.ToArray()
            };

            return new MemoryFileWritableStream(this, seed, mode, access);
        }
    }

    public byte[] ReadAllBytes()
    {
        lock (_syncRoot)
        {
            return _data.ToArray();
        }
    }

    public string ReadString(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(ReadAllBytes());
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_syncRoot)
        {
            _data = data.ToArray();
        }
    }

    internal void SetData(byte[] buffer)
    {
        lock (_syncRoot)
        {
            _data = buffer;
        }
    }

    private sealed class MemoryFileWritableStream : MemoryStream
    {
        private readonly MemoryFile _owner;
        private readonly FileAccess _access;
        private bool _disposed;

        internal MemoryFileWritableStream(MemoryFile owner, byte[] seed, FileMode mode, FileAccess access)
        {
            _owner = owner;
            _access = access;

            if ((access & FileAccess.Write) == 0)
            {
                throw new NotSupportedException("Writable stream requires write access.");
            }

            if (seed.Length > 0 && mode != FileMode.Create && mode != FileMode.Truncate)
            {
                Write(seed, 0, seed.Length);
            }

            if (mode == FileMode.Append)
            {
                Position = Length;
            }
            else if (mode is FileMode.Create or FileMode.Truncate)
            {
                Position = 0;
                SetLength(0);
            }
            else
            {
                Position = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if ((_access & FileAccess.Read) == 0)
            {
                throw new NotSupportedException("Stream is write-only.");
            }

            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> destination)
        {
            if ((_access & FileAccess.Read) == 0)
            {
                throw new NotSupportedException("Stream is write-only.");
            }

            return base.Read(destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                var buffer = ToArray();
                _owner.SetData(buffer);
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
