using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace AFCP;

public static class ListaSerializer
{
    private static int GetObjectSize(Type type)
        => type.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(type)) : Marshal.SizeOf(type);

    public static MethodInfo GetUnmanagedListSerializer(KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for list unmanaged type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        var listType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;

        MethodInfo asSpanMethod = typeof(CollectionsMarshal).GetMethod("AsSpan", BindingFlags.Static | BindingFlags.Public)!;
        MethodInfo asBytes = typeof(MemoryMarshal).GetMethods().Where(p => p.Name == "AsBytes").First().MakeGenericMethod(itemType);

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(Span<byte>));
        il.DeclareLocal(typeof(int));

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, asSpanMethod);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldloca, 0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, 1);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, asBytes);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetUnmanagedListDeserializer(KAType type)
    {
        var listType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;
        int itemSize = GetObjectSize(itemType);

        DynamicMethod deserializeMethod = new(
           $"Deserialize for list unmanaged type: {type.FullName}",
           listType,
            [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();

        ConstructorInfo byteSpanCtor = typeof(Span<byte>).GetConstructor([typeof(byte[])])!;
        MethodInfo cast = typeof(MemoryMarshal).GetMethods().Where(p => p.Name == "Cast").First().MakeGenericMethod(typeof(byte), itemType);
        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        ConstructorInfo listCtor = listType.GetConstructor([typeof(int)])!;
        MethodInfo listAddRange = typeof(CollectionExtensions).GetMethod("AddRange", BindingFlags.Static | BindingFlags.Public)!.MakeGenericMethod(itemType);

        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(Span<>).MakeGenericType(itemType));
        il.DeclareLocal(listType);

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

        il.Emit(OpCodes.Ldloca, 2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Newobj, listCtor);
        il.Emit(OpCodes.Stloc_3);

        il.Emit(OpCodes.Ldloc_3);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Call, listAddRange);

        il.Emit(OpCodes.Ldloc_3);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }

    public static MethodInfo GetManagedListSerializer(Serializer ser, KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for list managed type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        var listType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;
        var serializerForValue = ser.GetMethodForSerialize(serializeMethod, type.SubHoldingType[0], type);

        var getItems = listType.GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!
            ?? throw new Exception($"List<{itemType.Name}> has no _items field on this runtime; managed-list serialization is not supported for it.");
        var getCount = listType.GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance)!
            ?? throw new Exception($"List<{itemType.Name}> has no _size field on this runtime; managed-list serialization is not supported for it.");

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(getItems.FieldType);

        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, getCount);
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, getItems);
        il.Emit(OpCodes.Stloc_2);

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
            il.Emit(OpCodes.Ldloc_2);
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

    public static MethodInfo GetManagedListDeserializer(Serializer ser, KAType type)
    {
        var listType = type.DType;
        var itemType = type.SubHoldingType[0]!.DType;

        DynamicMethod deserializeMethod = new(
           $"Deserialize for list managed type: {type.FullName}",
           listType,
            [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();
        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        ConstructorInfo listCtor = listType.GetConstructor([typeof(int)])!;
        MethodInfo? addList = listType.GetMethod("Add", [itemType]);
        var deserializerForValue = ser.GetMethodForDeserialize(deserializeMethod, type.SubHoldingType[0]!, type);

        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(listType);

        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_2);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);
        il.Emit(OpCodes.Nop);

        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldobj, typeof(uint));
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Newobj, listCtor!);
        il.Emit(OpCodes.Stloc_3);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Br_S, checkLabel);
        il.MarkLabel(startLoopLabel);
        {
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, deserializerForValue);
            il.Emit(OpCodes.Callvirt, addList!);
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
