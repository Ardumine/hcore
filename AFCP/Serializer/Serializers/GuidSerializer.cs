using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public static class GuidSerializer
{
    public static MethodInfo GetGuidSerializer()
    {
        DynamicMethod serializeMethod = new(
            "Serialize for guid",
            typeof(void),
            [typeof(Serializer), typeof(Stream), typeof(Guid)]);
        var il = serializeMethod.GetILGenerator();

        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(byte[]), typeof(int), typeof(int)]);
        MethodInfo? toBytes = typeof(Guid).GetMethod("ToByteArray", []);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarga, 2);
        il.Emit(OpCodes.Call, toBytes!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_S, 16);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetGuidDeserializer()
    {
        DynamicMethod deserializeMethod = new(
            "Deserialize for GUID",
            typeof(Guid),
            [typeof(Serializer), typeof(Stream)]);

        var il = deserializeMethod.GetILGenerator();
        il.Emit(OpCodes.Nop);

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        ConstructorInfo? guidCtor = typeof(Guid).GetConstructor([typeof(byte[])]);

        il.DeclareLocal(typeof(byte[]));
        il.Emit(OpCodes.Nop);

        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_S, 16);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Newobj, guidCtor!);
        il.Emit(OpCodes.Ret);
        return deserializeMethod;
    }
}
