using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace AFCP;

public static class ArraySerializer
{
    private static int GetObjectSize(Type type)
        => type.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(type)) : Marshal.SizeOf(type);

    public static MethodInfo GetUnmanagedArraySerializer(KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for array unmanaged type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        var itemType = type.SubHoldingType[0]!.DType;
        int objSize = GetObjectSize(itemType);

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(int));
        Label skipBodyLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, 0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        // An empty array has no element 0 — Ldelema below would throw
        // IndexOutOfRangeException, so skip the body write entirely.
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, skipBodyLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Ldc_I4, objSize);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.MarkLabel(skipBodyLabel);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetUnmanagedArrayDeserializer(KAType type)
    {
        var arrayType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;
        int itemSize = GetObjectSize(itemType);

        DynamicMethod deserializeMethod = new(
           $"Deserialize for array unmanaged type: {type.FullName}",
           arrayType,
           [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();

        ConstructorInfo byteSpanCtor = typeof(Span<byte>).GetConstructor([typeof(byte[])])!;
        MethodInfo cast = typeof(MemoryMarshal).GetMethods().Where(p => p.Name == "Cast").First().MakeGenericMethod(typeof(byte), itemType);
        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        MethodInfo? toArray = typeof(ReadOnlySpan<>).MakeGenericType(itemType).GetMethod("ToArray", []);

        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(ReadOnlySpan<>).MakeGenericType(itemType));

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
        il.Emit(OpCodes.Stloc_2);

        il.Emit(OpCodes.Ldloca_S, 2);
        il.Emit(OpCodes.Call, toArray!);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }

    public static MethodInfo GetManagedArraySerializer(Serializer ser, KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for array managed type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        var itemType = type.SubHoldingType[0]!.DType;
        var serializerForValue = ser.GetMethodForSerialize(serializeMethod, type.SubHoldingType[0], type);

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));

        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, 1);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Br_S, checkLabel);
        il.MarkLabel(startLoopLabel);
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldelem, itemType);
            il.Emit(OpCodes.Call, serializerForValue);
        }
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);

        il.MarkLabel(checkLabel);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Clt);
        il.Emit(OpCodes.Brtrue_S, startLoopLabel);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetManagedArrayDeserializer(Serializer ser, KAType type)
    {
        var arrayType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;

        DynamicMethod deserializeMethod = new(
           $"Deserialize for array managed type: {type.FullName}",
           arrayType,
           [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();
        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        var deserializerForValue = ser.GetMethodForDeserialize(deserializeMethod, type.SubHoldingType[0]!, type);

        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(arrayType);

        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_2);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);

        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldobj, typeof(uint));
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Newarr, itemType);
        il.Emit(OpCodes.Stloc_3);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Br_S, checkLabel);
        il.MarkLabel(startLoopLabel);
        {
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, deserializerForValue);
            il.Emit(OpCodes.Stelem, itemType);
        }
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);

        il.MarkLabel(checkLabel);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Clt);
        il.Emit(OpCodes.Brtrue_S, startLoopLabel);

        il.Emit(OpCodes.Ldloc_3);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }
}
