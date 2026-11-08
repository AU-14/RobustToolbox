using NetSerializer;
using Robust.Shared.IoC;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading; // CMU14
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization
{
    internal abstract partial class RobustSerializer : IRobustSerializerInternal
    {
        [Dependency] private IReflectionManager _reflectionManager = default!;
        [Dependency] protected IRobustMappedStringSerializer MappedStringSerializer = default!;
        [Dependency] private ILogManager _logManager = default!;

        private readonly Dictionary<Type, Dictionary<string, Type?>> _cachedSerialized = new();

        private ISawmill LogSzr = default!;


        private Serializer _serializer = default!;

        private HashSet<Type> _serializableTypes = default!;
        private bool _initialized;
        private SerializerFloatFlags _floatFlags;

        private static Type[] AlwaysNetSerializable => new[]
        {
            typeof(Vector2i)
        };

        #region Statistics

        private readonly object _statsLock = new();

        // CMU14 Begin: independent counters need no shared monitor on every parallel state write.
        private long _largestObjectSerializedBytes;
        private Type? _largestObjectSerializedType;
        private long _bytesSerialized;
        private long _objectsSerialized;
        private long _largestObjectDeserializedBytes;
        private Type? _largestObjectDeserializedType;
        private long _bytesDeserialized;
        private long _objectsDeserialized;

        public long LargestObjectSerializedBytes => Volatile.Read(ref _largestObjectSerializedBytes);
        public Type? LargestObjectSerializedType => Volatile.Read(ref _largestObjectSerializedType);
        public long BytesSerialized => Volatile.Read(ref _bytesSerialized);
        public long ObjectsSerialized => Volatile.Read(ref _objectsSerialized);
        public long LargestObjectDeserializedBytes => Volatile.Read(ref _largestObjectDeserializedBytes);
        public Type? LargestObjectDeserializedType => Volatile.Read(ref _largestObjectDeserializedType);
        public long BytesDeserialized => Volatile.Read(ref _bytesDeserialized);
        public long ObjectsDeserialized => Volatile.Read(ref _objectsDeserialized);
        // CMU14 End

        #endregion

        public SerializerFloatFlags FloatFlags
        {
            get => _floatFlags;
            set
            {
                if (_initialized)
                    throw new InvalidOperationException("Already initialized!");

                _floatFlags = value;
            }
        }

        public void Initialize()
        {
            if (_initialized)
                throw new InvalidOperationException("Already initialized!");

            _initialized = true;

            var types = _reflectionManager.FindTypesWithAttribute<NetSerializableAttribute>()
                .OrderBy(x => x.FullName, StringComparer.InvariantCulture)
                .ToList();

#if DEBUG
            // confirm only shared types are marked for serialization, no client & server only types
            foreach (var type in types)
            {
                if (type.Assembly.FullName!.Contains("Server"))
                {
                    throw new InvalidOperationException($"Type {type} is server specific but has a NetSerializableAttribute!");
                }

                if (type.Assembly.FullName.Contains("Client"))
                {
                    throw new InvalidOperationException($"Type {type} is client specific but has a NetSerializableAttribute!");
                }
            }
#endif

            LogSzr = _logManager.GetSawmill("szr");
            types.AddRange(AlwaysNetSerializable);
            types.Add(typeof(Vector2));

            MappedStringSerializer.Initialize();

            var settings = new Settings
            {
                CustomTypeSerializers = new[]
                {
                    MappedStringSerializer.TypeSerializer,
                    new NetMathSerializer(),
                    new NetBitArraySerializer(),
                    new NetComponentDeltaStateSerializer(),
                    new NetFormattedStringSerializer(),
                    new NetUnsafeFloatSerializer(),
                    new NetVector2iUlongDictionarySerializer(), // CMU14: allocation-free roof dictionary writes, same wire format.
                }
            };

            if ((_floatFlags & SerializerFloatFlags.RemoveReadNan) != 0)
            {
                settings.CustomTypeSerializers =
                [
                    ..settings.CustomTypeSerializers,
                    // This replaces NetSerializer's default serializer.
                    new NetSafeFloatSerializer()
                ];
            }

            _serializer = new Serializer(types, settings);
            _serializableTypes = new HashSet<Type>(_serializer.GetTypeMap().Keys);
            LogSzr.Info($"Serializer Types Hash: {_serializer.GetSHA256()}");
        }

        public byte[] GetSerializableTypesHash() => Convert.FromHexString(_serializer.GetSHA256());
        public string GetSerializableTypesHashString() => _serializer.GetSHA256();

        internal void GetHashManifest(Stream stream, bool writeNewline=false)
        {
            _serializer.GetHashManifest(stream, writeNewline);
        }

        public (byte[] Hash, byte[] Package) GetStringSerializerPackage() => MappedStringSerializer.GeneratePackage();

        public Dictionary<Type, uint> GetTypeMap() => _serializer.GetTypeMap();

        public void Serialize(Stream stream, object toSerialize)
        {
            var start = StartMeasureStats(stream);
            _serializer.Serialize(stream, toSerialize);
            EndMeasureSerialize(stream, start, toSerialize.GetType());
        }

        public void SerializeDirect<T>(Stream stream, T toSerialize)
        {
            DebugTools.Assert(toSerialize == null || typeof(T) == toSerialize.GetType(),
                "Object must be of exact type specified in the generic parameter.");

            var start = StartMeasureStats(stream);
            _serializer.SerializeDirect(stream, toSerialize);
            EndMeasureSerialize(stream, start, typeof(T));
        }

        public T Deserialize<T>(Stream stream)
            => (T) Deserialize(stream);

        public void DeserializeDirect<T>(Stream stream, out T value)
        {
            var start = StartMeasureStats(stream);
            _serializer.DeserializeDirect(stream, out value);
            EndMeasureDeserialize(stream, start, typeof(T));
        }

        public object Deserialize(Stream stream)
        {
            var start = StartMeasureStats(stream);
            var result = _serializer.Deserialize(stream);
            EndMeasureDeserialize(stream, start, result.GetType());

            return result;
        }

        public bool CanSerialize(Type type)
            => _serializableTypes.Contains(type);

        /// <inheritdoc />
        public Type? FindSerializedType(Type assignableType, string serializedTypeName)
        {
            if (!_cachedSerialized.TryGetValue(assignableType, out var assigned))
            {
                assigned = new Dictionary<string, Type?>();
                _cachedSerialized[assignableType] = assigned;
            }

            if (assigned.TryGetValue(serializedTypeName, out var resolved))
                return resolved;

            assigned[serializedTypeName] = null;
            return null;
        }

        private static long StartMeasureStats(Stream stream)
        {
            return stream.CanSeek ? stream.Position : 0;
        }

        // CMU14 Begin: only a new maximum requires coordination of its size and type.
        private void EndMeasureDeserialize(Stream stream, long start, Type type)
        {
            Interlocked.Increment(ref _objectsDeserialized);
            if (!stream.CanSeek)
                return;

            var byteCount = stream.Position - start;
            Interlocked.Add(ref _bytesDeserialized, byteCount);
            if (byteCount <= Volatile.Read(ref _largestObjectDeserializedBytes))
                return;

            lock (_statsLock)
            {
                if (byteCount > _largestObjectDeserializedBytes)
                {
                    _largestObjectDeserializedType = type;
                    Volatile.Write(ref _largestObjectDeserializedBytes, byteCount);
                }
            }
        }

        private void EndMeasureSerialize(Stream stream, long start, Type type)
        {
            Interlocked.Increment(ref _objectsSerialized);
            if (!stream.CanSeek)
                return;

            var byteCount = stream.Position - start;
            Interlocked.Add(ref _bytesSerialized, byteCount);
            if (byteCount <= Volatile.Read(ref _largestObjectSerializedBytes))
                return;

            lock (_statsLock)
            {
                if (byteCount > _largestObjectSerializedBytes)
                {
                    _largestObjectSerializedType = type;
                    Volatile.Write(ref _largestObjectSerializedBytes, byteCount);
                }
            }
        }
        // CMU14 End
    }
}
