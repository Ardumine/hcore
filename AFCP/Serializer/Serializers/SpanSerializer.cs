using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace AFCP;

public static class SpanSerializer
{
    private static int GetObjectSize(Type type)
        => type.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(type)) : Marshal.SizeOf(type);

    public static MethodInfo GetUnmanagedSpanSerializer(KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for span unmanaged type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        var itemType = type.SubHoldingType[0]!.DType;
        MethodInfo asBytes = typeof(MemoryMarshal).GetMethods().Where(p => p.Name == "AsBytes").First().MakeGenericMethod(itemType);

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(int));

        il.Emit(OpCodes.Ldarga, 2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, 0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, asBytes);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetUnmanagedSpanDeserializer(KAType type)
    {
        var spanType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;
        int itemSize = GetObjectSize(itemType);

        DynamicMethod deserializeMethod = new(
           $"Deserialize for span unmanaged type: {type.FullName}",
           spanType,
            [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();

        ConstructorInfo byteSpanCtor = typeof(Span<byte>).GetConstructor([typeof(byte[])])!;
        MethodInfo cast = typeof(MemoryMarshal).GetMethods().Where(p => p.Name == "Cast").First().MakeGenericMethod(typeof(byte), itemType);
        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);

        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(typeof(int));

        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);
        il.Emit(OpCodes.Nop);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldobj, typeof(uint));
        il.Emit(OpCodes.Ldc_I4, itemSize);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Newobj, byteSpanCtor!);
        il.Emit(OpCodes.Call, cast);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }
}
