using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public static class NullableSerializer
{
    public static MethodInfo GetNullSerializer(KAType topType, MethodInfo serMethod)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for null type: {topType.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), topType.DType]);
        var il = serializeMethod.GetILGenerator();

        MethodInfo? writeStream = typeof(Stream).GetMethod("WriteByte", [typeof(byte)]);

        il.DeclareLocal(typeof(byte));
        Label retLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brtrue_S, retLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, serMethod);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetNullDeserializer(KAType topType, MethodInfo deserMethod)
    {
        DynamicMethod serializeMethod = new(
            $"Deserialize for null type: {topType.FullName}",
            topType.DType,
            [typeof(Serializer), typeof(Stream)]);

        var il = serializeMethod.GetILGenerator();
        MethodInfo? readStream = typeof(Stream).GetMethod("ReadByte");

        Label falseLabel = il.DefineLabel();
        Label retLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, readStream!);
        il.Emit(OpCodes.Brfalse_S, falseLabel);

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Br_S, retLabel);

        il.MarkLabel(falseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, deserMethod);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }
}
