using System.Reflection;
using System.Reflection.Emit;

namespace AFCP;

public static class StringSerializer
{
    public static MethodInfo GetStringSerializer()
    {
        DynamicMethod serializeMethod = new(
            "Serializer for string",
            typeof(void),
            [typeof(Serializer), typeof(Stream), typeof(string)]);
        var il = serializeMethod.GetILGenerator();

        ConstructorInfo? spanCtor = typeof(ReadOnlySpan<byte>).GetConstructor([typeof(void*), typeof(int)]);
        MethodInfo? stringRef = typeof(string).GetMethod("GetPinnableReference", []);
        MethodInfo? writeStream = typeof(Stream).GetMethod("Write", [typeof(ReadOnlySpan<byte>)]);

        il.DeclareLocal(typeof(int));

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Stloc_0);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, 0);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, stringRef!);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Newobj, spanCtor!);
        il.Emit(OpCodes.Callvirt, writeStream!);

        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }

    public static MethodInfo GetStringDeserializer()
    {
        DynamicMethod serializeMethod = new(
            "Deserializer for string",
            typeof(string),
            [typeof(Serializer), typeof(Stream)]);
        var il = serializeMethod.GetILGenerator();

        MethodInfo? streamReadExactly = typeof(Stream).GetMethod("ReadExactly", [typeof(byte[]), typeof(int), typeof(int)]);
        ConstructorInfo? strConstructor = typeof(string).GetConstructor([typeof(char*), typeof(int), typeof(int)]);

        il.DeclareLocal(typeof(byte[]));
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(byte[]));

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
        il.Emit(OpCodes.Stloc_1);

        // An empty string has a zero-length body — `Ldelema` on element 0 of an
        // empty array below always bounds-checks even though the body construct
        // is skipped for zero length (the same class of bug as the zero-length
        // unmanaged array fast path fixed in C7a). Short-circuit to string.Empty.
        Label emptyLabel = il.DefineLabel();
        Label doneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Brfalse_S, emptyLabel);

        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Stloc_2);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Callvirt, streamReadExactly!);

        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(byte));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Newobj, strConstructor!);
        il.Emit(OpCodes.Br_S, doneLabel);

        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Ldsfld, typeof(string).GetField(nameof(string.Empty))!);

        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);
        return serializeMethod;
    }
}
