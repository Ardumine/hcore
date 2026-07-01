using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace AFCP;

public static class UnmanagedSerializer
{
    private static int GetObjectSize(Type type)
        => type.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(type)) : Marshal.SizeOf(type);

    public static MethodInfo GetUnmanagedStructSerializer(KAType structType)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for struct type: {structType.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), structType.DType]);
        var il = serializeMethod.GetILGenerator();

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);
        int objSize = GetObjectSize(structType.DType);

        il.DeclareLocal(structType.DType.MakePointerType());

        il.Emit(OpCodes.Ldarga_S, 2);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4, objSize);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetUnmanagedStructDeserializer(KAType structType)
    {
        DynamicMethod deserializeMethod = new(
            $"Deserialize for struct type: {structType.FullName}",
            structType.DType,
            [typeof(Serializer), typeof(Stream)]);

        int objSize = GetObjectSize(structType.DType);
        var il = deserializeMethod.GetILGenerator();
        il.Emit(OpCodes.Nop);

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);

        il.DeclareLocal(typeof(byte[]));
        il.Emit(OpCodes.Nop);

        il.Emit(OpCodes.Ldc_I4, objSize);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4, objSize);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);
        il.Emit(OpCodes.Nop);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldobj, structType.DType);
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }
}
