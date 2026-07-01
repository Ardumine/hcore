using System.Reflection;

namespace AFCP;

/// <summary>
/// Reflection helpers that classify a <see cref="Type"/> into a
/// <see cref="KASubTypes"/> bucket so the serializer can pick the right IL-emit
/// path. Ported from V2's <c>SerializerTools</c>.
/// </summary>
public static class SerializerTools
{
    /// <summary>True for primitives, pointers, enums, and structs whose every field is itself unmanaged (memcpy-able).</summary>
    public static bool IsUnmanagedStruct(Type type)
    {
        if (type.IsPrimitive || type.IsPointer || type.IsEnum) return true;
        if (!type.IsValueType) return false;
        return type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .All(f => IsUnmanagedStruct(f.FieldType));
    }

    public static bool IsManagedStruct(Type type)
        => type.IsValueType && !type.IsPrimitive && !type.IsEnum && !type.IsPointer;

    public static bool CanBeNull(Type type)
    {
        if (!type.IsValueType) return true; // reference types are nullable
        return Nullable.GetUnderlyingType(type) != null;
    }

    public static bool IsList(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);

    public static bool IsDict(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    public static bool IsSpan(Type type)
        => type.FullName!.Contains("System.Span");

    public static bool CanBeDerived(Type type)
        => type.IsClass && !type.IsSealed && !type.IsAbstract;
}
