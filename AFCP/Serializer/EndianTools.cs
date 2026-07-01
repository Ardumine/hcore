namespace AFCP;

/// <summary>
/// Little-endian byte helpers used by the framing and serializer layers. All
/// AFCP wire integers are little-endian (matches the reference V2 implementation
/// and modern x86/ARM native layout).
/// </summary>
public static class EndianTools
{
    public static unsafe byte[] GetBytes(int value)
    {
        var buffer = new byte[4];
        fixed (byte* b = buffer) *(int*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(uint value)
    {
        var buffer = new byte[4];
        fixed (byte* b = buffer) *(uint*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(long value)
    {
        var buffer = new byte[8];
        fixed (byte* b = buffer) *(long*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(ulong value)
    {
        var buffer = new byte[8];
        fixed (byte* b = buffer) *(ulong*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(short value)
    {
        var buffer = new byte[2];
        fixed (byte* b = buffer) *(short*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(ushort value)
    {
        var buffer = new byte[2];
        fixed (byte* b = buffer) *(ushort*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(float value)
    {
        var buffer = new byte[4];
        fixed (byte* b = buffer) *(float*)b = value;
        return buffer;
    }

    public static unsafe byte[] GetBytes(double value)
    {
        var buffer = new byte[8];
        fixed (byte* b = buffer) *(double*)b = value;
        return buffer;
    }

    public static int GetInt(ReadOnlySpan<byte> b)
        => b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);

    public static uint GetUInt(ReadOnlySpan<byte> b)
        => (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

    public static long GetLong(ReadOnlySpan<byte> b)
        => (uint)GetInt(b[..4]) | ((long)GetUInt(b[4..]) << 32);

    public static ushort GetUShort(ReadOnlySpan<byte> b)
        => (ushort)(b[0] | (b[1] << 8));
}
