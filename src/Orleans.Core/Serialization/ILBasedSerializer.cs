namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using Microsoft.Extensions.Options;
    using System.Reflection;
    using Orleans.Runtime;

    /// <summary>
    /// Options for <see cref="ILBasedSerializer"/>.
    /// </summary>
    public class ILBasedSerializerOptions
    {
        /// <summary>
        /// Whether to use the <see cref="ILBasedSerializer"/> serializer only as a fallback
        /// </summary>
        public bool IsFallbackOnly { get; set; } = true;
    }

    /// <summary>
    /// Fallback serializer to be used when other serializers are unavailable.
    /// </summary>
    public class ILBasedSerializer : IKeyedSerializer
    {
        private static readonly Type ExceptionType = typeof(Exception);
        private static readonly Type TypeType = typeof(Type);

        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly ILSerializerGenerator generator = new ILSerializerGenerator();

        /// <summary>
        /// The collection of generated serializers.
        /// </summary>
        private readonly ConcurrentDictionary<Type, SerializerBundle> serializers =
            new ConcurrentDictionary<Type, SerializerBundle>();
        private readonly ILBasedSerializerOptions options;
        private readonly TypeSerializer typeSerializer;

        /// <summary>
        /// The serializer used when a concrete type is not known.
        /// </summary>
        private readonly SerializerBundle thisSerializer;

        /// <summary>
        /// The serializer used for implementations of <see cref="Type"/>.
        /// </summary>
        private readonly SerializerBundle namedTypeSerializer;

        private readonly SerializerBundle exceptionSerializer;

        private readonly Func<Type, SerializerBundle> generateSerializer;

        public ILBasedSerializer(ITypeResolver typeResolver, IOptions<ILBasedSerializerOptions> options)
        {
            this.options = options.Value;
            this.typeSerializer = new TypeSerializer(typeResolver);
            var fallbackExceptionSerializer = new ILBasedExceptionSerializer(this.generator, this.typeSerializer);
            this.exceptionSerializer = new SerializerBundle(
                new SerializerMethods(
                    fallbackExceptionSerializer.DeepCopy,
                    fallbackExceptionSerializer.Serialize,
                    fallbackExceptionSerializer.Deserialize));
            
            // Configure the serializer to be used when a concrete type is not known.
            // The serializer will generate and register serializers for concrete types
            // as they are discovered.
            this.thisSerializer = new SerializerBundle(
                new SerializerMethods(
                    this.DeepCopy,
                    this.Serialize,
                    this.Deserialize));

            this.namedTypeSerializer = new SerializerBundle(
                new SerializerMethods(
                    (original, context) => original,
                    (original, writer, expected) => {
                        var writer1 = writer.StreamWriter;
                        this.typeSerializer.WriteNamedType((Type)original, writer1);
                    },
                    (expected, reader) =>
                    {
                        var reader1 = reader.StreamReader;
                        return this.typeSerializer.ReadNamedType(reader1);
                    }));
            this.generateSerializer = this.GenerateSerializer;
        }

        /// <summary>
        /// Informs the serialization manager whether this serializer supports the type for serialization.
        /// </summary>
        /// <param name="type">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the item can be serialized.</returns>
        public bool IsSupportedType(Type type) => IsSupportedType(type, isFallback: false);

        /// <inheritdoc />
        public bool IsSupportedType(Type type, bool isFallback)
        {
            // Either the type has opted-in to using this serializer, or this is fallback serialization and this serializer thinks it can serialize this type.
            var optIn = type.GetCustomAttribute<EnableKeyedSerializerAttribute>() is { } attr && typeof(ILBasedSerializer).Equals(attr.SerializerType);
            if (optIn) return true;

            // If this isn't being called in the context of fallback serialization, then only allow serialization if this is not configured as a fallback-only serializer.
            if (!isFallback && IsFallbackOnly)
            {
                return false;
            }

            var isSupported = this.serializers.ContainsKey(type) || ILSerializerGenerator.IsSupportedType(type);
            return isSupported;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null) return null;
            Type type = source.GetType();
            return this.serializers.GetOrAdd(type, this.generateSerializer).Methods.DeepCopy(source, context);
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (item == null)
            {
                context.StreamWriter.Write((byte)ILSerializerTypeToken.Null);
                return;
            }

            var actualType = item.GetType();
            this.WriteType(actualType, expectedType, context);
            this.serializers.GetOrAdd(actualType, this.generateSerializer).Methods.Serialize(item, context, expectedType);
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var token = (ILSerializerTypeToken)reader.ReadByte();
            if (token == ILSerializerTypeToken.Null) return null;
            var actualType = this.ReadType(token, context, expectedType);
            return this.serializers.GetOrAdd(actualType, this.generateSerializer)
                       .Methods.Deserialize(expectedType, context);
        }

        private void WriteType(Type actualType, Type expectedType, ISerializationContext context)
        {
            if (ExceptionType.IsAssignableFrom(actualType))
            {
                // Exceptions are always serialized using a special-purpose serializer, even if the actual and expected
                // types match. That serializer also writes its own type header.
                context.StreamWriter.Write((byte) ILSerializerTypeToken.Exception);
            }
            else if (actualType == expectedType)
            {
                context.StreamWriter.Write((byte) ILSerializerTypeToken.ExpectedType);
            }
            else
            {
                context.StreamWriter.Write((byte) ILSerializerTypeToken.NamedType);
                this.typeSerializer.WriteNamedType(actualType, context.StreamWriter);
            }
        }

        private Type ReadType(ILSerializerTypeToken token, IDeserializationContext context, Type expectedType)
        {
            switch (token)
            {
                case ILSerializerTypeToken.ExpectedType:
                    return expectedType;
                case ILSerializerTypeToken.NamedType:
                    return this.typeSerializer.ReadNamedType(context.StreamReader);
                case ILSerializerTypeToken.Exception:
                    return ExceptionType;
                default:
                    throw new NotSupportedException($"{nameof(ILSerializerTypeToken)} of {token} is not supported.");
            }
        }

        private SerializerBundle GenerateSerializer(Type type)
        {
            if (type.IsGenericTypeDefinition) return this.thisSerializer;

            if (TypeType.IsAssignableFrom(type))
            {
                return this.namedTypeSerializer;
            }
            
            if (ExceptionType.IsAssignableFrom(type))
            {
                return this.exceptionSerializer;
            }

            return new SerializerBundle(this.generator.GenerateSerializer(type));
        }

        private enum ILSerializerTypeToken : byte
        {
            Null,
            ExpectedType,
            NamedType,
            Exception,
        }

        /// <summary>
        /// This class primarily exists as a means to hold a reference to a <see cref="SerializerMethods"/> structure.
        /// </summary>
        private class SerializerBundle
        {
            public readonly SerializerMethods Methods;
            
            public SerializerBundle(SerializerMethods methods)
            {
                this.Methods = methods;
            }
        }

        /// <inheritdoc />
        KeyedSerializerId IKeyedSerializer.SerializerId => KeyedSerializerId.ILBasedSerializer;

        /// <inheritdoc />
        public bool IsFallbackOnly => options.IsFallbackOnly;
    }

}