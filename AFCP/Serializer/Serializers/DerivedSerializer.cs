using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public static class DerivedSerializer
{
    public static MethodInfo GetDerivedSerializer(Serializer ser, KAType topType)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for derived type: {topType.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), topType.DType]);
        var il = serializeMethod.GetILGenerator();

        MethodInfo? getType = typeof(object).GetMethod("GetType", []);
        MethodInfo? getAssemblyName = typeof(Type).GetProperty("AssemblyQualifiedName")!.GetMethod;
        MethodInfo? serFunc = typeof(Serializer).GetMethod("Serialize", [typeof(Stream), typeof(object), typeof(Type)]);
        var strWriter = StringSerializer.GetStringSerializer();

        il.DeclareLocal(typeof(Type));

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, getType!);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Callvirt, getAssemblyName!);
        il.Emit(OpCodes.Call, strWriter);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, serFunc!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetDerivedDeserializer(Serializer ser, KAType topType)
    {
        DynamicMethod serializeMethod = new(
            $"Deserialize for derived type: {topType.FullName}",
            topType.DType,
            [typeof(Serializer), typeof(Stream)]);

        var il = serializeMethod.GetILGenerator();
        MethodInfo? strRead = StringSerializer.GetStringDeserializer();
        MethodInfo? getType = typeof(Type).GetMethod("GetType", [typeof(string)]);
        MethodInfo? deserFunc = typeof(Serializer).GetMethod("Deserialize", [typeof(Stream), typeof(Type)]);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);

        il.Emit(OpCodes.Call, strRead!);
        il.Emit(OpCodes.Call, getType!);
        il.Emit(OpCodes.Call, deserFunc!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }
}
