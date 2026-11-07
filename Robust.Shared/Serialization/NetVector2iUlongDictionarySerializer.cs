using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NetSerializer;
using Robust.Shared.Maths;

namespace Robust.Shared.Serialization;

// CMU14 class: preserve NetSerializer's dictionary wire format without its temporary pair array.
internal sealed class NetVector2iUlongDictionarySerializer : IStaticTypeSerializer
{
    private static readonly ConditionalWeakTable<Serializer, ArrayTypeId> ArrayTypeIds = new();

    public bool Handles(Type type) => type == typeof(Dictionary<Vector2i, ulong>);

    // Keep the original subtype closure and type IDs, even though Write does not allocate an array.
    public IEnumerable<Type> GetSubtypes(Type type) => [typeof(KeyValuePair<Vector2i, ulong>[])];

    public MethodInfo GetStaticWriter(Type type) => typeof(NetVector2iUlongDictionarySerializer).GetMethod(nameof(Write))!;
    public MethodInfo GetStaticReader(Type type) => typeof(NetVector2iUlongDictionarySerializer).GetMethod(nameof(Read))!;

    public static void Write(Serializer serializer, Stream stream, Dictionary<Vector2i, ulong>? value)
    {
        if (value == null)
        {
            Primitives.WritePrimitive(stream, 0u);
            return;
        }

        // NetSerializer 1.1.1 writes an object-typed pair array: array type ID, count + 1,
        // then each pair's key (Vector2i.X, Y) and value. Concrete enumeration keeps its order.
        var arrayType = ArrayTypeIds.GetValue(serializer, static s => new ArrayTypeId(s));
        Primitives.WritePrimitive(stream, arrayType.Value);
        Primitives.WritePrimitive(stream, (uint) value.Count + 1);
        foreach (var (key, mask) in value)
        {
            Primitives.WritePrimitive(stream, key.X);
            Primitives.WritePrimitive(stream, key.Y);
            Primitives.WritePrimitive(stream, mask);
        }
    }

    public static void Read(Serializer serializer, Stream stream, out Dictionary<Vector2i, ulong>? value)
    {
        // Retain the normal array reader's type, length, and allocation-limit validation.
        var decoded = serializer.Deserialize(stream);
        if (decoded == null)
        {
            value = null;
            return;
        }

        if (decoded is not KeyValuePair<Vector2i, ulong>[] pairs)
            throw new InvalidDataException("Expected a Vector2i/ulong pair array.");

        value = new Dictionary<Vector2i, ulong>(pairs.Length);
        try
        {
            foreach (var pair in pairs)
                value.Add(pair.Key, pair.Value);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Invalid dictionary contents", e);
        }
    }

    private sealed class ArrayTypeId(Serializer serializer)
    {
        public readonly uint Value = serializer.GetTypeMap()[typeof(KeyValuePair<Vector2i, ulong>[])];
    }
}
