namespace AFCP;

/// <summary>
/// Classification of a CLR <see cref="Type"/> for the binary serializer. The
/// serializer dispatches to a per-category IL-emit serializer based on
/// <see cref="TypeOfData"/>. Ported from V2's <c>KAType</c>.
/// </summary>
public struct KAType
{
    public required Type DType { get; set; }

    public required KASubTypes TypeOfData { get; set; }
    public required bool IsUnmanaged { get; set; }
    public required bool CanBeNull { get; set; }
    public required bool CanBeDerived { get; set; }

    /// <summary>
    /// For arrays/lists: [0] is the element type. For dictionaries: [0] is the
    /// key type, [1] is the value type. Empty otherwise.
    /// </summary>
    public KAType[] SubHoldingType { get; set; }

    public required string? FullName { get; set; }
    public required string? AssemblyName { get; set; }

    public static KAType Get(Type type)
    {
        var typeofData = KASubTypes.UnmanagedStruct;
        var holdingTypes = new List<KAType>();

        if (type == typeof(string))
        {
            typeofData = KASubTypes.String;
        }
        else if (SerializerTools.IsList(type))
        {
            var listItemType = type.GetGenericArguments().Single();
            if (SerializerTools.IsUnmanagedStruct(listItemType))
            {
                typeofData = KASubTypes.UnmanagedList;
                holdingTypes.Add(Get(listItemType));
            }
            else
            {
                typeofData = KASubTypes.ManagedList;
                holdingTypes.Add(Get(listItemType));
            }
        }
        else if (SerializerTools.IsSpan(type))
        {
            throw new Exception($"The type {type} is a span. Spans cannot be serialized. Use an array to hold your data.");
        }
        else if (type.IsArray && type.GetArrayRank() == 1)
        {
            Type arrayType = type.GetElementType()!;
            if (SerializerTools.IsUnmanagedStruct(arrayType))
            {
                typeofData = KASubTypes.Unmanaged1DArray;
                holdingTypes.Add(Get(arrayType));
            }
            else
            {
                typeofData = KASubTypes.Managed1DArray;
                holdingTypes.Add(Get(arrayType));
            }
        }
        else if (type == typeof(Guid))
        {
            typeofData = KASubTypes.Guid;
        }
        else if (SerializerTools.IsUnmanagedStruct(type))
        {
            typeofData = KASubTypes.UnmanagedStruct;
        }
        else if (SerializerTools.IsDict(type))
        {
            typeofData = KASubTypes.Dict;
            holdingTypes.Add(Get(type.GetGenericArguments()[0]));
            holdingTypes.Add(Get(type.GetGenericArguments()[1]));
        }
        else if (SerializerTools.IsManagedStruct(type))
        {
            typeofData = KASubTypes.ManagedStruct;
        }
        else if (type.IsClass)
        {
            typeofData = KASubTypes.Class;
        }
        else
        {
            throw new Exception($"Cannot get the general type of the type {type.Name}");
        }

        return new()
        {
            DType = type,
            TypeOfData = typeofData,
            CanBeNull = SerializerTools.CanBeNull(type),
            SubHoldingType = holdingTypes.ToArray(),
            FullName = type.FullName,
            IsUnmanaged = SerializerTools.IsUnmanagedStruct(type),
            AssemblyName = type.AssemblyQualifiedName,
            CanBeDerived = SerializerTools.CanBeDerived(type),
        };
    }

    public override bool Equals(object? obj) => obj is KAType other && DType.Equals(other.DType);
    public static bool operator ==(KAType left, KAType right) => left.DType.Equals(right.DType);
    public static bool operator !=(KAType left, KAType right) => !left.DType.Equals(right.DType);
    public override int GetHashCode() => DType.GetHashCode();
}

public enum KASubTypes
{
    String,
    Guid,
    UnmanagedStruct,    // int, float, long, bool, byte, unmanaged structs, enums...
    ManagedStruct,
    Class,
    UnmanagedList,
    ManagedList,
    Unmanaged1DArray,
    Managed1DArray,
    Dict,
}
