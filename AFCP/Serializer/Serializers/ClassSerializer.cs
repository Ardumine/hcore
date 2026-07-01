using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public struct KAClassProperty
{
    public required string Name { get; set; }
    public required KAType Type { get; set; }
    public required MethodInfo GetMethod { get; set; }
    public required MethodInfo SetMethod { get; set; }
}

public static class ClassSerializer
{
    public static MethodInfo CreateClassSerializerMethod(Serializer ser, KAType type)
    {
        DynamicMethod serializeMethod = new(
            $"Serialize for class type: {type.FullName}",
            typeof(void),
            [typeof(Serializer), typeof(Stream), type.DType]);
        var il = serializeMethod.GetILGenerator();

        foreach (var prop in GetClassProperties(type))
        {
            var propType = prop.Type;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, prop.GetMethod);
            il.Emit(OpCodes.Call, ser.GetMethodForSerialize(serializeMethod, propType, type));
        }

        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo CreateDeserializerMethod(Serializer ser, KAType type)
    {
        DynamicMethod deserMethod = new(
            $"Deserialize for class type: {type.FullName}",
            type.DType,
            [typeof(Serializer), typeof(Stream)]);

        ConstructorInfo? emptyConstructor = type.DType.GetConstructor(Type.EmptyTypes);
        if (emptyConstructor == null)
        {
            throw new Exception($"There is no empty constructor for the type {type.FullName}");
        }

        var il = deserMethod.GetILGenerator();
        il.DeclareLocal(type.DType);
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Newobj, emptyConstructor);
        il.Emit(OpCodes.Stloc_0);

        foreach (var prop in GetClassProperties(type))
        {
            var propType = prop.Type;
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, ser.GetMethodForDeserialize(deserMethod, propType, type));
            il.Emit(OpCodes.Callvirt, prop.SetMethod!);
            il.Emit(OpCodes.Nop);
        }

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
        return deserMethod;
    }

    public static List<KAClassProperty> GetClassProperties(KAType type)
    {
        var props = new List<KAClassProperty>();
        foreach (var prop in type.DType.GetProperties().OrderBy(p => p.Name))
        {
            if (prop.GetMethod == null || prop.SetMethod == null)
            {
                throw new Exception($"The prop '{prop.Name}' of the class '{type.FullName}' has a non-public get or set!");
            }
            if (Attribute.IsDefined(prop, typeof(IgnoreParseAttribute)))
            {
                continue;
            }

            props.Add(new()
            {
                Name = prop.Name,
                Type = KAType.Get(prop.PropertyType),
                GetMethod = prop.GetMethod,
                SetMethod = prop.SetMethod
            });
        }
        return props;
    }
}
