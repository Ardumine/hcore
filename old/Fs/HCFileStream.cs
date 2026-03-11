using Microsoft.Win32.SafeHandles;

namespace HCore.Main.Fs;

public class HCFileStream : FileStream
{
    public HCFileStream(SafeFileHandle handle, FileAccess access) : base(handle, access)
    {
    }

    public HCFileStream(SafeFileHandle handle, FileAccess access, int bufferSize) : base(handle, access, bufferSize)
    {
    }

    public HCFileStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) : base(handle, access, bufferSize, isAsync)
    {
    }

    public HCFileStream(IntPtr handle, FileAccess access) : base(handle, access)
    {
    }

    public HCFileStream(IntPtr handle, FileAccess access, bool ownsHandle) : base(handle, access, ownsHandle)
    {
    }

    public HCFileStream(IntPtr handle, FileAccess access, bool ownsHandle, int bufferSize) : base(handle, access, ownsHandle, bufferSize)
    {
    }

    public HCFileStream(IntPtr handle, FileAccess access, bool ownsHandle, int bufferSize, bool isAsync) : base(handle, access, ownsHandle, bufferSize, isAsync)
    {
    }

    public HCFileStream(string path, FileMode mode) : base(path, mode)
    {
    }

    public HCFileStream(string path, FileMode mode, FileAccess access) : base(path, mode, access)
    {
    }

    public HCFileStream(string path, FileMode mode, FileAccess access, FileShare share) : base(path, mode, access, share)
    {
    }

    public HCFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) : base(path, mode, access, share, bufferSize)
    {
    }

    public HCFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) : base(path, mode, access, share, bufferSize, useAsync)
    {
    }

    public HCFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) : base(path, mode, access, share, bufferSize, options)
    {
    }

    public HCFileStream(string path, FileStreamOptions options) : base(path, options)
    {
    }
}