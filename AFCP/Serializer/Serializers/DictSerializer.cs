using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public static class DictSerializer
{
    public static DynamicMethod CreateSerializer(Serializer ser, KAType dictType, KAType keyType, KAType valueType)
    {
        DynamicMethod serializeMethod = new(
            "Serializer for Dict",
            typeof(void),
            [typeof(Serializer), typeof(Stream), dictType.DType]);

        var serializerForKey = ser.GetMethodForSerialize(serializeMethod, keyType, dictType);
        var serializerForValue = ser.GetMethodForSerialize(serializeMethod, valueType, dictType);

        var getEntries = dictType.DType.GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!
            ?? throw new Exception($"Dictionary<{keyType.DType.Name},{valueType.DType.Name}> has no _entries field on this runtime; dict serialization is not supported for it.");
        var getCount = dictType.DType.GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance)!
            ?? throw new Exception($"Dictionary<{keyType.DType.Name},{valueType.DType.Name}> has no _count field on this runtime; dict serialization is not supported for it.");

        var spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        var writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);
        var getKeyEntry = getEntries.FieldType.GetElementType()!.GetField("key")!;
        var getValueEntry = getEntries.FieldType.GetElementType()!.GetField("value")!;

        var il = serializeMethod.GetILGenerator();
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(getEntries.FieldType);
        il.DeclareLocal(getEntries.FieldType.GetElementType()!.MakePointerType());

        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, getCount);
        il.Emit(OpCodes.Stloc_1);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldfld, getEntries);
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
            il.Emit(OpCodes.Ldloc_2);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldelema, getEntries.FieldType.GetElementType()!);
            il.Emit(OpCodes.Stloc_3);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldfld, getKeyEntry);
            il.Emit(OpCodes.Call, serializerForKey);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldfld, getValueEntry);
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

    public static DynamicMethod CreateDeserializer(Serializer ser, KAType dictType, KAType keyType, KAType valueType)
    {
        DynamicMethod deserializeMethod = new(
            "Deserializer for Dict",
            dictType.DType,
            [typeof(Serializer), typeof(Stream)]);

        var deserializerForKey = ser.GetMethodForDeserialize(deserializeMethod, keyType, dictType);
        var deserializerForValue = ser.GetMethodForDeserialize(deserializeMethod, valueType, dictType);

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        MethodInfo? tryAddDict = dictType.DType.GetMethod("TryAdd", [keyType.DType, valueType.DType]);
        ConstructorInfo? dictConstructor = dictType.DType.GetConstructor([typeof(int)]);

        var il = deserializeMethod.GetILGenerator();
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(dictType.DType);

        Label startLoopLabel = il.DefineLabel();
        Label checkLabel = il.DefineLabel();

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
        il.Emit(OpCodes.Newobj, dictConstructor!);
        il.Emit(OpCodes.Stloc_3);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Br_S, checkLabel);
        il.MarkLabel(startLoopLabel);
        {
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, deserializerForKey);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, deserializerForValue);
            il.Emit(OpCodes.Callvirt, tryAddDict!);
            il.Emit(OpCodes.Pop);
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
