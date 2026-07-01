using System.Collections.Concurrent;
using System.Reflection;

namespace AFCP;

public struct KATransformerForType
{
    public required KAType Type { get; set; }

    public required MethodInfo methodSerialize { get; set; }
    public required ReturnValueDelegate FastSerialize { get; set; }

    public MethodInfo methodDeserialize { get; set; }
    public ReturnValueDelegate FastDeserialize { get; set; }

    public required bool CanhandleDerived { get; set; }
}

/// <summary>
/// A reflection-free binary serializer that compiles a per-type IL-emit
/// serializer/deserializer on first use and caches it. Ported from V2's
/// <c>Kernel.AFCP.Serializer</c>. Supports: unmanaged structs/enums (memcpy),
/// strings (UTF-16, length-prefixed), <see cref="Guid"/>, classes (public
/// get/set properties, <see cref="IgnoreParseAttribute"/> to skip), nullable
/// reference/value types (null-flag byte), 1D arrays and <see cref="List{T}"/>
/// of unmanaged or managed items, <see cref="Dictionary{TKey,TValue}"/>, and
/// derived (polymorphic) classes (writes the assembly-qualified type name).
///
/// All integers on the wire are little-endian. The format is not self-describing
/// — both ends must agree on the type <typeparamref name="T"/> at the call site.
/// </summary>
public class Serializer
{
    private readonly ConcurrentDictionary<Type, KATransformerForType> _typesTransformer = new();
    private readonly ConcurrentDictionary<Type, KATransformerForType> _typesForDerived = new();

    public KATransformerForType GetTypeTransformer(KAType type)
    {
        if (_typesTransformer.TryGetValue(type.DType, out var existing)) return existing;

        KATransformerForType transformer;
        if (type.TypeOfData == KASubTypes.String)
        {
            transformer = CreateStringTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Class || type.TypeOfData == KASubTypes.ManagedStruct)
        {
            transformer = CreateClassTransformer(type);
            if (type.CanBeDerived)
            {
                transformer = CreateDerivedTransformer(type);
            }
        }
        else if (type.TypeOfData == KASubTypes.UnmanagedList)
        {
            transformer = CreateUnmanagedListTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.ManagedList)
        {
            transformer = CreateManagedListTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.UnmanagedStruct)
        {
            transformer = CreateUnmanagedStructTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Guid)
        {
            transformer = CreateGuidTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Dict)
        {
            transformer = CreateDictTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Unmanaged1DArray)
        {
            transformer = CreateUnmanagedArrayTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Managed1DArray)
        {
            transformer = CreateManagedArrayTransformer(type);
        }
        else
        {
            throw new Exception($"No transformer found for type {type.FullName}");
        }

        if (type.CanBeNull)
        {
            transformer = CreateNullTransformer(type, transformer);
        }

        _typesTransformer[type.DType] = transformer;
        return transformer;
    }

    public KATransformerForType GetTypeTransformer(Type type)
    {
        if (_typesTransformer.TryGetValue(type, out var existing)) return existing;
        return GetTypeTransformer(KAType.Get(type));
    }

    public KATransformerForType GetTypeTransformerDerived(KAType type)
    {
        if (_typesForDerived.TryGetValue(type.DType, out var existing)) return existing;

        KATransformerForType transformer;
        if (type.TypeOfData == KASubTypes.String)
        {
            transformer = CreateStringTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Class || type.TypeOfData == KASubTypes.ManagedStruct)
        {
            transformer = CreateClassTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.UnmanagedList)
        {
            transformer = CreateUnmanagedListTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.ManagedList)
        {
            transformer = CreateManagedListTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.UnmanagedStruct)
        {
            transformer = CreateUnmanagedStructTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Guid)
        {
            transformer = CreateGuidTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Dict)
        {
            transformer = CreateDictTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Unmanaged1DArray)
        {
            transformer = CreateUnmanagedArrayTransformer(type);
        }
        else if (type.TypeOfData == KASubTypes.Managed1DArray)
        {
            transformer = CreateManagedArrayTransformer(type);
        }
        else
        {
            throw new Exception($"No transformer found for type {type.FullName}");
        }

        if (type.CanBeNull)
        {
            transformer = CreateNullTransformer(type, transformer);
        }
        _typesForDerived[type.DType] = transformer;
        return transformer;
    }

    public MethodInfo GetMethodForSerialize(MethodInfo currentSerializer, KAType objType, KAType currentSerType)
    {
        if (objType.Equals(currentSerType))
        {
            if (SerializerTools.CanBeNull(objType.DType))
                return NullableSerializer.GetNullSerializer(objType, currentSerializer);
            return currentSerializer;
        }
        else if (_typesTransformer.TryGetValue(objType.DType, out var kASerializerType))
        {
            return kASerializerType.methodSerialize;
        }
        else
        {
            return GetTypeTransformer(objType).methodSerialize;
        }
    }

    public MethodInfo GetMethodForDeserialize(MethodInfo currentDeserializer, KAType objType, KAType currentSerType)
    {
        if (objType.Equals(currentSerType))
        {
            if (SerializerTools.CanBeNull(objType.DType))
                return NullableSerializer.GetNullDeserializer(objType, currentDeserializer);
            return currentDeserializer;
        }
        else if (_typesTransformer.TryGetValue(objType.DType, out var kASerializerType))
        {
            return kASerializerType.methodDeserialize;
        }
        else
        {
            return GetTypeTransformer(objType).methodDeserialize;
        }
    }

    public KATransformerForType CreateNullTransformer(KAType type, KATransformerForType originalTransformer)
    {
        var serializeMethod = NullableSerializer.GetNullSerializer(type, originalTransformer.methodSerialize);
        var deserializeMethod = NullableSerializer.GetNullDeserializer(type, originalTransformer.methodDeserialize);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateDerivedTransformer(KAType type)
    {
        var serializeMethod = DerivedSerializer.GetDerivedSerializer(this, type);
        var deserializeMethod = DerivedSerializer.GetDerivedDeserializer(this, type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = true
        };
    }

    public KATransformerForType CreateClassTransformer(KAType type)
    {
        var serializeMethod = ClassSerializer.CreateClassSerializerMethod(this, type);
        var deserializeMethod = ClassSerializer.CreateDeserializerMethod(this, type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateUnmanagedStructTransformer(KAType type)
    {
        var serializeMethod = UnmanagedSerializer.GetUnmanagedStructSerializer(type);
        var deserializeMethod = UnmanagedSerializer.GetUnmanagedStructDeserializer(type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateStringTransformer(KAType type)
    {
        var serializeMethod = StringSerializer.GetStringSerializer();
        var deserializeMethod = StringSerializer.GetStringDeserializer();

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateGuidTransformer(KAType type)
    {
        var serializeMethod = GuidSerializer.GetGuidSerializer();
        var deserializeMethod = GuidSerializer.GetGuidDeserializer();

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateUnmanagedListTransformer(KAType type)
    {
        var serializeMethod = ListaSerializer.GetUnmanagedListSerializer(type);
        var deserializeMethod = ListaSerializer.GetUnmanagedListDeserializer(type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateManagedListTransformer(KAType type)
    {
        var serializeMethod = ListaSerializer.GetManagedListSerializer(this, type);
        var deserializeMethod = ListaSerializer.GetManagedListDeserializer(this, type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateUnmanagedArrayTransformer(KAType type)
    {
        var serializeMethod = ArraySerializer.GetUnmanagedArraySerializer(type);
        var deserializeMethod = ArraySerializer.GetUnmanagedArrayDeserializer(type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateManagedArrayTransformer(KAType type)
    {
        var serializeMethod = ArraySerializer.GetManagedArraySerializer(this, type);
        var deserializeMethod = ArraySerializer.GetManagedArrayDeserializer(this, type);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public KATransformerForType CreateDictTransformer(KAType type)
    {
        var serializeMethod = DictSerializer.CreateSerializer(this, type, type.SubHoldingType[0], type.SubHoldingType[1]);
        var deserializeMethod = DictSerializer.CreateDeserializer(this, type, type.SubHoldingType[0], type.SubHoldingType[1]);

        return new KATransformerForType
        {
            Type = type,
            methodSerialize = serializeMethod,
            FastSerialize = new FastMethodInfo(serializeMethod).Delegate,
            methodDeserialize = deserializeMethod,
            FastDeserialize = new FastMethodInfo(deserializeMethod).Delegate,
            CanhandleDerived = false
        };
    }

    public void Serialize<T>(Stream stream, T? obj)
    {
        GetTypeTransformer(typeof(T)).FastSerialize(null, [this, stream, (object?)obj]);
    }

    /// <summary>Serialize by runtime type (used by the derived/polymorphic path). Do not remove.</summary>
    public void Serialize(Stream stream, object obj, Type type)
    {
        if (!_typesForDerived.TryGetValue(type, out var kASerializerType))
        {
            kASerializerType = GetTypeTransformerDerived(KAType.Get(type));
            _typesForDerived[type] = kASerializerType;
        }
        kASerializerType.FastSerialize(null, [this, stream, obj]);
    }

    public T Deserialize<T>(Stream stream)
    {
        return (T)GetTypeTransformer(typeof(T)).FastDeserialize(null, [this, stream])!;
    }

    /// <summary>Deserialize by runtime type (used by the derived/polymorphic path). Do not remove.</summary>
    public object Deserialize(Stream stream, Type type)
    {
        if (!_typesForDerived.TryGetValue(type, out var kASerializerType))
        {
            kASerializerType = GetTypeTransformerDerived(KAType.Get(type));
            _typesForDerived[type] = kASerializerType;
        }
        return kASerializerType.FastDeserialize(null, [this, stream])!;
    }
}
