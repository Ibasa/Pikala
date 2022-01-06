using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    struct DeserializeInformation
    {
        public Type StaticType { get; }
        public bool ShouldMemo { get; }
        public PickledTypeInfo? ContextType { get; }

        public DeserializeInformation(Type staticType, bool shouldMemo, PickledTypeInfo? contextType = null)
        {
            StaticType = staticType;
            ShouldMemo = shouldMemo;
            ContextType = contextType;
        }

        public bool NeedsOperationToken { get { return StaticType.IsValueType; } }
    }

    public sealed partial class Pickler
    {
        private static DeserializeInformation AssemblyInfo = new DeserializeInformation(typeof(Assembly), true);
        private static DeserializeInformation TypeInfo = new DeserializeInformation(typeof(Type), true);
        private static DeserializeInformation ObjectInfo = new DeserializeInformation(typeof(object), true);

        private DeserializeInformation MakeInfo(Type staticType, PickledTypeInfo? contextType = null)
        {
            return new DeserializeInformation(staticType, !staticType.IsValueType, contextType);
        }

        private static object ReadEnumerationValue(BinaryReader reader, TypeCode typeCode)
        {
            switch (typeCode)
            {
                case TypeCode.SByte:
                    return reader.ReadSByte();
                case TypeCode.Int16:
                    return reader.ReadInt16();
                case TypeCode.Int32:
                    return reader.ReadInt32();
                case TypeCode.Int64:
                    return reader.ReadInt64();

                case TypeCode.Byte:
                    return reader.ReadByte();
                case TypeCode.UInt16:
                    return reader.ReadUInt16();
                case TypeCode.UInt32:
                    return reader.ReadUInt32();
                case TypeCode.UInt64:
                    return reader.ReadUInt64();
            }

            throw new Exception($"Invalid type code '{typeCode}' for enumeration");
        }

        private static Type TypeFromTypeCode(TypeCode typeCode)
        {
            switch (typeCode)
            {
                case TypeCode.SByte:
                    return typeof(sbyte);
                case TypeCode.Int16:
                    return typeof(short);
                case TypeCode.Int32:
                    return typeof(int);
                case TypeCode.Int64:
                    return typeof(long);

                case TypeCode.Byte:
                    return typeof(byte);
                case TypeCode.UInt16:
                    return typeof(ushort);
                case TypeCode.UInt32:
                    return typeof(uint);
                case TypeCode.UInt64:
                    return typeof(ulong);
            }

            throw new NotImplementedException($"Unhandled type code '{typeCode}' for TypeFromTypeCode");
        }

        private SignatureElement DeserializeSignatureElement(PicklerDeserializationState state)
        {
            var operation = (SignatureElementOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case SignatureElementOperation.Type:
                    {
                        var type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, null, null);
                        return new SignatureType(type.Type);
                    }

                case SignatureElementOperation.TVar:
                case SignatureElementOperation.MVar:
                    return new SignatureGenericParameter(operation == SignatureElementOperation.TVar, state.Reader.Read7BitEncodedInt());

                case SignatureElementOperation.Generic:
                    {
                        var genericTypeDefinition = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, null, null);
                        var genericArguments = new SignatureElement[state.Reader.Read7BitEncodedInt()];
                        for (int i = 0; i < genericArguments.Length; ++i)
                        {
                            genericArguments[i] = DeserializeSignatureElement(state);
                        }
                        return new SignatureConstructedGenericType(genericTypeDefinition.Type, genericArguments);
                    }

                case SignatureElementOperation.Array:
                    {
                        var rank = state.Reader.Read7BitEncodedInt();
                        var elementType = DeserializeSignatureElement(state);
                        return new SignatureArray(rank, elementType);
                    }

                case SignatureElementOperation.ByRef:
                    {
                        var elementType = DeserializeSignatureElement(state);
                        return new SignatureByRef(elementType);
                    }
            }

            throw new NotImplementedException($"Unhandled SignatureElement: {operation}");
        }


        private Signature DeserializeSignature(PicklerDeserializationState state)
        {
            var name = state.Reader.ReadString();
            var genericParameterCount = state.Reader.Read7BitEncodedInt();
            var returnType = DeserializeSignatureElement(state);
            var parameters = new SignatureElement[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < parameters.Length; ++i)
            {
                parameters[i] = DeserializeSignatureElement(state);
            }
            return new Signature(name, genericParameterCount, returnType, parameters);
        }

        private void DeserializeConstructorHeader(PicklerDeserializationState state, Type[]? genericTypeParameters, PickledTypeInfoDef constructingType, out PickledConstructorInfoDef constructingConstructor)
        {
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var callingConvention = (CallingConventions)state.Reader.ReadInt32();

            var parameterCount = state.Reader.Read7BitEncodedInt();
            Type[]? parameterTypes = null;
            if (parameterCount != 0)
            {
                parameterTypes = new Type[parameterCount];
                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, null);
                    parameterTypes[j] = parameterType.Type;
                }
            }

            var typeBuilder = constructingType.TypeBuilder;
            var constructorBuilder = typeBuilder.DefineConstructor(methodAttributes, callingConvention, parameterTypes);

            ParameterBuilder[]? parameters = null;
            if (parameterTypes != null)
            {
                parameters = new ParameterBuilder[parameterCount];
                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterName = state.Reader.ReadNullableString();
                    var parameterAttributes = (ParameterAttributes)state.Reader.ReadInt32();
                    parameters[j] = constructorBuilder.DefineParameter(1 + j, parameterAttributes, parameterName);
                }
            }

            constructorBuilder.InitLocals = state.Reader.ReadBoolean();

            var locals = new PickledTypeInfo[state.Reader.Read7BitEncodedInt()];
            for (int j = 0; j < locals.Length; ++j)
            {
                // We can't actually DECLARE locals here, so store them on the ConstructingMethod till construction time
                locals[j] = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, null);
            }

            constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, parameterTypes, locals);

            var usedTypes = state.Reader.Read7BitEncodedInt();
            for (int j = 0; j < usedTypes; ++j)
            {
                // We can just discared the type here, we just need it in the stream before the method body is done
                DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, null);
            }
        }

        private void DeserializeMethodHeader(PicklerDeserializationState state, Type[]? genericTypeParameters, PickledTypeInfoDef constructingType, ref PickledMethodInfoDef constructingMethod)
        {
            var methodName = state.Reader.ReadString();
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var methodImplAttributes = (MethodImplAttributes)state.Reader.ReadInt32();
            var callingConventions = (CallingConventions)state.Reader.ReadInt32();
            var typeBuilder = constructingType.TypeBuilder;
            var methodBuilder = typeBuilder.DefineMethod(methodName, methodAttributes, callingConventions);
            constructingMethod = new PickledMethodInfoDef(constructingType, methodBuilder);

            var methodGenericParameterCount = state.Reader.Read7BitEncodedInt();
            if (methodGenericParameterCount != 0)
            {
                var methodGenericParameterNames = new string[methodGenericParameterCount];
                for (int j = 0; j < methodGenericParameterCount; ++j)
                {
                    methodGenericParameterNames[j] = state.Reader.ReadString();
                }
                constructingMethod.GenericParameters = methodBuilder.DefineGenericParameters(methodGenericParameterNames);
            }

            var returnType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, constructingMethod.GenericParameters);
            var parameterCount = state.Reader.Read7BitEncodedInt();
            if (parameterCount != 0)
            {
                constructingMethod.ParameterTypes = new Type[parameterCount];
                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, constructingMethod.GenericParameters);
                    constructingMethod.ParameterTypes[j] = parameterType.Type;
                }

                methodBuilder.SetSignature(returnType.Type, null, null, constructingMethod.ParameterTypes, null, null);

                constructingMethod.Parameters = new ParameterBuilder[parameterCount];
                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var parameterName = state.Reader.ReadNullableString();
                    var parameterAttributes = (ParameterAttributes)state.Reader.ReadInt32();
                    constructingMethod.Parameters[j] = methodBuilder.DefineParameter(1 + j, parameterAttributes, parameterName);
                }
            }
            else
            {
                methodBuilder.SetSignature(returnType.Type, null, null, null, null, null);
            }

            methodBuilder.SetImplementationFlags(methodImplAttributes);

            if (methodAttributes.HasFlag(MethodAttributes.PinvokeImpl) || methodAttributes.HasFlag(MethodAttributes.UnmanagedExport) || methodAttributes.HasFlag(MethodAttributes.Abstract))
            {

            }
            else
            {
                methodBuilder.InitLocals = state.Reader.ReadBoolean();

                constructingMethod.Locals = new PickledTypeInfo[state.Reader.Read7BitEncodedInt()];
                for (int j = 0; j < constructingMethod.Locals.Length; ++j)
                {
                    // We can't actually DECLARE locals here, so store them on the ConstructingMethod till construction time
                    constructingMethod.Locals[j] = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, constructingMethod.GenericParameters);
                }

                var usedTypes = state.Reader.Read7BitEncodedInt();
                for (int j = 0; j < usedTypes; ++j)
                {
                    // We can just discared the type here, we just need it in the stream before the method body is done
                    Deserialize(state, TypeInfo, genericTypeParameters, constructingMethod.GenericParameters);
                }
            }
        }

        private void DeserializeMethodBody(PicklerDeserializationState state, Type[]? genericTypeParameters, Type[]? genericMethodParameters, PickledTypeInfo[] locals, ILGenerator ilGenerator)
        {
            // Now it should be safe to declare locals
            foreach (var local in locals)
            {
                ilGenerator.DeclareLocal(local.Type);
            }

            var ilLabels = new Dictionary<int, Label>();
            Label GetLabel(int offset)
            {
                if (ilLabels.TryGetValue(offset, out var label))
                {
                    return label;
                }
                label = ilGenerator.DefineLabel();
                ilLabels[offset] = label;
                return label;
            }

            while (true)
            {
                ilGenerator.MarkLabel(GetLabel(ilGenerator.ILOffset));

                var opCodeByte = state.Reader.ReadByte();
                OpCode opCode;
                if (opCodeByte == 0xFF)
                {
                    break;
                }
                else if (opCodeByte == 0xfe)
                {
                    opCode = _twoByteOpCodes[state.Reader.ReadByte()];
                }
                else
                {
                    opCode = _oneByteOpCodes[opCodeByte];
                }

                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        {
                            ilGenerator.Emit(opCode);
                            break;
                        }

                    case OperandType.InlineSwitch:
                        {
                            Label[] labels = new Label[state.Reader.ReadInt32()];
                            // These targets are represented as offsets (positive or negative) from the beginning of the instruction following this switch instruction.
                            var offset = ilGenerator.ILOffset + opCode.Size + 4 + 4 * labels.Length;
                            for (int i = 0; i < labels.Length; ++i)
                            {
                                var target = state.Reader.ReadInt32();
                                labels[i] = GetLabel(offset + target);
                            }
                            ilGenerator.Emit(opCode, labels);
                            break;
                        }

                    case OperandType.InlineSig:
                        {
                            throw new NotImplementedException("InlineSig not yet deserialisable");
                        }

                    case OperandType.InlineTok:
                        {
                            var memberInfo = DeserializeNonNull<PickledMemberInfo>(state, new DeserializeInformation(typeof(MemberInfo), true), genericTypeParameters, genericMethodParameters);
                            memberInfo.Emit(ilGenerator, opCode);
                            break;
                        }

                    case OperandType.InlineType:
                        {
                            var typeInfo = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                            ilGenerator.Emit(opCode, typeInfo.Type);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldInfo = DeserializeNonNull<PickledFieldInfo>(state, new DeserializeInformation(typeof(FieldInfo), true), genericTypeParameters, genericMethodParameters);
                            ilGenerator.Emit(opCode, fieldInfo.FieldInfo);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodBase = DeserializeNonNull<PickledMethodBase>(state, new DeserializeInformation(typeof(MethodBase), true), genericTypeParameters, genericMethodParameters);
                            methodBase.Emit(ilGenerator, opCode);
                            break;
                        }

                    case OperandType.InlineString:
                        {
                            var stringValue = state.Reader.ReadString();
                            ilGenerator.Emit(opCode, stringValue);
                            break;
                        }

                    case OperandType.ShortInlineI:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadByte());
                            break;
                        }

                    case OperandType.InlineI:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadInt32());
                            break;
                        }

                    case OperandType.InlineI8:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadInt64());
                            break;
                        }

                    case OperandType.ShortInlineR:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadSingle());
                            break;
                        }

                    case OperandType.InlineR:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadDouble());
                            break;
                        }

                    case OperandType.ShortInlineVar:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadByte());
                            break;
                        }

                    case OperandType.InlineVar:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadInt16());
                            break;
                        }

                    case OperandType.ShortInlineBrTarget:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadByte());
                            break;
                        }

                    case OperandType.InlineBrTarget:
                        {
                            ilGenerator.Emit(opCode, state.Reader.ReadInt32());
                            break;
                        }


                    default:
                        throw new NotImplementedException($"Unknown OpCode.OperandType {opCode.OperandType}");
                }
            }
        }

        private void DeserializeTypeDefComplex(PicklerDeserializationState state, PickledTypeInfoDef constructingType)
        {
            var isValueType = constructingType.TypeDef == TypeDef.Struct;
            var typeBuilder = constructingType.TypeBuilder;

            if (!isValueType)
            {
                var baseType = Deserialize<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                if (baseType != null)
                {
                    typeBuilder.SetParent(baseType.Type);
                }
            }

            var interfaceCount = state.Reader.Read7BitEncodedInt();
            var interfaceMap = new List<(PickledMethodInfo, Signature)>();
            for (int i = 0; i < interfaceCount; ++i)
            {
                var interfaceType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                typeBuilder.AddInterfaceImplementation(interfaceType.Type);

                var mapCount = state.Reader.Read7BitEncodedInt();
                for (int j = 0; j < mapCount; ++j)
                {
                    var interfaceMethodSignature = DeserializeSignature(state);
                    var targetMethodSignature = DeserializeSignature(state);
                    var interfaceMethod = interfaceType.GetMethod(interfaceMethodSignature);
                    interfaceMap.Add((interfaceMethod, targetMethodSignature));
                }
            }

            var fieldCount = state.Reader.Read7BitEncodedInt();
            constructingType.Fields = new PickledFieldInfoDef[fieldCount];
            for (int i = 0; i < fieldCount; ++i)
            {
                var fieldName = state.Reader.ReadString();
                var fieldAttributes = (FieldAttributes)state.Reader.ReadInt32();
                var fieldType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                var fieldBuilder = typeBuilder.DefineField(fieldName, fieldType.Type, fieldAttributes);
                constructingType.Fields[i] = new PickledFieldInfoDef(constructingType, fieldBuilder);
            }

            var constructorCount = state.Reader.Read7BitEncodedInt();
            constructingType.Constructors = new PickledConstructorInfoDef[constructorCount];
            for (int i = 0; i < constructorCount; ++i)
            {
                DeserializeConstructorHeader(state, constructingType.GenericParameters, constructingType, out constructingType.Constructors[i]);
            }

            var methodCount = state.Reader.Read7BitEncodedInt();
            constructingType.Methods = new PickledMethodInfoDef[methodCount];
            for (int i = 0; i < methodCount; ++i)
            {
                DeserializeMethodHeader(state, constructingType.GenericParameters, constructingType, ref constructingType.Methods[i]);
            }

            foreach (var (interfaceMethod, targetMethodSignature) in interfaceMap)
            {
                MethodInfo? targetMethod = null;
                foreach (var method in constructingType.Methods)
                {
                    if (targetMethodSignature == method.GetSignature())
                    {
                        targetMethod = method.MethodBuilder;
                        break;
                    }
                }

                if (targetMethod == null)
                {
                    throw new Exception($"Could not find {typeBuilder}.{targetMethodSignature}");
                }

                typeBuilder.DefineMethodOverride(targetMethod, interfaceMethod.MethodInfo);
            }

            MethodBuilder GetMethod(Signature signature)
            {
                var info = constructingType.GetMethod(signature);
                // If we had covariant returns this cast wouldn't be needed.
                var def = (PickledMethodInfoDef)info;
                return def.MethodBuilder;
            }

            var propertyCount = state.Reader.Read7BitEncodedInt();
            constructingType.Properties = new PickledPropertyInfoDef[propertyCount];
            for (int i = 0; i < propertyCount; ++i)
            {
                var propertyName = state.Reader.ReadString();
                var propertyAttributes = (PropertyAttributes)state.Reader.ReadInt32();
                var propertyType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                var propertyParametersCount = state.Reader.Read7BitEncodedInt();
                var propertyParameters = new Type[propertyParametersCount];
                for (int j = 0; j < propertyParametersCount; ++j)
                {
                    propertyParameters[j] = (DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null)).Type;
                }

                var propertyBuilder = typeBuilder.DefineProperty(propertyName, propertyAttributes, propertyType.Type, propertyParameters);
                constructingType.Properties[i] = new PickledPropertyInfoDef(constructingType, propertyBuilder, propertyParameters);

                var count = state.Reader.Read7BitEncodedInt();
                var hasGetter = (count & 0x1) != 0;
                var hasSetter = (count & 0x2) != 0;
                var otherCount = count >> 2;

                if (hasGetter)
                {
                    propertyBuilder.SetGetMethod(GetMethod(DeserializeSignature(state)));
                }
                if (hasSetter)
                {
                    propertyBuilder.SetSetMethod(GetMethod(DeserializeSignature(state)));
                }
                for (var j = 0; j < otherCount; ++j)
                {
                    propertyBuilder.AddOtherMethod(GetMethod(DeserializeSignature(state)));
                }
            }

            var eventCount = state.Reader.Read7BitEncodedInt();
            constructingType.Events = new PickledEventInfoDef[eventCount];
            for (int i = 0; i < eventCount; ++i)
            {
                var eventName = state.Reader.ReadString();
                var eventAttributes = (EventAttributes)state.Reader.ReadInt32();
                var eventType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);


                var eventBuilder = typeBuilder.DefineEvent(eventName, eventAttributes, eventType.Type);
                constructingType.Events[i] = new PickledEventInfoDef(constructingType, eventBuilder, eventName);

                var count = state.Reader.Read7BitEncodedInt();
                var hasRaiser = (count & 0x1) != 0;
                var otherCount = count >> 1;

                eventBuilder.SetAddOnMethod(GetMethod(DeserializeSignature(state)));
                eventBuilder.SetRemoveOnMethod(GetMethod(DeserializeSignature(state)));

                if (hasRaiser)
                {
                    eventBuilder.SetRaiseMethod(GetMethod(DeserializeSignature(state)));
                }
                for (var j = 0; j < otherCount; ++j)
                {
                    eventBuilder.AddOtherMethod(GetMethod(DeserializeSignature(state)));
                }
            }

            state.PushTrailer(() =>
            {
                ReadCustomAttributes(state, constructingType.TypeBuilder.SetCustomAttribute);

                foreach (var field in constructingType.Fields)
                {
                    ReadCustomAttributes(state, field.FieldBuilder.SetCustomAttribute);
                }
                foreach (var constructor in constructingType.Constructors)
                {
                    var constructorBuilder = constructor.ConstructorBuilder;
                    ReadCustomAttributes(state, constructorBuilder.SetCustomAttribute);

                    var ilGenerator = constructorBuilder.GetILGenerator();
                    DeserializeMethodBody(state, constructingType.GenericParameters, null, constructor.Locals!, ilGenerator);
                }
                foreach (var method in constructingType.Methods)
                {
                    var methodBuilder = method.MethodBuilder;
                    ReadCustomAttributes(state, methodBuilder.SetCustomAttribute);
                    if (methodBuilder.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || methodBuilder.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || methodBuilder.Attributes.HasFlag(MethodAttributes.Abstract))
                    {

                    }
                    else
                    {
                        var ilGenerator = methodBuilder.GetILGenerator();
                        DeserializeMethodBody(state, constructingType.GenericParameters, method.GenericParameters, method.Locals!, ilGenerator);
                    }
                }
                foreach (var property in constructingType.Properties)
                {
                    ReadCustomAttributes(state, property.PropertyBuilder.SetCustomAttribute);
                }
                foreach (var evt in constructingType.Events)
                {
                    ReadCustomAttributes(state, evt.EventBuilder.SetCustomAttribute);
                }

                constructingType.FullyDefined = true;

            },
            () => constructingType.CreateType(),
            () =>
            {
                var type = constructingType.Type;

                var staticFields =
                    type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsInitOnly)
                    .ToArray();

                for (int i = 0; i < staticFields.Length; ++i)
                {
                    var fieldName = state.Reader.ReadString();
                    FieldInfo? fieldInfo = null;
                    for (int j = 0; j < staticFields.Length; ++j)
                    {
                        if (fieldName == staticFields[j].Name)
                        {
                            fieldInfo = staticFields[j];
                        }
                    }

                    if (fieldInfo == null)
                    {
                        throw new Exception($"Could not find static field '{fieldName}' on type '{type.Name}'");
                    }

                    var deserInfo = new DeserializeInformation(typeof(object), !fieldInfo.FieldType.IsValueType);
                    var fieldValue = ReducePickle(Deserialize(state, deserInfo, null, null));
                    fieldInfo.SetValue(null, fieldValue);
                }
            });
        }

        private PickledTypeInfoDef ConstructingTypeForTypeDef(TypeDef typeDef, string typeName, TypeAttributes typeAttributes, Func<string, TypeAttributes, Type?, TypeBuilder> defineType)
        {

            switch (typeDef)
            {
                case TypeDef.Enum:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(Enum)));

                case TypeDef.Delegate:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(MulticastDelegate)));

                case TypeDef.Struct:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(ValueType)));

                case TypeDef.Class:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, null));

                default:
                    throw new Exception($"Unrecgonized TypeDef: {typeDef}");
            }
        }

        private void DeserializeTypeDef(PicklerDeserializationState state, PickledTypeInfoDef constructingType)
        {
            if (constructingType.TypeDef == TypeDef.Enum)
            {
                var typeCode = (TypeCode)state.Reader.ReadByte();
                var underlyingType = TypeFromTypeCode(typeCode);
                var typeBuilder = constructingType.TypeBuilder;
                typeBuilder.DefineField("value__", underlyingType, FieldAttributes.Private | FieldAttributes.SpecialName);

                var count = state.Reader.Read7BitEncodedInt();
                for (int i = 0; i < count; ++i)
                {
                    var name = state.Reader.ReadString();
                    var value = ReadEnumerationValue(state.Reader, typeCode);

                    FieldBuilder enumerationField = typeBuilder.DefineField(name, typeBuilder, FieldAttributes.Public | FieldAttributes.Literal | FieldAttributes.Static);
                    enumerationField.SetConstant(value);
                }

                ReadCustomAttributes(state, typeBuilder.SetCustomAttribute);

                constructingType.FullyDefined = true;

                state.PushTrailer(null, () => constructingType.CreateType(), null);
            }
            else if (constructingType.TypeDef == TypeDef.Delegate)
            {
                var typeBuilder = constructingType.TypeBuilder;

                var constructorParameters = new[] { typeof(object), typeof(IntPtr) };
                var constructorBuilder = typeBuilder.DefineConstructor(
                    MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                    CallingConventions.Standard, constructorParameters);
                constructorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime);
                var parameters = new ParameterBuilder[] {
                    constructorBuilder.DefineParameter(1, ParameterAttributes.None, "object"),
                    constructorBuilder.DefineParameter(2, ParameterAttributes.None, "method"),
                };

                var constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, constructorParameters, null);
                constructingType.Constructors = new PickledConstructorInfoDef[] { constructingConstructor };

                var returnType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                var parameterCount = state.Reader.Read7BitEncodedInt();
                var parameterNames = new string[parameterCount];
                var parameterTypes = new Type[parameterCount];
                for (int i = 0; i < parameterCount; ++i)
                {
                    parameterNames[i] = state.Reader.ReadString();
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, constructingType.GenericParameters, null);
                    parameterTypes[i] = parameterType.Type;
                }

                var invokeMethod = typeBuilder.DefineMethod(
                    "Invoke", MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Public,
                    returnType.Type, parameterTypes);
                invokeMethod.SetImplementationFlags(MethodImplAttributes.Runtime);
                var constructingMethod = new PickledMethodInfoDef(constructingType, invokeMethod);
                constructingMethod.ParameterTypes = parameterTypes;
                constructingMethod.Parameters = new ParameterBuilder[parameterTypes.Length];
                for (int i = 0; i < parameterCount; i++)
                {
                    constructingMethod.Parameters[i] = invokeMethod.DefineParameter(i + 1, ParameterAttributes.None, parameterNames[i]);
                }
                constructingType.Methods = new PickledMethodInfoDef[] { constructingMethod };

                constructingType.FullyDefined = true;

                state.PushTrailer(null, () => constructingType.CreateType(), null);
            }
            else
            {
                DeserializeTypeDefComplex(state, constructingType);
            }
        }

        private Array DeserializeArray(PicklerDeserializationState state, bool isSZArray, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var pickledTypeInfo = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
            var elementType = pickledTypeInfo.Type;

            Array array;
            if (isSZArray)
            {
                var length = state.Reader.Read7BitEncodedInt();
                array = Array.CreateInstance(elementType, length);
                state.SetMemo(position, true, array);
            }
            else
            {
                var rank = state.Reader.ReadByte();
                var lengths = new int[rank];
                var lowerBounds = new int[rank];
                for (int dimension = 0; dimension < rank; ++dimension)
                {
                    lengths[dimension] = state.Reader.Read7BitEncodedInt();
                    lowerBounds[dimension] = state.Reader.Read7BitEncodedInt();
                }
                array = Array.CreateInstance(elementType, lengths, lowerBounds);
                state.SetMemo(position, true, array);
            }

            // If this is a primitive type just block copy it across to the stream, excepting endianness (Which dotnet
            // currently only supports little endian anyway, and mono on big endian is probably a fringe use?) this is
            // safe. We could extend this to also consider product types with static layout but:
            // A) Currently I don't think any mscorlib types are defined with a strict layout so they would never hit this
            // B) We wouldn't do this for user defined type because they might change layout been write and read
            if (elementType.IsPrimitive)
            {
                unsafe
                {
                    var arrayHandle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        var pin = arrayHandle.AddrOfPinnedObject();
                        // TODO We should just use Unsafe.SizeOf here but that's a net5.0 addition
                        long byteCount;
                        if (elementType == typeof(bool))
                        {
                            byteCount = array.LongLength;
                        }
                        else if (elementType == typeof(char))
                        {
                            byteCount = 2 * array.LongLength;
                        }
                        else
                        {
                            byteCount = System.Runtime.InteropServices.Marshal.SizeOf(elementType) * array.LongLength;
                        }

                        while (byteCount > 0)
                        {
                            // Read 4k at a time
                            var bufferSize = 4096L;
                            var length = (int)(byteCount < bufferSize ? byteCount : bufferSize);

                            var span = new Span<byte>(pin.ToPointer(), length);
                            state.Reader.Read(span);

                            pin = IntPtr.Add(pin, length);
                            byteCount -= length;
                        }
                    }
                    finally
                    {
                        arrayHandle.Free();

                    }
                }
            }
            else
            {
                var elementInfo = new DeserializeInformation(elementType, !elementType.IsValueType);

                if (isSZArray)
                {
                    for (int index = 0; index < array.Length; ++index)
                    {
                        array.SetValue(ReducePickle(Deserialize(state, elementInfo, genericTypeParameters, genericMethodParameters)), index);
                    }
                }
                else
                {
                    var indices = new int[array.Rank];
                    bool isEmpty = false;
                    for (int dimension = 0; dimension < array.Rank; ++dimension)
                    {
                        indices[dimension] = array.GetLowerBound(dimension);
                        isEmpty |= array.GetLength(dimension) == 0;
                    }

                    // If the array is empty (any length == 0) no need to loop
                    if (!isEmpty)
                    {
                        var didBreak = true;
                        while (didBreak)
                        {
                            // The first time we call into Iterate we know the array is non-empty, and indices is equal to lowerBounds (i.e the first element)
                            // If we reach the last element we don't call back into Iterate

                            var item = ReducePickle(Deserialize(state, elementInfo, genericTypeParameters, genericMethodParameters));
                            array.SetValue(item, indices);

                            // Increment indices to the next position, we work through the dimensions backwards because that matches the order that GetEnumerator returns when we serialise out the items
                            didBreak = false;
                            for (int dimension = array.Rank - 1; dimension >= 0; --dimension)
                            {
                                var next = indices[dimension] + 1;
                                if (next < array.GetLowerBound(dimension) + array.GetLength(dimension))
                                {
                                    indices[dimension] = next;
                                    didBreak = true;
                                    break;
                                }
                                else
                                {
                                    indices[dimension] = array.GetLowerBound(dimension);
                                }
                            }
                        }
                    }
                }
            }
            return array;
        }

        private object DeserializeTuple(PicklerDeserializationState state, bool isValueTuple, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var length = state.Reader.ReadByte();
            // if length == 0 short circuit to just return a new ValueTuple
            if (length == 0)
            {
                System.Diagnostics.Debug.Assert(isValueTuple, "Tuple length was zero but it wasn't a value tuple");
                return new ValueTuple();
            }

            var genericParameters = new Type[length];
            var genericArguments = new Type[length];
            for (int i = 0; i < length; ++i)
            {
                var pickledTypeInfo = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                genericArguments[i] = pickledTypeInfo.Type;
                genericParameters[i] = Type.MakeGenericMethodParameter(i);
            }

            var items = new object?[length];
            for (int i = 0; i < length; ++i)
            {
                items[i] = ReducePickle(Deserialize(state, MakeInfo(genericArguments[i]), genericTypeParameters, genericMethodParameters));
            }

            var tupleType = isValueTuple ? typeof(System.ValueTuple) : typeof(System.Tuple);
            var openCreateMethod = tupleType.GetMethod("Create", length, genericParameters);
            System.Diagnostics.Debug.Assert(openCreateMethod != null, "GetMethod for Tuple.Create returned null");
            var closedCreateMethod = openCreateMethod.MakeGenericMethod(genericArguments);
            var tupleObject = closedCreateMethod.Invoke(null, items);
            System.Diagnostics.Debug.Assert(tupleObject != null, "Tuple.Create returned null");

            return tupleObject;
        }

        private object DeserializeISerializable(PicklerDeserializationState state, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var memberCount = state.Reader.Read7BitEncodedInt();

            var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
            var info = new System.Runtime.Serialization.SerializationInfo(type, new System.Runtime.Serialization.FormatterConverter());

            for (int i = 0; i < memberCount; ++i)
            {
                var name = state.Reader.ReadString();
                var value = ReducePickle(Deserialize(state, MakeInfo(typeof(object)), genericTypeParameters, genericMethodParameters));
                info.AddValue(name, value);
            }

            var ctor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.DefaultBinder,
                new[] { typeof(System.Runtime.Serialization.SerializationInfo), typeof(System.Runtime.Serialization.StreamingContext) }, null);

            if (ctor == null)
            {
                throw new Exception($"Could not deserialize type '{type}' expected a constructor (SerializationInfo, StreamingContext)");
            }

            var result = ctor.Invoke(new object[] { info, context });

            if (result is System.Runtime.Serialization.IDeserializationCallback deserializationCallback)
            {
                deserializationCallback.OnDeserialization(this);
            }

            return result;
        }

        private object DeserializeReducer(PicklerDeserializationState state, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var method = Deserialize<PickledMethodBase>(state, MakeInfo(typeof(MethodBase)), genericTypeParameters, genericMethodParameters);

            object? target;
            if (method is PickledConstructorInfo)
            {
                target = null;
            }
            else if (method is PickledMethodInfo)
            {
                target = ReducePickle(Deserialize(state, MakeInfo(typeof(object)), genericTypeParameters, genericMethodParameters));
            }
            else
            {
                throw new Exception($"Invalid reduction MethodBase was '{method}'.");
            }

            var args = new object?[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < args.Length; ++i)
            {
                var arg = ReducePickle(Deserialize(state, MakeInfo(typeof(object)), genericTypeParameters, genericMethodParameters));
                args[i] = arg;
            }

            var result = method.Invoke(target, args);
            if (result == null)
            {
                throw new Exception($"Invalid reducer method, '{method}' returned null.");
            }
            return result;
        }

        private void DeserializeObject(PicklerDeserializationState state, object uninitializedObject, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var fields = GetSerializedFields(type);
            var fieldCount = state.Reader.Read7BitEncodedInt();
            if (fields.Length != fieldCount)
            {
                throw new Exception($"Can not deserialize type '{type}', serialised {fieldCount} fields but type expects {fields.Length}");
            }

            for (int i = 0; i < fieldCount; ++i)
            {
                var name = state.Reader.ReadString();
                FieldInfo? toSet = null;
                foreach (var field in fields)
                {
                    if (field.Name == name)
                    {
                        toSet = field;
                        break;
                    }
                }

                if (toSet == null)
                {
                    throw new Exception($"Can not deserialize type '{type}', could not find expected field '{name}'");
                }

                var deserInfo = new DeserializeInformation(typeof(object), !toSet.FieldType.IsValueType);
                object? value = ReducePickle(Deserialize(state, deserInfo, genericTypeParameters, genericMethodParameters));

                toSet.SetValue(uninitializedObject, value);
            }
        }

        // TODO It would be good to only return PickledObject things as part of typedef construction and not have that recurse through Deserialize
        [return: NotNullIfNotNull("obj")]
        private object? ReducePickle(object? obj)
        {
            if (obj is PickledObject pickledObject)
            {
                return pickledObject.Get();
            }
            return obj;
        }

        private PickledFieldInfo DeserializeFieldInfo(PicklerDeserializationState state, long position, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var type = info.ContextType;
                if (type == null)
                {
                    type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                }
                var name = state.Reader.ReadString();
                return state.SetMemo(position, true, type.GetField(name));
            });
        }

        private PickledPropertyInfo DeserializePropertyInfo(PicklerDeserializationState state, long position, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var type = info.ContextType;
                if (type == null)
                {
                    type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                }
                var signature = DeserializeSignature(state);
                return state.SetMemo(position, true, type.GetProperty(signature));
            });
        }

        private PickledEventInfo DeserializeEventInfo(PicklerDeserializationState state, long position, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var type = info.ContextType;
                if (type == null)
                {
                    type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                }
                var name = state.Reader.ReadString();
                return state.SetMemo(position, true, type.GetEvent(name));
            });
        }

        private PickledConstructorInfo DeserializeConstructorInfo(PicklerDeserializationState state, long position, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var type = info.ContextType;
                if (type == null)
                {
                    type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                }
                var signature = DeserializeSignature(state);
                return state.SetMemo(position, true, type.GetConstructor(signature));
            });
        }

        private PickledMethodInfo DeserializeMethodInfo(PicklerDeserializationState state, long position, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var type = info.ContextType;
                if (type == null)
                {
                    type = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                }
                var signature = DeserializeSignature(state);
                var genericArgumentCount = state.Reader.Read7BitEncodedInt();
                PickledTypeInfo[]? genericArguments = null;
                if (genericArgumentCount != 0)
                {
                    genericArguments = new PickledTypeInfo[genericArgumentCount];
                    for (int i = 0; i < genericArgumentCount; ++i)
                    {
                        genericArguments[i] = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                    }
                }

                var methodInfo = type.GetMethod(signature);

                if (genericArguments != null)
                {
                    return state.SetMemo(position, true, new ConstructingGenericMethod(methodInfo, genericArguments));
                }
                return state.SetMemo(position, true, methodInfo);
            });
        }

        private object DeserializeDelegate(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var objType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
            var invocationList = new Delegate[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < invocationList.Length; ++i)
            {
                var target = Deserialize(state, ObjectInfo, genericTypeParameters, genericMethodParameters);
                var info = new DeserializeInformation(typeof(MethodInfo), true);
                var method = DeserializeNonNull<PickledMethodInfo>(state, info, genericTypeParameters, genericMethodParameters);
                invocationList[i] = Delegate.CreateDelegate(objType.Type, target, method.MethodInfo);
            }
            return state.SetMemo(position, true, Delegate.Combine(invocationList));
        }

        private Assembly DeserializeAsesmblyRef(PicklerDeserializationState state, long position)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            // Check to see if its already in our loaded assembly set
            Assembly? assembly = null;
            foreach (var candidate in AssemblyLoadContext.Assemblies)
            {
                if (candidate.FullName == assemblyName.FullName)
                {
                    if (assembly != null)
                    {
                        throw new Exception($"Ambiguous assembly name '{assemblyName}', found multiple matching assemblies.");
                    }
                    assembly = candidate;
                }
            }
            // Else try to load it
            if (assembly == null)
            {
                assembly = AssemblyLoadContext.LoadFromAssemblyName(assemblyName);
            }
            return state.SetMemo(position, ShouldMemo(assembly, typeof(Assembly)), assembly);
        }

        private void ReadCustomAttributes(PicklerDeserializationState state, Action<CustomAttributeBuilder> setCustomAttribute)
        {
            var attributeCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < attributeCount; ++i)
            {
                var attributeType = DeserializeNonNull<PickledTypeInfo>(state, MakeInfo(typeof(Type)), null, null);

                var constructor = DeserializeNonNull<PickledConstructorInfo>(state, MakeInfo(typeof(ConstructorInfo), attributeType), null, null);
                var arguments = new object?[state.Reader.Read7BitEncodedInt()];
                for (int j = 0; j < arguments.Length; ++j)
                {
                    arguments[j] = ReducePickle(Deserialize(state, ObjectInfo, null, null));
                }

                var namedProperties = new PropertyInfo[state.Reader.Read7BitEncodedInt()];
                var propertyValues = new object?[namedProperties.Length];
                for (int j = 0; j < namedProperties.Length; ++j)
                {
                    var propertyInfo = DeserializeNonNull<PickledPropertyInfo>(state, MakeInfo(typeof(PropertyInfo), attributeType), null, null);
                    namedProperties[j] = propertyInfo.PropertyInfo;
                    var deserInfo = new DeserializeInformation(typeof(object), !namedProperties[j].PropertyType.IsValueType);
                    propertyValues[j] = ReducePickle(Deserialize(state, deserInfo, null, null));
                }

                var namedFields = new FieldInfo[state.Reader.Read7BitEncodedInt()];
                var fieldValues = new object?[namedFields.Length];
                for (int j = 0; j < namedFields.Length; ++j)
                {
                    var pickledField = DeserializeNonNull<PickledFieldInfo>(state, MakeInfo(typeof(FieldInfo), attributeType), null, null);
                    namedFields[j] = pickledField.FieldInfo;
                    var deserInfo = new DeserializeInformation(typeof(object), !pickledField.FieldInfo.FieldType.IsValueType);
                    fieldValues[j] = ReducePickle(Deserialize(state, deserInfo, null, null));
                }

                var customBuilder = new CustomAttributeBuilder(constructor.ConstructorInfo, arguments, namedProperties, propertyValues, namedFields, fieldValues);

                setCustomAttribute(customBuilder);
            }
        }

        private Func<AssemblyName, AssemblyBuilderAccess, AssemblyBuilder>? _defineDynamicAssembly = null;

        private static Guid _ddaGuid = new Guid("75468177-0C74-489F-967D-AC6BAB6BA7A4");
        private static object _ddaLock = new object();

        private Assembly? LookupWorkaroundAssembly(System.Runtime.Loader.AssemblyLoadContext alc)
        {
            foreach (var assembly in alc.Assemblies)
            {
                var name = assembly.GetName();
                if (name.Name == "DefineDynamicAssembly" && assembly.ManifestModule.ModuleVersionId == _ddaGuid)
                {
                    return assembly;
                }
            }
            return null;
        }

        private Assembly BuildAndLoadWorkaroundAssembly(System.Runtime.Loader.AssemblyLoadContext alc)
        {
            var contentId = new System.Reflection.Metadata.BlobContentId(_ddaGuid, 0x04030201);

            var ilBuilder = new System.Reflection.Metadata.BlobBuilder();
            var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();

            metadata.AddAssembly(
                metadata.GetOrAddString("DefineDynamicAssembly"),
                new Version(1, 0, 0, 0),
                default(System.Reflection.Metadata.StringHandle),
                default(System.Reflection.Metadata.BlobHandle),
                flags: 0,
                AssemblyHashAlgorithm.None);

            metadata.AddModule(
                0,
                metadata.GetOrAddString("DefineDynamicAssembly.dll"),
                metadata.GetOrAddGuid(_ddaGuid),
                default(System.Reflection.Metadata.GuidHandle),
                default(System.Reflection.Metadata.GuidHandle));

            var mscorlibName = mscorlib.GetName();
            var mscorlibPublicKey = metadata.GetOrAddBlob(mscorlibName.GetPublicKey());

            var mscorlibAssemblyRef = metadata.AddAssemblyReference(
                metadata.GetOrAddString(mscorlibName.Name),
                mscorlibName.Version,
                metadata.GetOrAddString(mscorlibName.CultureName),
                mscorlibPublicKey,
                default(AssemblyFlags),
                default(System.Reflection.Metadata.BlobHandle));

            var assemblyBuilderRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection.Emit"),
                metadata.GetOrAddString("AssemblyBuilder"));

            var assemblyNameRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection"),
                metadata.GetOrAddString("AssemblyName"));

            var assemblyBuilderAccessRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection.Emit"),
                metadata.GetOrAddString("AssemblyBuilderAccess"));

            var ddaSignature = new System.Reflection.Metadata.BlobBuilder();
            new System.Reflection.Metadata.Ecma335.BlobEncoder(ddaSignature)
                .MethodSignature()
                .Parameters(2,
                    returnType => returnType.Type().Type(assemblyBuilderRef, false),
                    parameters =>
                    {
                        parameters.AddParameter().Type().Type(assemblyNameRef, false);
                        parameters.AddParameter().Type().Type(assemblyBuilderAccessRef, true);
                    });

            var defineDynamicAssemblyRef = metadata.AddMemberReference(
                assemblyBuilderRef,
                metadata.GetOrAddString("DefineDynamicAssembly"),
                metadata.GetOrAddBlob(ddaSignature));

            var codeBuilder = new System.Reflection.Metadata.BlobBuilder();
            var il = new System.Reflection.Metadata.Ecma335.InstructionEncoder(codeBuilder);
            il.LoadArgument(0);
            il.LoadArgument(1);
            il.Call(defineDynamicAssemblyRef);
            il.OpCode(System.Reflection.Metadata.ILOpCode.Ret);

            var methodBodyStream = new System.Reflection.Metadata.Ecma335.MethodBodyStreamEncoder(ilBuilder);
            var ddaOffset = methodBodyStream.AddMethodBody(il);

            var parameters = metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("name"), 0);
            metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("access"), 1);

            var ddaMethodDef = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL,
                metadata.GetOrAddString("DefineDynamicAssembly"),
                metadata.GetOrAddBlob(ddaSignature),
                ddaOffset,
                parameterList: parameters);

            metadata.AddTypeDefinition(
                default(TypeAttributes),
                default(System.Reflection.Metadata.StringHandle),
                metadata.GetOrAddString("<Module>"),
                baseType: default(System.Reflection.Metadata.EntityHandle),
                fieldList: System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1),
                methodList: ddaMethodDef);

            var metadataRootBuilder = new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata);

            var peHeaderBuilder = new System.Reflection.PortableExecutable.PEHeaderBuilder();
            var peBuilder = new System.Reflection.PortableExecutable.ManagedPEBuilder(
                peHeaderBuilder,
                metadataRootBuilder,
                ilBuilder,
                flags: System.Reflection.PortableExecutable.CorFlags.ILOnly,
                deterministicIdProvider: content => contentId);

            var builder = new System.Reflection.Metadata.BlobBuilder();
            peBuilder.Serialize(builder);

            var memoryStream = new System.IO.MemoryStream();
            builder.WriteContentTo(memoryStream);
            memoryStream.Position = 0;

            return alc.LoadFromStream(memoryStream);
        }

        private Assembly DeserializeAsesmblyDef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            var access = AssemblyLoadContext.IsCollectible ? AssemblyBuilderAccess.RunAndCollect : AssemblyBuilderAccess.Run;
            var currentContextualReflectionContextOrDefault =
                System.Runtime.Loader.AssemblyLoadContext.CurrentContextualReflectionContext ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            AssemblyBuilder assembly;
            if (AssemblyLoadContext == currentContextualReflectionContextOrDefault)
            {
                // If the assembly load context is the current contextual one then we can just call DefineDynamicAssembly.
                assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, access);

            }
            else if (Environment.Version.Major >= 6)
            {
                // Else for runtime 6.0 onwards we can set our AssemblyLoadContext as the current contextual context and then call DefineDynamicAssembly
                using var scope = AssemblyLoadContext.EnterContextualReflection();
                assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, access);
            }
            else
            {
                // Else before net6 DefineDynamicAssembly did not check the contextual ALC, it instead used the callers ALC. So to get around this we do some "fun" here
                // where we build a tiny assembly with one method to call AssemblyBuilder.DefineDynamicAssembly, load that into the ALC we want to use then invoke
                // the method on it. We can reuse this method, so we cache it via a Func.
                if (_defineDynamicAssembly == null)
                {
                    lock (_ddaLock)
                    {
                        if (_defineDynamicAssembly == null)
                        {
                            var ddaAssembly = LookupWorkaroundAssembly(AssemblyLoadContext) ?? BuildAndLoadWorkaroundAssembly(AssemblyLoadContext);
                            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(ddaAssembly);
                            System.Diagnostics.Debug.Assert(context == AssemblyLoadContext, "Failed to load into defined ALC");
                            var ddaMethod = ddaAssembly.ManifestModule.GetMethod("DefineDynamicAssembly");
                            System.Diagnostics.Debug.Assert(ddaMethod != null, "Failed to GetMethod(\"DefineDynamicAssembly\")");
                            var ddaDelegate = ddaMethod.CreateDelegate(typeof(Func<AssemblyName, AssemblyBuilderAccess, AssemblyBuilder>));
                            _defineDynamicAssembly = (Func<AssemblyName, AssemblyBuilderAccess, AssemblyBuilder>)ddaDelegate;
                        }
                    }
                }
                assembly = _defineDynamicAssembly(assemblyName, access);
            }

            if (assembly == null)
            {
                throw new Exception($"Could not define assembly '{assemblyName}'");
            }

            state.SetMemo(position, true, assembly);


            ReadCustomAttributes(state, assembly.SetCustomAttribute);
            return assembly;
        }

        private Module DeserializeManifestModuleRef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var assembly = DeserializeNonNull<Assembly>(state, AssemblyInfo, genericTypeParameters, genericMethodParameters);
            return state.SetMemo(position, ShouldMemo(assembly, typeof(Module)), assembly.ManifestModule);
        }

        private Module DeserializeModuleRef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var name = state.Reader.ReadString();
            var assembly = DeserializeNonNull<Assembly>(state, AssemblyInfo, genericTypeParameters, genericMethodParameters);
            var module = assembly.GetModule(name);
            if (module == null)
            {
                throw new Exception($"Could not load module '{name}' from assembly '{assembly}'");
            }
            return state.SetMemo(position, true, module);
        }

        private ModuleBuilder DeserializeModuleDef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var name = state.Reader.ReadString();
                var (callback, _) = DeserializeWithMemo(state, position, (AssemblyBuilder assembly) =>
                {
                    var module = assembly.DefineDynamicModule(name);
                    if (module == null)
                    {
                        throw new Exception($"Could not create module '{name}' in assembly '{assembly}'");
                    }
                    return state.SetMemo(position, true, module);
                }, AssemblyInfo, genericTypeParameters, genericMethodParameters);
                var moduleBuilder = callback.Invoke();

                ReadCustomAttributes(state, moduleBuilder.SetCustomAttribute);

                var fieldCount = state.Reader.Read7BitEncodedInt();
                var fields = new PickledFieldInfoDef[fieldCount];
                for (int i = 0; i < fieldCount; ++i)
                {
                    var fieldName = state.Reader.ReadString();
                    var fieldAttributes = (FieldAttributes)state.Reader.ReadInt32();
                    var fieldSize = state.Reader.ReadInt32();
                    FieldBuilder fieldBuilder;
                    if (fieldSize < 0)
                    {
                        fieldBuilder = moduleBuilder.DefineUninitializedData(fieldName, -fieldSize, fieldAttributes);
                    }
                    else
                    {
                        var data = state.Reader.ReadBytes(fieldSize);
                        fieldBuilder = moduleBuilder.DefineInitializedData(fieldName, data, fieldAttributes);
                    }
                    ReadCustomAttributes(state, fieldBuilder.SetCustomAttribute);
                }

                var methodCount = state.Reader.Read7BitEncodedInt();
                var methods = new PickledMethodInfoDef[methodCount];
                for (int i = 0; i < methodCount; ++i)
                {
                    throw new NotImplementedException();
                    //DeserializeMethodHeader(state, null, constructingModule, ref methods[i]);
                    //ReadCustomAttributes(state, method.SetCustomAttribute, genericTypeParameters, genericMethodParameters);
                }

                state.PushTrailer(() =>
                {
                    foreach (var method in methods)
                    {
                        var ilGenerator = method.MethodBuilder.GetILGenerator();
                        DeserializeMethodBody(state, null, method.GenericParameters, method.Locals!, ilGenerator);
                    }
                },
                () => moduleBuilder.CreateGlobalFunctions(),
                () => { });

                return moduleBuilder;
            });
        }

        private PickledGenericType DeserializeGenericInstantiation(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var genericType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            var genericArguments = new PickledTypeInfo[genericArgumentCount];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericArguments[i] = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
            }
            return state.SetMemo(position, true, new PickledGenericType(genericType, genericArguments));
        }

        private PickledTypeInfo DeserializeGenericParameter(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var info = new DeserializeInformation(typeof(MemberInfo), true);
            var genericTypeOrMethod = DeserializeNonNull<PickledMemberInfo>(state, info, genericTypeParameters, genericMethodParameters);
            var genericParameter = state.Reader.Read7BitEncodedInt();
            return state.SetMemo(position, true, genericTypeOrMethod.GetGenericArgument(genericParameter));
        }

        private PickledTypeInfoRef DeserializeTypeRef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var parent = Deserialize(state, ObjectInfo, genericTypeParameters, genericMethodParameters);
            var typeName = state.Reader.ReadString();

            PickledTypeInfoRef? result;
            if (parent is Module module)
            {
                var type = module.GetType(typeName);
                if (type == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{module.FullyQualifiedName}'");
                }

                result = new PickledTypeInfoRef(type);
            }
            else if (parent is PickledTypeInfoRef declaringType)
            {
                result = new PickledTypeInfoRef(declaringType.Type.GetNestedType(typeName, BindingFlags.Public | BindingFlags.NonPublic));
                if (result == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from type '{declaringType}'");
                }
            }
            else if (parent is PickledMethodInfoRef declaringMethod)
            {
                var generics = declaringMethod.MethodInfo.GetGenericArguments();

                result = null;
                foreach (var generic in generics)
                {
                    if (generic.Name == typeName)
                    {
                        result = new PickledTypeInfoRef(generic);
                        break;
                    }
                }

                if (result == null)
                {
                    throw new Exception($"Could not load generic parameter '{typeName}' from type '{declaringMethod}'");
                }
            }
            else
            {
                throw new Exception($"Unexpected parent '{parent}' for type '{typeName}'");
            }
            return state.SetMemo(position, true, result);
        }

        private PickledTypeInfoDef DeserializeTypeDef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            return state.RunWithTrailers(() =>
            {
                var typeName = state.Reader.ReadString();
                var typeAttributes = (TypeAttributes)state.Reader.ReadInt32();
                var typeDef = (TypeDef)state.Reader.ReadByte();
                string[]? genericParameters = null;
                if (typeDef != TypeDef.Enum)
                {
                    // Enums never have generic parameters, but anything else might
                    var genericParameterCount = state.Reader.Read7BitEncodedInt();
                    if (genericParameterCount != 0)
                    {
                        genericParameters = new string[genericParameterCount];
                        for (int i = 0; i < genericParameterCount; ++i)
                        {
                            genericParameters[i] = state.Reader.ReadString();
                        }
                    }
                }

                var (callback, _) = DeserializeWithMemo(state, position, (object parent) =>
                {
                    PickledTypeInfoDef result;
                    if (parent is ModuleBuilder module)
                    {
                        result = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, module.DefineType);
                    }
                    else if (parent is PickledTypeInfoDef declaringType)
                    {
                        result = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, declaringType.TypeBuilder.DefineNestedType);
                    }
                    else
                    {
                        throw new Exception($"Unexpected parent '{parent} : {parent.GetType().FullName}' for type '{typeName}'");
                    }

                    if (genericParameters != null)
                    {
                        result.GenericParameters = result.TypeBuilder.DefineGenericParameters(genericParameters);
                    }

                    state.AddTypeDef(result);
                    return state.SetMemo(position, true, result);
                }, ObjectInfo, genericTypeParameters, genericMethodParameters);
                var constructingType = callback.Invoke();
                DeserializeTypeDef(state, constructingType);
                return constructingType;
            });
        }

        private object DeserializeComplex(PicklerDeserializationState state, PickleOperation operation, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            // Operation won't be any of the primitive operations once this is called

            // -1 for the operation code we've already read
            var position = state.Reader.BaseStream.Position - 1;

            switch (operation)
            {
                case PickleOperation.SZArray:
                case PickleOperation.Array:
                    return DeserializeArray(state, operation == PickleOperation.SZArray, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Mscorlib:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return mscorlib;

                case PickleOperation.AssemblyRef:
                    return DeserializeAsesmblyRef(state, position);

                case PickleOperation.AssemblyDef:
                    return DeserializeAsesmblyDef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ManifestModuleRef:
                    return DeserializeManifestModuleRef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ModuleRef:
                    return DeserializeModuleRef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ModuleDef:
                    return DeserializeModuleDef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ArrayType:
                    {
                        var rank = state.Reader.ReadByte();
                        var elementType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                        Type arrayType;
                        if (rank == 0)
                        {
                            arrayType = elementType.Type.MakeArrayType();
                        }
                        else
                        {
                            arrayType = elementType.Type.MakeArrayType(rank);
                        }
                        return state.SetMemo(position, true, new PickledTypeInfoRef(arrayType));
                    }

                case PickleOperation.GenericInstantiation:
                    return DeserializeGenericInstantiation(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.GenericParameter:
                    return DeserializeGenericParameter(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.MVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (genericMethodParameters == null)
                        {
                            throw new Exception("Encountered an MVar operation without a current method context");
                        }
                        return state.SetMemo(position, true, new PickledTypeInfoRef(genericMethodParameters[genericParameterPosition]));
                    }

                case PickleOperation.TVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (genericTypeParameters == null)
                        {
                            throw new Exception("Encountered an TVar operation without a current type context");
                        }
                        return state.SetMemo(position, true, new PickledTypeInfoRef(genericTypeParameters[genericParameterPosition]));
                    }

                case PickleOperation.TypeRef:
                    return DeserializeTypeRef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.TypeDef:
                    return DeserializeTypeDef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.FieldRef:
                    return DeserializeFieldInfo(state, position, info, genericTypeParameters, genericMethodParameters);

                case PickleOperation.PropertyRef:
                    return DeserializePropertyInfo(state, position, info, genericTypeParameters, genericMethodParameters);

                case PickleOperation.EventRef:
                    return DeserializeEventInfo(state, position, info, genericTypeParameters, genericMethodParameters);

                case PickleOperation.MethodRef:
                    return DeserializeMethodInfo(state, position, info, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ConstructorRef:
                    return DeserializeConstructorInfo(state, position, info, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Delegate:
                    return DeserializeDelegate(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Tuple:
                case PickleOperation.ValueTuple:
                    return state.SetMemo(position, info.ShouldMemo, DeserializeTuple(state, operation == PickleOperation.ValueTuple, genericTypeParameters, genericMethodParameters));

                case PickleOperation.Reducer:
                    {
                        return state.SetMemo(position, info.ShouldMemo, DeserializeReducer(state, genericTypeParameters, genericMethodParameters));
                    }

                case PickleOperation.ISerializable:
                    {
                        Type objType;
                        if (info.StaticType.IsValueType)
                        {
                            objType = info.StaticType;
                        }
                        else
                        {
                            var pickledObjType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                            objType = pickledObjType.Type;
                        }
                        return state.SetMemo(position, info.ShouldMemo, DeserializeISerializable(state, objType, genericTypeParameters, genericMethodParameters));
                    }

                case PickleOperation.Object:
                    {
                        object uninitalizedObject;
                        Type objectType;
                        if (info.StaticType.IsValueType)
                        {
                            objectType = info.StaticType;
                            uninitalizedObject = state.SetMemo(position, info.ShouldMemo, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(objectType));
                        }
                        else
                        {
                            var (callback, objType) = DeserializeWithMemo(state, position, (PickledTypeInfo objType) =>
                            {
                                var result = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(objType.Type);
                                return state.SetMemo(position, info.ShouldMemo, result);
                            }, TypeInfo, genericTypeParameters, genericMethodParameters);

                            objectType = objType.Type;
                            uninitalizedObject = callback.Invoke();
                        }
                        DeserializeObject(state, uninitalizedObject, objectType, genericTypeParameters, genericMethodParameters);
                        return uninitalizedObject;
                    }

                default:
                    {
                        throw new Exception($"Unhandled PickleOperation '{operation}'");
                    }
            }
        }

        private (MemoCallback<R>, T) DeserializeWithMemo<T, R>(PicklerDeserializationState state, long position, Func<T, R> callback, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class where R : class
        {
            var memo = state.RegisterMemoCallback(position, callback);
            var result = DeserializeNonNull<T>(state, info, genericTypeParameters, genericMethodParameters);
            return (memo, result);
        }

        [return: MaybeNull]
        private T Deserialize<T>(PicklerDeserializationState state, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class
        {
            var obj = Deserialize(state, info, genericTypeParameters, genericMethodParameters);
            return obj as T;
        }

        private T DeserializeNonNull<T>(PicklerDeserializationState state, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class
        {
            var position = state.Reader.BaseStream.Position;
            var obj = Deserialize(state, info, genericTypeParameters, genericMethodParameters);
            var result = obj as T;
            if (result == null)
            {
                throw new Exception($"Got unexpected null object at {position}");
            }
            return result;
        }

        private object? Deserialize(PicklerDeserializationState state, DeserializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var position = state.Reader.BaseStream.Position;

            var maybeOperation = InferOperationFromStaticType(info.StaticType);
            PickleOperation operation;
            if (maybeOperation.HasValue)
            {
                operation = maybeOperation.Value;
            }
            else
            {
                // Could not infer operation from static type, read the operation token
                operation = (PickleOperation)state.Reader.ReadByte();
            }

            switch (operation)
            {
                case PickleOperation.Null:
                    return null;

                case PickleOperation.Memo:
                    return state.DoMemo();

                case PickleOperation.Boolean:
                    {
                        var result = (object)state.Reader.ReadBoolean();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Char:
                    {
                        var result = (object)state.Reader.ReadChar();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Byte:
                    {
                        var result = (object)state.Reader.ReadByte();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Int16:
                    {
                        var result = (object)state.Reader.ReadInt16();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Int32:
                    {
                        var result = (object)state.Reader.ReadInt32();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Int64:
                    {
                        var result = (object)state.Reader.ReadInt64();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.SByte:
                    {
                        var result = (object)state.Reader.ReadSByte();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.UInt16:
                    {
                        var result = (object)state.Reader.ReadUInt16();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.UInt32:
                    {
                        var result = (object)state.Reader.ReadUInt32();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.UInt64:
                    {
                        var result = (object)state.Reader.ReadUInt64();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Single:
                    {
                        var result = (object)state.Reader.ReadSingle();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Double:
                    {
                        var result = (object)state.Reader.ReadDouble();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.Decimal:
                    {
                        var result = (object)state.Reader.ReadDecimal();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.DBNull:
                    return DBNull.Value;
                case PickleOperation.String:
                    {
                        var result = state.Reader.ReadString();
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.IntPtr:
                    {
                        var result = new IntPtr(state.Reader.ReadInt64());
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
                case PickleOperation.UIntPtr:
                    {
                        var result = new UIntPtr(state.Reader.ReadUInt64());
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }

                case PickleOperation.Enum:
                    {
                        Type enumType;
                        if (info.StaticType.IsValueType)
                        {
                            enumType = info.StaticType;
                        }
                        else
                        {
                            var pickledEnumType = DeserializeNonNull<PickledTypeInfo>(state, TypeInfo, genericTypeParameters, genericMethodParameters);
                            enumType = pickledEnumType.Type;
                        }
                        var enumTypeCode = Type.GetTypeCode(enumType);
                        var result = Enum.ToObject(enumType, ReadEnumerationValue(state.Reader, enumTypeCode));
                        state.SetMemo(position, info.ShouldMemo, result);
                        return result;
                    }
            }

            return DeserializeComplex(state, operation, info, genericTypeParameters, genericMethodParameters);
        }

        public object? Deserialize(Stream stream)
        {
            using var state = new PicklerDeserializationState(stream);
            // Firstly read the header to make sure it looks like a Pikala stream
            var header = state.Reader.ReadUInt32();
            if (header != _header)
            {
                throw new InvalidDataException("Input stream does not start with PKLA");
            }
            var version = state.Reader.ReadUInt32();
            if (version != _version)
            {
                throw new InvalidDataException($"Input stream does not match expected version. Got {version}, expected {_version}");
            }

            var result = Deserialize(state, ObjectInfo, null, null);
            if (result is PickledObject pickledObject)
            {
                return pickledObject.Get();
            }
            state.DoStaticFields();
            return result;
        }
    }
}
