﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using BinarySerialization.Graph.ValueGraph;

namespace BinarySerialization.Graph.TypeGraph
{
    internal abstract class TypeNode : Node
    {
        public static readonly Dictionary<Type, SerializedType> DefaultSerializedTypes =
            new Dictionary<Type, SerializedType>
            {
                {typeof (bool), SerializedType.Int1},
                {typeof (sbyte), SerializedType.Int1},
                {typeof (byte), SerializedType.UInt1},
                {typeof (char), SerializedType.UInt2},
                {typeof (short), SerializedType.Int2},
                {typeof (ushort), SerializedType.UInt2},
                {typeof (int), SerializedType.Int4},
                {typeof (uint), SerializedType.UInt4},
                {typeof (long), SerializedType.Int8},
                {typeof (ulong), SerializedType.UInt8},
                {typeof (float), SerializedType.Float4},
                {typeof (double), SerializedType.Float8},
                {typeof (string), SerializedType.NullTerminatedString},
                {typeof (byte[]), SerializedType.ByteArray}
            };

        private readonly SerializedType? _serializedType;

        protected TypeNode(TypeNode parent)
            : base(parent)
        {
        }

        protected TypeNode(TypeNode parent, Type type)
            : this(parent)
        {
            Type = type;
            NullableUnderlyingType = Nullable.GetUnderlyingType(Type);
        }

        protected TypeNode(TypeNode parent, Type parentType, MemberInfo memberInfo, Type subType = null)
            : this(parent)
        {
            if (memberInfo == null)
                return;

            MemberInfo = memberInfo;

            Name = memberInfo.Name;

            var propertyInfo = memberInfo as PropertyInfo;
            var fieldInfo = memberInfo as FieldInfo;

            if (propertyInfo != null)
            {
                Type = subType ?? propertyInfo.PropertyType;

                ValueGetter = MagicMethods.MagicFunc(parentType, propertyInfo.GetGetMethod());

                var setMethod = propertyInfo.GetSetMethod();

                if (setMethod != null)
                    ValueSetter = MagicMethods.MagicAction(parentType, setMethod);
            }
            else if (fieldInfo != null)
            {
                Type = subType ?? fieldInfo.FieldType;

                ValueGetter = fieldInfo.GetValue;
                ValueSetter = fieldInfo.SetValue;
            }
            else throw new NotSupportedException($"{memberInfo.GetType().Name} not supported");

            NullableUnderlyingType = Nullable.GetUnderlyingType(Type);

            var attributes = memberInfo.GetCustomAttributes(true);

            IsIgnored = attributes.OfType<IgnoreAttribute>().Any();

            /* Don't go any further if we're ignoring this. */
            if (IsIgnored)
                return;

            var fieldOrderAttribute = attributes.OfType<FieldOrderAttribute>().SingleOrDefault();
            if (fieldOrderAttribute != null)
                Order = fieldOrderAttribute.Order;

            var serializeAsAttribute = attributes.OfType<SerializeAsAttribute>().SingleOrDefault();
            if (serializeAsAttribute != null)
            {
                _serializedType = serializeAsAttribute.SerializedType;

#pragma warning disable 618
                Endianness = serializeAsAttribute.Endianness;
#pragma warning restore 618

#pragma warning disable 618
                if (!string.IsNullOrEmpty(serializeAsAttribute.Encoding))
                    Encoding = Encoding.GetEncoding(serializeAsAttribute.Encoding);
#pragma warning restore 618

                if (_serializedType.Value == SerializedType.NullTerminatedString)
                    AreStringsNullTerminated = true;
            }

            IsNullable = NullableUnderlyingType != null;
            if (!IsNullable)
            {
                var serializedType = GetSerializedType();
                IsNullable = serializedType == SerializedType.Default ||
                             serializedType == SerializedType.ByteArray ||
                             serializedType == SerializedType.NullTerminatedString ||
                             serializedType == SerializedType.SizedString;
            }

            // setup bindings
            FieldLengthBindings = GetBindings<FieldLengthAttribute>(attributes);
            FieldCountBindings = GetBindings<FieldCountAttribute>(attributes);
            FieldOffsetBindings = GetBindings<FieldOffsetAttribute>(attributes);
            FieldEndiannessBindings = GetBindings<FieldEndiannessAttribute>(attributes);
            FieldEncodingBindings = GetBindings<FieldEncodingAttribute>(attributes);

            var fieldAlignmentAttributes = attributes.OfType<FieldAlignmentAttribute>().ToLookup(attribute => attribute.Mode);
            var leftAlignmentAttributes =
                fieldAlignmentAttributes[FieldAlignmentMode.LeftAndRight].Concat(
                    fieldAlignmentAttributes[FieldAlignmentMode.LeftOnly]);

            var rightAlignmentAttributes =
                fieldAlignmentAttributes[FieldAlignmentMode.LeftAndRight].Concat(
                    fieldAlignmentAttributes[FieldAlignmentMode.RightOnly]);

            LeftFieldAlignmentBindings = GetBindings<FieldAlignmentAttribute>(leftAlignmentAttributes.Cast<object>().ToArray());
            RightFieldAlignmentBindings = GetBindings<FieldAlignmentAttribute>(rightAlignmentAttributes.Cast<object>().ToArray());

            var fieldValueAttributes = attributes.OfType<FieldValueAttributeBase>().ToArray();
            FieldValueAttributes = new ReadOnlyCollection<FieldValueAttributeBase>(fieldValueAttributes);

            if (FieldValueAttributes.Count > 0)
            {
                FieldValueBindings = GetBindings<FieldValueAttributeBase>(attributes);
            }

            var serializeWhenAttributes = attributes.OfType<SerializeWhenAttribute>().ToArray();
            SerializeWhenAttributes = new ReadOnlyCollection<SerializeWhenAttribute>(serializeWhenAttributes);

            if (SerializeWhenAttributes.Count > 0)
            {
                SerializeWhenBindings = new ReadOnlyCollection<ConditionalBinding>(
                    serializeWhenAttributes.Select(
                        attribute => new ConditionalBinding(attribute, GetBindingLevel(attribute.Binding))).ToList());
            }

            var serializeWhenNotAttributes = attributes.OfType<SerializeWhenNotAttribute>().ToArray();
            SerializeWhenNotAttributes = new ReadOnlyCollection<SerializeWhenNotAttribute>(serializeWhenNotAttributes);

            if (SerializeWhenNotAttributes.Count > 0)
            {
                SerializeWhenNotBindings = new ReadOnlyCollection<ConditionalBinding>(
                    serializeWhenNotAttributes.Select(
                        attribute => new ConditionalBinding(attribute, GetBindingLevel(attribute.Binding))).ToList());
            }

            // don't inherit subtypes if this is itself a subtype
            if (subType == null)
            {
                var subtypeAttributes = attributes.OfType<SubtypeAttribute>().Cast<SubtypeBaseAttribute>().ToArray();

                SubtypeAttributes = new ReadOnlyCollection<SubtypeBaseAttribute>(subtypeAttributes);
                SubtypeBinding = GetBinding(subtypeAttributes, Type);

                SubtypeDefaultAttribute = attributes.OfType<SubtypeDefaultAttribute>().SingleOrDefault();

                if (SubtypeDefaultAttribute != null)
                {
                    if (!Type.IsAssignableFrom(SubtypeDefaultAttribute.Subtype))
                        throw new InvalidOperationException($"{SubtypeDefaultAttribute.Subtype} is not a subtype of {Type}");
                }
            }

            var itemSubtypeAttributes = attributes.OfType<ItemSubtypeAttribute>().Cast<SubtypeBaseAttribute>().ToArray();
            ItemSubtypeAttributes = new ReadOnlyCollection<SubtypeBaseAttribute>(itemSubtypeAttributes);

            if (itemSubtypeAttributes.Length > 0)
            {
                Type itemBaseType;

                if (Type.IsArray)
                {
                    itemBaseType = Type.GetElementType();
                }
                else if (typeof (IList).IsAssignableFrom(Type))
                {
                    var genericArguments = Type.GetGenericArguments();
                    if (genericArguments.Length > 1)
                    {
                        throw new InvalidOperationException("Multiple generic arguments not supported.");
                    }

                    itemBaseType = genericArguments[0];
                }
                else
                {
                    throw new InvalidOperationException("ItemSubtype can only be used with collections.");
                }

                ItemSubtypeBinding = GetBinding(itemSubtypeAttributes, itemBaseType);
                ItemSubtypeDefaultAttribute = attributes.OfType<ItemSubtypeDefaultAttribute>().SingleOrDefault();
            }

            SerializeUntilAttribute = attributes.OfType<SerializeUntilAttribute>().SingleOrDefault();
            if (SerializeUntilAttribute != null)
            {
                SerializeUntilBinding = new Binding(SerializeUntilAttribute,
                    GetBindingLevel(SerializeUntilAttribute.Binding));
            }

            ItemLengthBindings = GetBindings<ItemLengthAttribute>(attributes);

            ItemSerializeUntilAttribute = attributes.OfType<ItemSerializeUntilAttribute>().SingleOrDefault();

            if (ItemSerializeUntilAttribute != null)
            {
                ItemSerializeUntilBinding = new Binding(ItemSerializeUntilAttribute,
                    GetBindingLevel(ItemSerializeUntilAttribute.Binding));
            }
        }

        private BindingCollection GetBindings<TAttribute>(IEnumerable<object> attributes) 
            where TAttribute : FieldBindingBaseAttribute
        {
            var typeAttributes = attributes.OfType<TAttribute>().ToList();

            if (!typeAttributes.Any())
                return null;

            var bindings =
                typeAttributes.Select(
                    attribute =>
                        new Binding(attribute, GetBindingLevel(attribute.Binding)));

            return new BindingCollection(bindings);
        }

        private Binding GetBinding(SubtypeBaseAttribute[] attributes, Type checkType)
        {
            if (attributes.Length == 0)
                return null;

            var bindingGroups =
                attributes.GroupBy(subtypeAttribute => subtypeAttribute.Binding);

            if (bindingGroups.Count() > 1)
                throw new BindingException("Subtypes must all specify the same binding configuration.");

            var firstBinding = attributes[0];
            var binding = new Binding(firstBinding, GetBindingLevel(firstBinding.Binding));

            var valueGroups = attributes.GroupBy(attribute => attribute.Value);
            if (valueGroups.Count() < attributes.Length)
                throw new InvalidOperationException("Subtype values must be unique.");

            if (binding.BindingMode == BindingMode.TwoWay)
            {
                var subTypeGroups = attributes.GroupBy(attribute => attribute.Subtype);
                if (subTypeGroups.Count() < attributes.Length)
                    throw new InvalidOperationException(
                        "Subtypes must be unique for two-way subtype bindings.  Set BindingMode to OneWay to disable updates to the binding source during serialization.");
            }

            var invalidSubtype =
                attributes.FirstOrDefault(attribute => !checkType.IsAssignableFrom(attribute.Subtype));

            if (invalidSubtype != null)
                throw new InvalidOperationException($"{invalidSubtype.Subtype} is not a subtype of {checkType}");

            return binding;
        }



        public MemberInfo MemberInfo { get; }
        public Type Type { get; }
        public Type NullableUnderlyingType { get; }

        public Type BaseSerializedType => NullableUnderlyingType ?? Type;

        public Action<object, object> ValueSetter { get; }
        public Func<object, object> ValueGetter { get; }

        public BindingCollection FieldLengthBindings { get; }
        public BindingCollection ItemLengthBindings { get; }
        public BindingCollection FieldCountBindings { get; }
        public BindingCollection FieldOffsetBindings { get; }
        public BindingCollection LeftFieldAlignmentBindings { get; }
        public BindingCollection RightFieldAlignmentBindings { get; }
        public BindingCollection FieldEndiannessBindings { get; }
        public BindingCollection FieldEncodingBindings { get; }
        public BindingCollection FieldValueBindings { get; }

        public Binding SerializeUntilBinding { get; }
        public Binding ItemSerializeUntilBinding { get; }
        public Binding SubtypeBinding { get; }
        public Binding ItemSubtypeBinding { get; }

        public ReadOnlyCollection<ConditionalBinding> SerializeWhenBindings { get; }
        public ReadOnlyCollection<ConditionalBinding> SerializeWhenNotBindings { get; }
        public ReadOnlyCollection<FieldValueAttributeBase> FieldValueAttributes { get; }
        public ReadOnlyCollection<SubtypeBaseAttribute> SubtypeAttributes { get; }
        public SubtypeDefaultAttribute SubtypeDefaultAttribute { get; }
        public ReadOnlyCollection<SubtypeBaseAttribute> ItemSubtypeAttributes { get; }
        public ItemSubtypeDefaultAttribute ItemSubtypeDefaultAttribute { get; }
        public ReadOnlyCollection<SerializeWhenAttribute> SerializeWhenAttributes { get; }
        public ReadOnlyCollection<SerializeWhenNotAttribute> SerializeWhenNotAttributes { get; }
        public SerializeUntilAttribute SerializeUntilAttribute { get; }
        public ItemSerializeUntilAttribute ItemSerializeUntilAttribute { get; }
        public Endianness? Endianness { get; }
        public Encoding Encoding { get; }

        public bool IsIgnored { get; }

        public int? Order { get; }

        public bool AreStringsNullTerminated { get; }

        public bool IsNullable { get; }

        public SerializedType GetSerializedType(Type referenceType = null)
        {
            if (referenceType == null)
                referenceType = BaseSerializedType;

            SerializedType serializedType;
            if (_serializedType != null && _serializedType.Value != SerializedType.Default)
                serializedType = _serializedType.Value;
            else if (!DefaultSerializedTypes.TryGetValue(referenceType, out serializedType))
                return SerializedType.Default;

            // handle special cases within null terminated strings
            if (serializedType == SerializedType.NullTerminatedString)
            {
                // If null terminated string is specified but field length is present, override
                if (FieldLengthBindings != null)
                    serializedType = SerializedType.SizedString;

                // If null terminated string is specified but item field length is present, override
                var parent = (TypeNode)Parent;
                if (parent.ItemLengthBindings != null)
                    serializedType = SerializedType.SizedString;
            }

            return serializedType;
        }

        public ValueNode CreateSerializer(ValueNode parent)
        {
            try
            {
                return CreateSerializerOverride(parent);
            }
            catch (Exception e)
            {
                var reference = Name == null
                    ? $"type '{Type}'"
                    : $"member '{Name}'";
                var message = $"Error serializing {reference}.  See inner exception for detail.";
                throw new InvalidOperationException(message, e);
            }
        }

        public abstract ValueNode CreateSerializerOverride(ValueNode parent);

        public int GetBindingLevel(BindingInfo binding)
        {
            var level = 0;

            switch (binding.RelativeSourceMode)
            {
                case RelativeSourceMode.Self:
                    level = 1;
                    break;
                case RelativeSourceMode.FindAncestor:
                case RelativeSourceMode.SerializationContext:
                    level = FindAncestorLevel(binding);
                    break;
            }

            return level;
        }

        public static bool IsValueType(Type type)
        {
            return type.GetTypeInfo().IsPrimitive || type == typeof (string) || type == typeof (byte[]);
        }

        protected Func<object> CreateCompiledConstructor()
        {
            return CreateCompiledConstructor(Type);
        }

        protected static Func<object> CreateCompiledConstructor(Type type)
        {
            if (type == typeof (string))
                return () => string.Empty;

            var constructor = type.GetConstructor(new Type[0]);
            return CreateCompiledConstructor(constructor);
        }

        protected static Func<object> CreateCompiledConstructor(ConstructorInfo constructor)
        {
            if (constructor == null)
                return null;

            return Expression.Lambda<Func<object>>(Expression.New(constructor)).Compile();
        }

        private int FindAncestorLevel(BindingInfo binding)
        {
            var level = 1;
            var parent = (TypeNode) Parent;
            while (parent != null)
            {
                if (binding != null && binding.RelativeSourceMode == RelativeSourceMode.FindAncestor)
                {
                    if (binding.AncestorLevel == level)
                    {
                        return level;
                    }

                    if(binding.AncestorType != null && parent.Type != null && 
                        binding.AncestorType.IsAssignableFrom(parent.Type))
                    {
                        return level;
                    }
                }

                parent = (TypeNode) parent.Parent;
                level++;
            }

            if (binding != null && binding.RelativeSourceMode == RelativeSourceMode.SerializationContext)
                return level;

            throw new BindingException("No ancestor found.");
        }
    }
}