using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace Ibasa.Pikala
{
    public sealed partial class Pickler
    {
        /// <summary>
        /// This is used for codegen
        /// </summary>
        [return: NotNull]
        private static T AddMemo<T>([DisallowNull] T value, PicklerDeserializationState state, bool memo)
        {
            if (memo)
            {
                object obj = value;
                System.Diagnostics.Debug.Assert(ShouldMemo(obj), "Tried to call AddMemo for an object that shouldn't be memoised");
                state.AddMemo(obj);
            }
            return value;
        }

        private static void AddMemo(PicklerDeserializationState state, object obj)
        {
            System.Diagnostics.Debug.Assert(ShouldMemo(obj), "Tried to call AddMemo for an object that shouldn't be memoised");
            state.AddMemo(obj);
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
                        var type = DeserializeType(state, default);
                        return new SignatureType(type.Type);
                    }

                case SignatureElementOperation.TVar:
                case SignatureElementOperation.MVar:
                    return new SignatureGenericParameter(operation == SignatureElementOperation.TVar, state.Reader.Read7BitEncodedInt());

                case SignatureElementOperation.Generic:
                    {
                        var genericTypeDefinition = DeserializeType(state, default);
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

                case SignatureElementOperation.Pointer:
                    {
                        var elementType = DeserializeSignatureElement(state);
                        return new SignaturePointer(elementType);
                    }
            }

            throw new NotImplementedException($"Unhandled SignatureElement: {operation}");
        }

        private SignatureLocation DeserializeSignatureLocation(PicklerDeserializationState state, bool withModifiers)
        {
            var element = DeserializeSignatureElement(state);

            if (withModifiers)
            {
                var mods = state.Reader.ReadByte();
                var reqmodCount = mods >> 4;
                var optmodCount = mods & 0xF;

                var requiredCustomModifiers = new Type[reqmodCount];
                var optionalCustomModifiers = new Type[optmodCount];

                for (int k = 0; k < reqmodCount; ++k)
                {
                    requiredCustomModifiers[k] = DeserializeType(state, default).Type;
                }
                for (int k = 0; k < optmodCount; ++k)
                {
                    optionalCustomModifiers[k] = DeserializeType(state, default).Type;
                }

                return new SignatureLocation(element, requiredCustomModifiers, optionalCustomModifiers, null);
            }

            return new SignatureLocation(element, Type.EmptyTypes, Type.EmptyTypes, null);
        }

        private Signature DeserializeSignature(PicklerDeserializationState state)
        {
            var name = state.Reader.ReadString();
            var callingConvention = (CallingConventions)state.Reader.ReadByte();
            var genericParameterCount = state.Reader.Read7BitEncodedInt();
            var returnType = DeserializeSignatureLocation(state, true);

            var parameterCount = state.Reader.Read7BitEncodedInt();
            var withModifiers = (parameterCount & 0x1) != 0;
            parameterCount >>= 1;

            var parameters = new SignatureLocation[parameterCount];
            for (int i = 0; i < parameters.Length; ++i)
            {
                parameters[i] = DeserializeSignatureLocation(state, withModifiers);
            }
            return new Signature(name, callingConvention, genericParameterCount, returnType, parameters);
        }

        private (Type, Type[], Type[]) DeserializeParameter(PicklerDeserializationState state, bool withModifiers, GenericTypeContext typeContext)
        {
            var parameterType = DeserializeType(state, typeContext).Type;

            if (withModifiers)
            {
                var mods = state.Reader.ReadByte();
                var reqmodCount = mods >> 4;
                var optmodCount = mods & 0xF;

                var requiredCustomModifiers = new Type[reqmodCount];
                var optionalCustomModifiers = new Type[optmodCount];

                for (int k = 0; k < reqmodCount; ++k)
                {
                    requiredCustomModifiers[k] = DeserializeType(state, typeContext).Type;
                }
                for (int k = 0; k < optmodCount; ++k)
                {
                    optionalCustomModifiers[k] = DeserializeType(state, typeContext).Type;
                }

                return (parameterType, requiredCustomModifiers, optionalCustomModifiers);
            }

            return (parameterType, Type.EmptyTypes, Type.EmptyTypes);
        }

        private void DeserializeConstructorHeader(PicklerDeserializationState state, PickledTypeInfo[]? genericTypeParameters, PickledTypeInfoDef constructingType, out PickledConstructorInfoDef constructingConstructor)
        {
            var typeContext = new GenericTypeContext(genericTypeParameters);
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var callingConvention = (CallingConventions)state.Reader.ReadByte();

            var parameterCount = state.Reader.Read7BitEncodedInt();
            var withModifiers = (parameterCount & 0x1) != 0;
            parameterCount >>= 1;

            Type[]? parameterTypes = null;
            Type[][]? requiredCustomModifiers = null;
            Type[][]? optionalCustomModifiers = null;
            if (parameterCount != 0)
            {
                parameterTypes = new Type[parameterCount];
                requiredCustomModifiers = new Type[parameterCount][];
                optionalCustomModifiers = new Type[parameterCount][];

                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var (parameterType, reqmods, optmods) = DeserializeParameter(state, withModifiers, typeContext);
                    parameterTypes[j] = parameterType;
                    requiredCustomModifiers[j] = reqmods;
                    optionalCustomModifiers[j] = optmods;
                }
            }

            var typeBuilder = constructingType.TypeBuilder;
            var constructorBuilder = typeBuilder.DefineConstructor(methodAttributes, callingConvention, parameterTypes, requiredCustomModifiers, optionalCustomModifiers);

            ParameterBuilder[]? parameters = null;
            if (parameterTypes != null)
            {
                parameters = new ParameterBuilder[parameterCount];
                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterName = state.Reader.ReadNullableString();
                    var parameterAttributes = (ParameterAttributes)state.Reader.ReadInt32();
                    parameters[j] = constructorBuilder.DefineParameter(1 + j, parameterAttributes, parameterName);

                    if (parameterAttributes.HasFlag(ParameterAttributes.HasDefault))
                    {
                        var defaultValue = ReadConstant(state, parameterTypes[j]);
                        parameters[j].SetConstant(defaultValue);
                    }
                }
            }

            constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, parameterTypes);
        }

        private void DeserializeMethodHeader(PicklerDeserializationState state, PickledTypeInfo[]? genericTypeParameters, PickledTypeInfoDef constructingType, ref PickledMethodInfoDef constructingMethod)
        {
            var methodName = state.Reader.ReadString();
            var methodImplAttributes = (MethodImplAttributes)state.Reader.ReadInt32();
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var callingConventions = (CallingConventions)state.Reader.ReadByte();
            var typeBuilder = constructingType.TypeBuilder;

            var methodBuilder = typeBuilder.DefineMethod(methodName, methodAttributes, callingConventions);
            System.Diagnostics.Debug.Assert(methodBuilder.CallingConvention == callingConventions, $"{methodBuilder.CallingConvention} != {callingConventions}");

            var methodGenericParameterNames = new string[state.Reader.Read7BitEncodedInt()];
            for (int j = 0; j < methodGenericParameterNames.Length; ++j)
            {
                methodGenericParameterNames[j] = state.Reader.ReadString();
            }

            constructingMethod = new PickledMethodInfoDef(constructingType, methodBuilder);

            var genericParameterBuilders = methodGenericParameterNames.Length == 0 ? null : methodBuilder.DefineGenericParameters(methodGenericParameterNames);
            if (genericParameterBuilders != null)
            {
                constructingMethod.GenericParameters = new PickledGenericParameterDef[genericParameterBuilders.Length];
                for (int j = 0; j < genericParameterBuilders.Length; ++j)
                {
                    constructingMethod.GenericParameters[j] = new PickledGenericParameterDef(constructingMethod, genericParameterBuilders[j]);
                }
            }

            var typeContext = new GenericTypeContext(genericTypeParameters, constructingMethod.GenericParameters);

            var (returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers) = DeserializeParameter(state, true, typeContext);

            var parameterCount = state.Reader.Read7BitEncodedInt();
            var withModifiers = (parameterCount & 0x1) != 0;
            parameterCount >>= 1;

            Type[][]? parameterTypeRequiredCustomModifiers = null;
            Type[][]? parameterTypeOptionalCustomModifiers = null;
            if (parameterCount != 0)
            {
                constructingMethod.ParameterTypes = new Type[parameterCount];
                parameterTypeRequiredCustomModifiers = new Type[parameterCount][];
                parameterTypeOptionalCustomModifiers = new Type[parameterCount][];

                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var (parameterType, reqmods, optmods) = DeserializeParameter(state, withModifiers, typeContext);
                    constructingMethod.ParameterTypes[j] = parameterType;
                    parameterTypeRequiredCustomModifiers[j] = reqmods;
                    parameterTypeOptionalCustomModifiers[j] = optmods;
                }
            }

            methodBuilder.SetSignature(
                returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                constructingMethod.ParameterTypes, parameterTypeRequiredCustomModifiers, parameterTypeOptionalCustomModifiers);

            constructingMethod.Parameters = new ParameterBuilder[parameterCount];
            for (int j = 0; j < constructingMethod.Parameters.Length; ++j)
            {
                var parameterName = state.Reader.ReadNullableString();
                var parameterAttributes = (ParameterAttributes)state.Reader.ReadInt32();
                constructingMethod.Parameters[j] = methodBuilder.DefineParameter(1 + j, parameterAttributes, parameterName);

                if (parameterAttributes.HasFlag(ParameterAttributes.HasDefault))
                {
                    var defaultValue = ReadConstant(state, constructingMethod.ParameterTypes[j]);
                    constructingMethod.Parameters[j].SetConstant(defaultValue);
                }
            }

            methodBuilder.SetImplementationFlags(methodImplAttributes);
        }

        private void DeserializeMethodBody(PicklerDeserializationState state, GenericTypeContext typeContext, ConstructorBuilder constructorBuilder)
        {
            var ilGenerator = constructorBuilder.GetILGenerator();
            System.Diagnostics.Debug.Assert(ilGenerator != null, "Expected non-null ILGenerator for constructor");
            DeserializeMethodBody(state, typeContext, ilGenerator, b => constructorBuilder.InitLocals = b);
        }

        private void DeserializeMethodBody(PicklerDeserializationState state, GenericTypeContext typeContext, MethodBuilder methodBuilder)
        {
            var ilGenerator = methodBuilder.GetILGenerator();
            System.Diagnostics.Debug.Assert(ilGenerator != null, "Expected non-null ILGenerator for method");
            DeserializeMethodBody(state, typeContext, ilGenerator, b => methodBuilder.InitLocals = b);
        }

        private void DeserializeMethodBody(PicklerDeserializationState state, GenericTypeContext typeContext, ILGenerator ilGenerator, Action<bool> setInitLocals)
        {
            setInitLocals(state.Reader.ReadBoolean());

            var locals = new PickledTypeInfo[state.Reader.Read7BitEncodedInt()];
            for (int j = 0; j < locals.Length; ++j)
            {
                var localType = DeserializeType(state, typeContext);
                ilGenerator.DeclareLocal(localType.Type);
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
                            var memberInfo = DeserializeMemberInfo(state);
                            memberInfo.Emit(ilGenerator, opCode);
                            break;
                        }

                    case OperandType.InlineType:
                        {
                            var typeInfo = DeserializeType(state, typeContext);
                            ilGenerator.Emit(opCode, typeInfo.Type);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldInfo = DeserializeFieldInfo(state, false);
                            ilGenerator.Emit(opCode, fieldInfo.FieldInfo);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodBase = DeserializeMethodBase(state);
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

        private object? ReadConstant(PicklerDeserializationState state, Type constantType)
        {
            if (constantType == typeof(string))
            {
                return state.Reader.ReadNullableString();
            }

            if (constantType.IsEnum)
            {
                return ReadEnumerationValue(state.Reader, Type.GetTypeCode(constantType));
            }
            else if (constantType == typeof(bool))
            {
                return state.Reader.ReadBoolean();
            }
            else if (constantType == typeof(char))
            {
                return state.Reader.ReadChar();
            }
            else if (constantType == typeof(byte))
            {
                return state.Reader.ReadByte();
            }
            else if (constantType == typeof(sbyte))
            {
                return state.Reader.ReadSByte();
            }
            else if (constantType == typeof(short))
            {
                return state.Reader.ReadInt16();
            }
            else if (constantType == typeof(ushort))
            {
                return state.Reader.ReadUInt16();
            }
            else if (constantType == typeof(int))
            {
                return state.Reader.ReadInt32();
            }
            else if (constantType == typeof(uint))
            {
                return state.Reader.ReadUInt32();
            }
            else if (constantType == typeof(long))
            {
                return state.Reader.ReadInt64();
            }
            else if (constantType == typeof(ulong))
            {
                return state.Reader.ReadUInt64();
            }
            else if (constantType == typeof(float))
            {
                return state.Reader.ReadSingle();
            }
            else if (constantType == typeof(double))
            {
                return state.Reader.ReadDouble();
            }
            else if (constantType == typeof(decimal))
            {
                return state.Reader.ReadDecimal();
            }
            else
            {
                return null;
            }
        }


        private void DeserializeInterfaceDef(PicklerDeserializationState state, PickledTypeInfoDef constructingType)
        {
            var typeContext = new GenericTypeContext(constructingType.GenericParameters);

            AddMemo(state, constructingType);

            state.Stages.PushStage2(state =>
            {
                var typeBuilder = constructingType.TypeBuilder;

                var baseTypes = new List<PickledTypeInfo>();

                ReadCustomAttributesTypes(state);

                var interfaceCount = state.Reader.Read7BitEncodedInt();
                var interfaceMap = new List<(PickledMethodInfo, Signature)>();
                for (int i = 0; i < interfaceCount; ++i)
                {
                    var interfaceType = DeserializeType(state, typeContext);
                    baseTypes.Add(interfaceType);
                    typeBuilder.AddInterfaceImplementation(interfaceType.Type);
                }

                constructingType.BaseTypes = baseTypes.ToArray();

                var methodCount = state.Reader.Read7BitEncodedInt();
                constructingType.Methods = new PickledMethodInfoDef[methodCount];
                for (int i = 0; i < methodCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
                    DeserializeMethodHeader(state, constructingType.GenericParameters, constructingType, ref constructingType.Methods[i]);
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
                    ReadCustomAttributesTypes(state);
                    var propertyName = state.Reader.ReadString();
                    var propertyAttributes = (PropertyAttributes)state.Reader.ReadInt32();
                    var propertySignature = DeserializeSignature(state);

                    var (returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers) = propertySignature.ReturnType.Reify(typeContext);
                    var parameterTypes = new Type[propertySignature.Parameters.Length];
                    var parameterTypeRequiredCustomModifiers = new Type[propertySignature.Parameters.Length][];
                    var parameterTypeOptionalCustomModifiers = new Type[propertySignature.Parameters.Length][];
                    for (int j = 0; j < propertySignature.Parameters.Length; ++j)
                    {
                        var (type, reqmods, optmods) = propertySignature.Parameters[j].Reify(typeContext);
                        parameterTypes[j] = type;
                        parameterTypeRequiredCustomModifiers[j] = reqmods;
                        parameterTypeOptionalCustomModifiers[j] = optmods;
                    }

                    var propertyBuilder = typeBuilder.DefineProperty(
                        propertyName, propertyAttributes, propertySignature.CallingConvention,
                        returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                        parameterTypes, parameterTypeRequiredCustomModifiers, parameterTypeOptionalCustomModifiers);
                    constructingType.Properties[i] = new PickledPropertyInfoDef(constructingType, propertyBuilder, propertySignature);

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
                    ReadCustomAttributesTypes(state);
                    var eventName = state.Reader.ReadString();
                    var eventAttributes = (EventAttributes)state.Reader.ReadInt32();
                    var eventType = DeserializeType(state, typeContext);


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

                state.Stages.PushStage3(state =>
                {
                    ReadCustomAttributes(state, constructingType.TypeBuilder.SetCustomAttribute);

                    foreach (var method in constructingType.Methods)
                    {
                        var methodBuilder = method.MethodBuilder;
                        ReadCustomAttributes(state, methodBuilder.SetCustomAttribute);
                        if (methodBuilder.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || methodBuilder.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || methodBuilder.Attributes.HasFlag(MethodAttributes.Abstract))
                        {

                        }
                        else
                        {
                            DeserializeMethodBody(state, new GenericTypeContext(typeContext.GenericTypeParameters, method.GenericParameters), methodBuilder);
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
                });
            });
        }

        private void DeserializeTypeDefComplex(PicklerDeserializationState state, PickledTypeInfoDef constructingType)
        {
            var typeContext = new GenericTypeContext(constructingType.GenericParameters);

            AddMemo(state, constructingType);

            state.Stages.PushStage2(state =>
            {
                var isValueType = constructingType.TypeDef == TypeDef.Struct;
                var typeBuilder = constructingType.TypeBuilder;

                var baseTypes = new List<PickledTypeInfo>();

                if (!isValueType)
                {
                    var baseType = DeserializeType(state, typeContext);
                    baseTypes.Add(baseType);
                    typeBuilder.SetParent(baseType.Type);
                }

                ReadCustomAttributesTypes(state);

                var interfaceCount = state.Reader.Read7BitEncodedInt();
                var interfaceMap = new List<(PickledMethodInfo, Signature)>();
                for (int i = 0; i < interfaceCount; ++i)
                {
                    var interfaceType = DeserializeType(state, typeContext);
                    baseTypes.Add(interfaceType);
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

                constructingType.BaseTypes = baseTypes.ToArray();

                var fieldCount = state.Reader.Read7BitEncodedInt();
                constructingType.Fields = new PickledFieldInfoDef[fieldCount];
                var serialisationFields = new List<(PickledTypeInfo, PickledFieldInfo)>();
                for (int i = 0; i < fieldCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
                    var fieldName = state.Reader.ReadString();
                    var fieldAttributes = (FieldAttributes)state.Reader.ReadInt32();
                    var fieldType = DeserializeType(state, typeContext);
                    var fieldBuilder = typeBuilder.DefineField(fieldName, fieldType.Type, fieldAttributes);
                    constructingType.Fields[i] = new PickledFieldInfoDef(constructingType, fieldBuilder);

                    if (!fieldAttributes.HasFlag(FieldAttributes.Literal) && !fieldAttributes.HasFlag(FieldAttributes.Static))
                    {
                        serialisationFields.Add((fieldType, constructingType.Fields[i]));
                    }

                    if (fieldAttributes.HasFlag(FieldAttributes.Literal))
                    {
                        fieldBuilder.SetConstant(ReadConstant(state, fieldBuilder.FieldType));
                    }
                }

                var constructorCount = state.Reader.Read7BitEncodedInt();
                constructingType.Constructors = new PickledConstructorInfoDef[constructorCount];
                for (int i = 0; i < constructorCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
                    DeserializeConstructorHeader(state, constructingType.GenericParameters, constructingType, out constructingType.Constructors[i]);
                }

                var methodCount = state.Reader.Read7BitEncodedInt();
                constructingType.Methods = new PickledMethodInfoDef[methodCount];
                for (int i = 0; i < methodCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
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
                    ReadCustomAttributesTypes(state);
                    var propertyName = state.Reader.ReadString();
                    var propertyAttributes = (PropertyAttributes)state.Reader.ReadInt32();
                    var propertySignature = DeserializeSignature(state);

                    var (returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers) = propertySignature.ReturnType.Reify(typeContext);
                    var parameterTypes = new Type[propertySignature.Parameters.Length];
                    var parameterTypeRequiredCustomModifiers = new Type[propertySignature.Parameters.Length][];
                    var parameterTypeOptionalCustomModifiers = new Type[propertySignature.Parameters.Length][];
                    for (int j = 0; j < propertySignature.Parameters.Length; ++j)
                    {
                        var (type, reqmods, optmods) = propertySignature.Parameters[j].Reify(typeContext);
                        parameterTypes[j] = type;
                        parameterTypeRequiredCustomModifiers[j] = reqmods;
                        parameterTypeOptionalCustomModifiers[j] = optmods;
                    }

                    var propertyBuilder = typeBuilder.DefineProperty(
                        propertyName, propertyAttributes, propertySignature.CallingConvention,
                        returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                        parameterTypes, parameterTypeRequiredCustomModifiers, parameterTypeOptionalCustomModifiers);
                    constructingType.Properties[i] = new PickledPropertyInfoDef(constructingType, propertyBuilder, propertySignature);

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
                    ReadCustomAttributesTypes(state);
                    var eventName = state.Reader.ReadString();
                    var eventAttributes = (EventAttributes)state.Reader.ReadInt32();
                    var eventType = DeserializeType(state, typeContext);


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

                state.Stages.PushStage3(state =>
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
                        DeserializeMethodBody(state, new GenericTypeContext(typeContext.GenericTypeParameters), constructorBuilder);
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
                            DeserializeMethodBody(state, new GenericTypeContext(typeContext.GenericTypeParameters, method.GenericParameters), methodBuilder);
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

                    state.Stages.PushStage4(state =>
                    {
                        var type = constructingType.CompleteType;

                        var staticFields =
                            type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Where(field => !field.IsLiteral && !field.IsInitOnly)
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
                                    break;
                                }
                            }

                            if (fieldInfo == null)
                            {
                                throw new Exception($"Could not find static field '{fieldName}' on type '{type.Name}'");
                            }

                            var typeInfo = GetOrReadSerialisedObjectTypeInfo(state, fieldInfo.FieldType);
                            var fieldValue = InvokeDeserializationMethod(typeInfo, state, false);
                            fieldInfo.SetValue(null, fieldValue);
                        }
                    });
                });
            });
        }

        private PickledTypeInfoDef ConstructingTypeForTypeDef(TypeDef typeDef, string typeName, TypeAttributes typeAttributes, PickledTypeInfoDef? parent, Func<string, TypeAttributes, Type?, TypeBuilder> defineType)
        {

            switch (typeDef)
            {
                case TypeDef.Enum:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(Enum)), parent);

                case TypeDef.Delegate:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(MulticastDelegate)), parent);

                case TypeDef.Struct:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, typeof(ValueType)), parent);

                case TypeDef.Class:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, null), parent);

                case TypeDef.Interface:
                    return new PickledTypeInfoDef(typeDef, defineType(typeName, typeAttributes, null), parent);

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

                AddMemo(state, constructingType);

                state.Stages.PushStage2(state =>
                {
                    ReadCustomAttributesTypes(state);

                    state.Stages.PushStage3(state =>
                    {
                        ReadCustomAttributes(state, typeBuilder.SetCustomAttribute);

                        constructingType.FullyDefined = true;

                        state.Stages.PushStage4(state =>
                        {
                            // Ensure the type is constructed
                            var _ = constructingType.CompleteType;
                        });
                    });
                });
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

                var typeContext = new GenericTypeContext(constructingType.GenericParameters);

                var constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, constructorParameters);
                constructingType.Constructors = new PickledConstructorInfoDef[] { constructingConstructor };

                var returnType = DeserializeType(state, typeContext);
                var parameterCount = state.Reader.Read7BitEncodedInt();
                var parameterNames = new string?[parameterCount];
                var parameterTypes = new Type[parameterCount];
                for (int i = 0; i < parameterCount; ++i)
                {
                    parameterNames[i] = state.Reader.ReadNullableString();
                    var parameterType = DeserializeType(state, typeContext);
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

                AddMemo(state, constructingType);

                state.Stages.PushStage2(state =>
                {
                    ReadCustomAttributesTypes(state);

                    state.Stages.PushStage3(state =>
                    {
                        ReadCustomAttributes(state, typeBuilder.SetCustomAttribute);

                        constructingType.FullyDefined = true;

                        state.Stages.PushStage4(state =>
                        {
                            // Ensure the type is constructed
                            var _ = constructingType.CompleteType;
                        });
                    });
                });
            } 
            else if (constructingType.TypeDef == TypeDef.Interface)
            {
                DeserializeInterfaceDef(state, constructingType);
            }
            else
            {
                DeserializeTypeDefComplex(state, constructingType);
            }
        }

        private PickledFieldInfo DeserializeFieldInfo(PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PickledFieldInfo>(state, false, out var obj))
                {
                    if (obj == null)
                    {
                        throw new Exception("FieldInfo was null");
                    }
                    return obj;
                }
            }

            var name = state.Reader.ReadString();
            // Fields can be on modules or types
            var typeField = state.Reader.ReadBoolean();
            if (typeField)
            {
                var type = DeserializeType(state, default);
                state.Stages.PopStages(state, 2);
                var result = type.GetField(name);
                AddMemo(state, result);
                return result;
            }
            else
            {
                var module = DeserializeModule(state);
                state.Stages.PopStages(state, 2);
                var result = module.GetField(name);
                AddMemo(state, result);
                return result;
            }
        }

        private PickledPropertyInfo DeserializePropertyInfo(PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PickledPropertyInfo>(state, false, out var obj))
                {
                    if (obj == null)
                    {
                        throw new Exception("PropertyInfo was null");
                    }
                    return obj;
                }
            }

            var signature = DeserializeSignature(state);
            var type = DeserializeType(state, default);
            state.Stages.PopStages(state, 2);
            var result = type.GetProperty(signature);
            AddMemo(state, result);
            return result;
        }

        private PickledEventInfo DeserializeEventInfo(PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PickledEventInfo>(state, false, out var obj))
                {
                    if (obj == null)
                    {
                        throw new Exception("EventInfo was null");
                    }
                    return obj;
                }
            }

            var name = state.Reader.ReadString();
            var type = DeserializeType(state, default);
            state.Stages.PopStages(state, 2);
            var result = type.GetEvent(name);
            AddMemo(state, result);
            return result;
        }

        private PickledConstructorInfo DeserializeConstructorInfo(PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PickledConstructorInfo>(state, false, out var obj))
                {
                    if (obj == null)
                    {
                        throw new Exception("ConstructorInfo was null");
                    }
                    return obj;
                }
            }

            var signature = DeserializeSignature(state);
            var type = DeserializeType(state, default);
            state.Stages.PopStages(state, 2);
            var result = type.GetConstructor(signature);
            AddMemo(state, result);
            return result;
        }

        private PickledMethodInfo DeserializeMethodInfo(PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PickledMethodInfo>(state, false, out var obj))
                {
                    if (obj == null)
                    {
                        throw new Exception("MethodInfo was null");
                    }
                    return obj;
                }
            }

            var signature = DeserializeSignature(state);
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            PickledTypeInfo[]? genericArguments = null;
            if (genericArgumentCount != 0)
            {
                genericArguments = new PickledTypeInfo[genericArgumentCount];
                for (int i = 0; i < genericArgumentCount; ++i)
                {
                    genericArguments[i] = DeserializeType(state, default);
                }
            }

            // Methods can be on modules or types
            var typeMethod = state.Reader.ReadBoolean();
            PickledMethodInfo methodInfo;
            if (typeMethod)
            {
                var type = DeserializeType(state, default);
                state.Stages.PopStages(state, 2);
                methodInfo = type.GetMethod(signature);
            }
            else
            {
                var module = DeserializeModule(state);
                state.Stages.PopStages(state, 2);
                methodInfo = module.GetMethod(signature);
            }

            if (genericArguments != null)
            {
                methodInfo = new ConstructingGenericMethod(methodInfo, genericArguments);
            }
            AddMemo(state, methodInfo);
            return methodInfo;
        }

        private static bool MaybeReadMemo<T>(PicklerDeserializationState state, [NotNullWhen(true)] out T? result)
        {
            var offset = state.Reader.Read15BitEncodedLong();
            if (offset == 0)
            {
                result = default;
                return false;
            }
            result = (T)state.GetMemo(offset);
            return true;
        }

        private PickledAssemblyRef DeserializeAsesmblyRef(PicklerDeserializationState state)
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
            var result = new PickledAssemblyRef(assembly);
            AddMemo(state, result);
            return result;
        }

        private void ReadCustomAttributes(PicklerDeserializationState state, Action<ConstructorInfo, byte[]> setCustomAttribute)
        {
            var buffer = new MemoryStream();
            var writer = new BinaryWriter(buffer);

            void WriteString(string str)
            {
                // Strings are emitted with a length prefix in a compressed format (1, 2 or 4 bytes) as used internally by metadata.
                byte[] utf8Str = System.Text.Encoding.UTF8.GetBytes(str);
                uint length = (uint)utf8Str.Length;
                if (length <= 0x7f)
                {
                    writer.Write((byte)length);
                }
                else if (length <= 0x3fff)
                {
                    writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((short)(length | 0x80_00)));
                }
                else
                {
                    writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(length | 0xC0_00_00_00));
                }
                writer.Write(utf8Str);
            }

            void CopyValue(Type type)
            {
                // Read a value of type 'type' from the pickle stream and write it to the buffer in ECMA blob format.
                // This is mostly a copy of bytes but our encoding of arrays, strings, and types differs.

                if (type.IsEnum)
                {
                    switch (Type.GetTypeCode(Enum.GetUnderlyingType(type)))
                    {
                        case TypeCode.SByte:
                            writer.Write(state.Reader.ReadSByte());
                            break;
                        case TypeCode.Byte:
                            writer.Write(state.Reader.ReadByte());
                            break;
                        case TypeCode.Int16:
                            writer.Write(state.Reader.ReadInt16());
                            break;
                        case TypeCode.UInt16:
                            writer.Write(state.Reader.ReadUInt16());
                            break;
                        case TypeCode.Int32:
                            writer.Write(state.Reader.ReadInt32());
                            break;
                        case TypeCode.UInt32:
                            writer.Write(state.Reader.ReadUInt32());
                            break;
                        case TypeCode.Int64:
                            writer.Write(state.Reader.ReadInt64());
                            break;
                        case TypeCode.UInt64:
                            writer.Write(state.Reader.ReadUInt64());
                            break;
                        default:
                            throw new Exception("Invalid base type for enum");
                    }
                }
                else if (type == typeof(string))
                {
                    var value = state.Reader.ReadNullableString();
                    if (value == null)
                    {
                        writer.Write((byte)0xFF);
                    }
                    else
                    {
                        WriteString(value);
                    }
                }
                else if (type == typeof(Type))
                {
                    var isNotNull = state.Reader.ReadBoolean();
                    if (!isNotNull)
                    {
                        writer.Write((byte)0xFF);
                    }
                    else
                    {
                        var value = DeserializeType(state, default);
                        WriteString(value.Type.AssemblyQualifiedName!);
                    }
                }
                else if (type.IsArray)
                {
                    var count = state.Reader.Read7BitEncodedInt();
                    if (count == -1)
                    {
                        writer.Write(0xFFFFFFFF);
                    }
                    else
                    {
                        writer.Write(count);
                        var elementType = type.GetElementType()!;
                        for (int i = 0; i < count; ++i)
                        {
                            CopyValue(elementType);
                        }
                    }
                }
                else if (type.IsPrimitive)
                {
                    switch (Type.GetTypeCode(type))
                    {
                        case TypeCode.SByte:
                            writer.Write(state.Reader.ReadSByte());
                            break;
                        case TypeCode.Byte:
                            writer.Write(state.Reader.ReadByte());
                            break;
                        case TypeCode.Char:
                            writer.Write(state.Reader.ReadChar());
                            break;
                        case TypeCode.Boolean:
                            writer.Write(state.Reader.ReadBoolean());
                            break;
                        case TypeCode.Int16:
                            writer.Write(state.Reader.ReadInt16());
                            break;
                        case TypeCode.UInt16:
                            writer.Write(state.Reader.ReadUInt16());
                            break;
                        case TypeCode.Int32:
                            writer.Write(state.Reader.ReadInt32());
                            break;
                        case TypeCode.UInt32:
                            writer.Write(state.Reader.ReadUInt32());
                            break;
                        case TypeCode.Int64:
                            writer.Write(state.Reader.ReadInt64());
                            break;
                        case TypeCode.UInt64:
                            writer.Write(state.Reader.ReadUInt64());
                            break;
                        case TypeCode.Single:
                            writer.Write(state.Reader.ReadSingle());
                            break;
                        case TypeCode.Double:
                            writer.Write(state.Reader.ReadDouble());
                            break;
                        default:
                            throw new Exception("Invalid primitive type for attribute");
                    }
                }
                else if (type == typeof(object))
                {
                    var taggedType = ReadType();
                    WriteType(taggedType);
                    CopyValue(taggedType);
                }
                else
                {
                    throw new Exception($"Unsupported type for attrtibute value: {type}");
                }
            }

            void WriteType(Type type)
            {
                if (type.IsPrimitive)
                {
                    switch (Type.GetTypeCode(type))
                    {
                        case TypeCode.SByte:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SByte);
                            break;
                        case TypeCode.Byte:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Byte);
                            break;
                        case TypeCode.Char:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Char);
                            break;
                        case TypeCode.Boolean:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Boolean);
                            break;
                        case TypeCode.Int16:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int16);
                            break;
                        case TypeCode.UInt16:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt16);
                            break;
                        case TypeCode.Int32:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int32);
                            break;
                        case TypeCode.UInt32:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt32);
                            break;
                        case TypeCode.Int64:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int64);
                            break;
                        case TypeCode.UInt64:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt64);
                            break;
                        case TypeCode.Single:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Single);
                            break;
                        case TypeCode.Double:
                            writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Double);
                            break;
                        default:
                            throw new Exception("Invalid primitive type for attribute");
                    }
                }
                else if (type.IsEnum)
                {
                    writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Enum);
                    WriteString(type.AssemblyQualifiedName!);
                }
                else if (type == typeof(string))
                {
                    writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.String);
                }
                else if (type == typeof(Type))
                {
                    writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Type);
                }
                else if (type.IsArray)
                {
                    writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SZArray);
                    WriteType(type.GetElementType()!);
                }
                else
                {
                    writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.TaggedObject);
                }
            }

            Type ReadType()
            {
                var typeCode = (System.Reflection.Metadata.SerializationTypeCode)state.Reader.ReadByte();

                switch (typeCode)
                {
                    case System.Reflection.Metadata.SerializationTypeCode.SByte:
                        return typeof(sbyte);
                    case System.Reflection.Metadata.SerializationTypeCode.Byte:
                        return typeof(byte);
                    case System.Reflection.Metadata.SerializationTypeCode.Char:
                        return typeof(char);
                    case System.Reflection.Metadata.SerializationTypeCode.Boolean:
                        return typeof(bool);
                    case System.Reflection.Metadata.SerializationTypeCode.Int16:
                        return typeof(short);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt16:
                        return typeof(ushort);
                    case System.Reflection.Metadata.SerializationTypeCode.Int32:
                        return typeof(int);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt32:
                        return typeof(uint);
                    case System.Reflection.Metadata.SerializationTypeCode.Int64:
                        return typeof(long);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt64:
                        return typeof(ulong);
                    case System.Reflection.Metadata.SerializationTypeCode.Single:
                        return typeof(float);
                    case System.Reflection.Metadata.SerializationTypeCode.Double:
                        return typeof(double);
                    case System.Reflection.Metadata.SerializationTypeCode.String:
                        return typeof(string);
                    case System.Reflection.Metadata.SerializationTypeCode.Type:
                        return typeof(Type);
                    case System.Reflection.Metadata.SerializationTypeCode.TaggedObject:
                        return typeof(object);
                    case System.Reflection.Metadata.SerializationTypeCode.Enum:
                        return DeserializeType(state, default).Type;
                    case System.Reflection.Metadata.SerializationTypeCode.SZArray:
                        return ReadType().MakeArrayType();
                }

                throw new Exception($"Unhandled type code: {typeCode}");
            }

            var attributeCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < attributeCount; ++i)
            {
                var attributeType = DeserializeType(state, default);
                var constructorSignature = DeserializeSignature(state);

                var constructor = attributeType.GetConstructor(constructorSignature);

                // We need to build the attribute binary manually because using CustomAttributeBuilder invokes type loads for verification checks we don't need.
                buffer.SetLength(0);

                // Write the blob header
                writer.Write((ushort)1);

                // First copy the fixed constructor attributes
                foreach (var parameter in constructor.GetParameters())
                {
                    CopyValue(parameter);
                }

                // Read the total number of fields and properties and write out the combined number to the blob stream
                var fieldCount = state.Reader.Read7BitEncodedInt();
                var propertyCount = state.Reader.Read7BitEncodedInt();

                writer.Write((ushort)(fieldCount + propertyCount));

                // Read in the fields and copy them to the blob stream
                for (int j = 0; j < fieldCount; ++j)
                {
                    writer.Write((byte)System.Reflection.Metadata.CustomAttributeNamedArgumentKind.Field);

                    var itemType = ReadType();
                    var itemName = state.Reader.ReadString();

                    // Check we have a field of this name and type
                    var fieldInfo = attributeType.GetField(itemName);
                    if (fieldInfo.FieldInfo.FieldType != itemType)
                    {
                        throw new InvalidOperationException($"Expected attribute field {attributeType}.{itemName} to have type {itemType} but was {fieldInfo.FieldInfo.FieldType}");
                    }

                    WriteType(itemType);
                    writer.Write(itemName);
                    CopyValue(itemType);
                }

                // Read in the properties and copy them to the blob stream
                for (int j = 0; j < propertyCount; ++j)
                {
                    writer.Write((byte)System.Reflection.Metadata.CustomAttributeNamedArgumentKind.Property);

                    var itemType = ReadType();
                    var itemName = state.Reader.ReadString();

                    WriteType(itemType);
                    writer.Write(itemName);
                    CopyValue(itemType);
                }

                setCustomAttribute(constructor.ConstructorInfo, buffer.ToArray());
            }
        }

        private void ReadCustomAttributesTypes(PicklerDeserializationState state)
        {
            void ReadValue(Type type)
            {
                // Read a value of type 'type' from the pickle stream and write it to the buffer in ECMA blob format.
                // This is mostly a copy of bytes but our encoding of arrays, strings, and types differs.

                if (type.IsEnum)
                {
                }
                else if (type == typeof(string))
                {
                }
                else if (type == typeof(Type))
                {
                    var isNotNull = state.Reader.ReadBoolean();
                    if (!isNotNull)
                    {
                    }
                    else
                    {
                        DeserializeType(state, default);
                    }
                }
                else if (type.IsArray)
                {
                    var count = state.Reader.Read7BitEncodedInt();
                    if (count == -1)
                    {
                    }
                    else
                    {
                        var elementType = type.GetElementType()!;
                        for (int i = 0; i < count; ++i)
                        {
                            ReadValue(elementType);
                        }
                    }
                }
                else if (type.IsPrimitive)
                {
                }
                else if (type == typeof(object))
                {
                    var taggedType = ReadType();
                    ReadValue(taggedType);
                }
                else
                {
                    throw new Exception($"Unsupported type for attrtibute value: {type}");
                }
            }

            Type ReadType()
            {
                var typeCode = (System.Reflection.Metadata.SerializationTypeCode)state.Reader.ReadByte();

                switch (typeCode)
                {
                    case System.Reflection.Metadata.SerializationTypeCode.SByte:
                        return typeof(sbyte);
                    case System.Reflection.Metadata.SerializationTypeCode.Byte:
                        return typeof(byte);
                    case System.Reflection.Metadata.SerializationTypeCode.Char:
                        return typeof(char);
                    case System.Reflection.Metadata.SerializationTypeCode.Boolean:
                        return typeof(bool);
                    case System.Reflection.Metadata.SerializationTypeCode.Int16:
                        return typeof(short);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt16:
                        return typeof(ushort);
                    case System.Reflection.Metadata.SerializationTypeCode.Int32:
                        return typeof(int);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt32:
                        return typeof(uint);
                    case System.Reflection.Metadata.SerializationTypeCode.Int64:
                        return typeof(long);
                    case System.Reflection.Metadata.SerializationTypeCode.UInt64:
                        return typeof(ulong);
                    case System.Reflection.Metadata.SerializationTypeCode.Single:
                        return typeof(float);
                    case System.Reflection.Metadata.SerializationTypeCode.Double:
                        return typeof(double);
                    case System.Reflection.Metadata.SerializationTypeCode.String:
                        return typeof(string);
                    case System.Reflection.Metadata.SerializationTypeCode.Type:
                        return typeof(Type);
                    case System.Reflection.Metadata.SerializationTypeCode.TaggedObject:
                        return typeof(object);
                    case System.Reflection.Metadata.SerializationTypeCode.Enum:
                        return DeserializeType(state, default).Type;
                    case System.Reflection.Metadata.SerializationTypeCode.SZArray:
                        return ReadType().MakeArrayType();
                }

                throw new Exception($"Unhandled type code: {typeCode}");
            }

            var attributeCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < attributeCount; ++i)
            {
                var attributeType = DeserializeType(state, default);
                var constructorSignature = DeserializeSignature(state);

                var constructorArguments = state.Reader.Read7BitEncodedInt();

                // First copy the fixed constructor attributes
                for (int j = 0; j < constructorArguments; ++j)
                {
                    var itemType = ReadType();
                    ReadValue(itemType);
                }

                // Read the total number of fields and properties and write out the combined number to the blob stream
                var fieldCount = state.Reader.Read7BitEncodedInt();
                var propertyCount = state.Reader.Read7BitEncodedInt();

                // Read in the fields and copy them to the blob stream
                for (int j = 0; j < fieldCount; ++j)
                {
                    var itemType = ReadType();
                    ReadValue(itemType);
                }

                // Read in the properties and copy them to the blob stream
                for (int j = 0; j < propertyCount; ++j)
                {
                    var itemType = ReadType();
                    ReadValue(itemType);
                }
            }
        }

        private PickledAssemblyDef DeserializeAssemblyDef(PicklerDeserializationState state)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            var access = AssemblyLoadContext.IsCollectible ? AssemblyBuilderAccess.RunAndCollect : AssemblyBuilderAccess.Run;
            var assemblyBuilder = AssemblyLoadContext.DefineDynamicAssembly(assemblyName, access);
            if (assemblyBuilder == null)
            {
                throw new Exception($"Could not define assembly '{assemblyName}'");
            }

            var assemblyDef = new PickledAssemblyDef(assemblyBuilder);
            AddMemo(state, assemblyDef);
            state.Stages.PushStage2(state =>
            {
                ReadCustomAttributesTypes(state);

                state.Stages.PushStage3(state =>
                {
                    ReadCustomAttributes(state, assemblyDef.AssemblyBuilder.SetCustomAttribute);
                });
            });

            return assemblyDef;
        }

        private PickledModuleRef DeserializeManifestModuleRef(PicklerDeserializationState state)
        {
            var assembly = DeserializeAssembly(state);
            var result = new PickledModuleRef(assembly.Assembly.ManifestModule);
            AddMemo(state, result);
            return result;
        }

        private PickledModuleRef DeserializeModuleRef(PicklerDeserializationState state)
        {
            var name = state.Reader.ReadString();
            var assembly = DeserializeAssembly(state);
            var module = assembly.Assembly.GetModule(name);
            if (module == null)
            {
                throw new Exception($"Could not load module '{name}' from assembly '{assembly}'");
            }
            var result = new PickledModuleRef(module);
            AddMemo(state, result);
            return result;
        }

        private PickledModuleDef DeserializeModuleDef(PicklerDeserializationState state)
        {
            var name = state.Reader.ReadString();
            var assembly = DeserializeAssembly(state);

            var assemblyDef = (PickledAssemblyDef)assembly;
            var assemblyBuilder = assemblyDef.AssemblyBuilder;
            var module = assemblyBuilder.DefineDynamicModule(name);
            if (module == null)
            {
                throw new Exception($"Could not create module '{name}' in assembly '{assembly}'");
            }
            var moduleDef = new PickledModuleDef(module);
            AddMemo(state, moduleDef);
            state.Stages.PushStage2(state =>
            {
                ReadCustomAttributesTypes(state);

                var fieldCount = state.Reader.Read7BitEncodedInt();
                moduleDef.Fields = new PickledFieldInfoDef[fieldCount];
                for (int i = 0; i < fieldCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
                    var fieldName = state.Reader.ReadString();
                    var fieldAttributes = (FieldAttributes)state.Reader.ReadInt32();
                    var fieldSize = state.Reader.ReadInt32();
                    FieldBuilder fieldBuilder;
                    if (fieldSize < 0)
                    {
                        fieldBuilder = moduleDef.ModuleBuilder.DefineUninitializedData(fieldName, -fieldSize, fieldAttributes);
                    }
                    else
                    {
                        var data = state.Reader.ReadBytes(fieldSize);
                        fieldBuilder = moduleDef.ModuleBuilder.DefineInitializedData(fieldName, data, fieldAttributes);
                    }

                    moduleDef.Fields[i] = new PickledFieldInfoDef(moduleDef, fieldBuilder);
                }

                var methodCount = state.Reader.Read7BitEncodedInt();
                moduleDef.Methods = new PickledMethodInfoDef[methodCount];
                for (int i = 0; i < methodCount; ++i)
                {
                    ReadCustomAttributesTypes(state);
                    throw new NotImplementedException();
                    //DeserializeMethodHeader(state, null, constructingModule, ref methods[i]);
                    //ReadCustomAttributes(state, method.SetCustomAttribute, typeContext);
                }

                state.Stages.PushStage3(state =>
                {

                    ReadCustomAttributes(state, moduleDef.ModuleBuilder.SetCustomAttribute);

                    foreach (var field in moduleDef.Fields)
                    {
                        ReadCustomAttributes(state, field.FieldBuilder.SetCustomAttribute);
                    }

                    foreach (var method in moduleDef.Methods)
                    {
                        ReadCustomAttributes(state, method.MethodBuilder.SetCustomAttribute);
                        // Module methods can be generic, but a module itself can't be generic.
                        DeserializeMethodBody(state, new GenericTypeContext(null, method.GenericParameters), method.MethodBuilder);
                    }

                    moduleDef.ModuleBuilder.CreateGlobalFunctions();
                });
            });

            return moduleDef;
        }

        private PickledGenericType DeserializeGenericInstantiation(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var genericType = DeserializeType(state, typeContext);
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            var genericArguments = new PickledTypeInfo[genericArgumentCount];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericArguments[i] = DeserializeType(state, typeContext);
            }
            var result = new PickledGenericType(genericType, genericArguments);
            AddMemo(state, result);
            return result;
        }

        private PickledTypeInfo DeserializeGenericParameter(PicklerDeserializationState state, bool isTypeParam)
        {
            var genericParameterPosition = state.Reader.Read7BitEncodedInt();
            PickledTypeInfo genericParameter;
            if (isTypeParam)
            {
                var type = DeserializeType(state, default);
                genericParameter = type.GetGenericArgument(genericParameterPosition);
            }
            else
            {
                var method = DeserializeMethodInfo(state, false);
                genericParameter = method.GetGenericArgument(genericParameterPosition);
            }
            AddMemo(state, genericParameter);
            return genericParameter;
        }

        private PickledTypeInfoRef DeserializeTypeRef(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var isNested = state.Reader.ReadBoolean();
            var typeName = state.Reader.ReadString();

            PickledTypeInfoRef result;
            if (isNested)
            {
                var declaringType = DeserializeType(state, typeContext).Type;
                var type = declaringType.GetNestedType(typeName, BindingFlags.Public | BindingFlags.NonPublic);
                if (type == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{declaringType}'");
                }

                result = new PickledTypeInfoRef(type);
            }
            else
            {
                var module = DeserializeModule(state);
                var type = module.Module.GetType(typeName);
                if (type == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{module.Module.FullyQualifiedName}'");
                }

                result = new PickledTypeInfoRef(type);
            }

            AddMemo(state, result);

            return result;
        }

        private PickledTypeInfoDef DeserializeTypeDef(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var typeName = state.Reader.ReadString();
            var typeAttributes = (TypeAttributes)state.Reader.ReadInt32();
            var typeFlags = state.Reader.ReadByte();
            var isNested = (typeFlags & (int)TypeDef.Nested) != 0;
            var typeDef = (TypeDef)(typeFlags & 0x7);
            string[]? genericParameters = null;
            GenericParameterAttributes[]? genericParameterAttributes = null;
            if (typeDef != TypeDef.Enum)
            {
                // Enums never have generic parameters, but anything else might
                var genericParameterCount = state.Reader.Read7BitEncodedInt();
                if (genericParameterCount != 0)
                {
                    genericParameters = new string[genericParameterCount];
                    genericParameterAttributes = new GenericParameterAttributes[genericParameterCount];
                    for (int i = 0; i < genericParameterCount; ++i)
                    {
                        genericParameters[i] = state.Reader.ReadString();
                        genericParameterAttributes[i] = (GenericParameterAttributes)state.Reader.ReadByte();
                    }
                }
            }

            PickledTypeInfoDef constructingType;
            if (isNested)
            {
                var declaringType = (PickledTypeInfoDef)DeserializeType(state, typeContext);
                constructingType = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, declaringType, declaringType.TypeBuilder.DefineNestedType);
            }
            else
            {
                var module = DeserializeModule(state);
                var moduleDef = (PickledModuleDef)module;
                constructingType = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, null, moduleDef.ModuleBuilder.DefineType);
            }

            if (genericParameters != null)
            {
                var genericParameterBuilders = constructingType.TypeBuilder.DefineGenericParameters(genericParameters);
                constructingType.GenericParameters = new PickledGenericParameterDef[genericParameters.Length];
                for (int i = 0; i < genericParameters.Length; ++i)
                {
                    genericParameterBuilders[i].SetGenericParameterAttributes(genericParameterAttributes[i]);
                    constructingType.GenericParameters[i] = new PickledGenericParameterDef(constructingType, genericParameterBuilders[i]);
                }
            }

            state.AddTypeDef(constructingType);
            DeserializeTypeDef(state, constructingType);
            return constructingType;
        }

        private PickledAssembly DeserializeAssembly(PicklerDeserializationState state)
        {
            var operation = (AssemblyOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case AssemblyOperation.Memo:
                    return (PickledAssembly)state.ReadMemo();

                case AssemblyOperation.MscorlibAssembly:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return new PickledAssemblyRef(mscorlib);

                case AssemblyOperation.AssemblyRef:
                    return DeserializeAsesmblyRef(state);

                case AssemblyOperation.AssemblyDef:
                    return DeserializeAssemblyDef(state);
            }

            throw new Exception($"Unexpected operation '{operation}' for Assembly");
        }

        private PickledModule DeserializeModule(PicklerDeserializationState state)
        {
            var operation = (ModuleOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case ModuleOperation.Memo:
                    return (PickledModule)state.ReadMemo();

                case ModuleOperation.MscorlibModule:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return new PickledModuleRef(mscorlib.ManifestModule);

                case ModuleOperation.ManifestModuleRef:
                    return DeserializeManifestModuleRef(state);

                case ModuleOperation.ModuleRef:
                    return DeserializeModuleRef(state);

                case ModuleOperation.ModuleDef:
                    return DeserializeModuleDef(state);
            }

            throw new Exception($"Unexpected operation '{operation}' for Module");
        }

        private PickledTypeInfo DeserializeType(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var operation = (TypeOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case TypeOperation.Memo:
                    {
                        var memo = state.ReadMemo();
                        return (PickledTypeInfo)memo;
                    }

                case TypeOperation.ArrayType:
                    {
                        var rank = state.Reader.ReadByte();
                        var elementType = DeserializeType(state, typeContext);
                        var result = new PickledArrayType(elementType, rank);
                        AddMemo(state, result);
                        return result;
                    }

                case TypeOperation.ByRefType:
                    {
                        var elementType = DeserializeType(state, typeContext);
                        var result = new PickledByRefType(elementType);
                        AddMemo(state, result);
                        return result;
                    }

                case TypeOperation.PointerType:
                    {
                        var elementType = DeserializeType(state, typeContext);
                        var result = new PickledPointerType(elementType);
                        AddMemo(state, result);
                        return result;
                    }

                case TypeOperation.GenericInstantiation:
                    return DeserializeGenericInstantiation(state, typeContext);

                case TypeOperation.GenericTypeParameter:
                case TypeOperation.GenericMethodParameter:
                    return DeserializeGenericParameter(state, operation == TypeOperation.GenericTypeParameter);

                case TypeOperation.MVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (typeContext.GenericMethodParameters == null)
                        {
                            throw new Exception("Encountered an MVar operation without a current method context");
                        }
                        var result = typeContext.GenericMethodParameters[genericParameterPosition];
                        AddMemo(state, result);
                        return result;
                    }

                case TypeOperation.TVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (typeContext.GenericTypeParameters == null)
                        {
                            throw new Exception("Encountered an TVar operation without a current type context");
                        }
                        var result = typeContext.GenericTypeParameters[genericParameterPosition];
                        AddMemo(state, result);
                        return result;
                    }

                case TypeOperation.TypeRef:
                    return DeserializeTypeRef(state, typeContext);

                case TypeOperation.TypeDef:
                    return DeserializeTypeDef(state, typeContext);

                default:
                    foreach (var kv in _wellKnownTypes)
                    {
                        // We don't memoize any of the well known types
                        if (kv.Value == operation) return new PickledTypeInfoRef(kv.Key);
                    }
                    break;
            }

            throw new Exception($"Unexpected operation '{operation}' for Type");
        }

        private PickledMethodBase DeserializeMethodBase(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for MethodBase");

                case ObjectOperation.Memo:
                    return (PickledMethodBase)state.ReadMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for MethodBase");
            }

            var runtimeType = DeserializeType(state, default).Type;

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MethodBase)), "Expected a MethodBase type");

            if (runtimeType.IsAssignableTo(typeof(MethodInfo)))
            {
                return DeserializeMethodInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(ConstructorInfo)))
            {
                return DeserializeConstructorInfo(state, true);
            }

            throw new Exception($"Unexpected type '{runtimeType}' for MethodBase");
        }

        private PickledMemberInfo DeserializeMemberInfo(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for MemberInfo");

                case ObjectOperation.Memo:
                    return (PickledMemberInfo)state.ReadMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for MemberInfo");
            }

            var runtimeType = DeserializeType(state, default).Type;

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MemberInfo)), "Expected a MemberInfo type");

            if (runtimeType.IsAssignableTo(typeof(FieldInfo)))
            {
                return DeserializeFieldInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(PropertyInfo)))
            {
                return DeserializePropertyInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(EventInfo)))
            {
                return DeserializeEventInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(MethodInfo)))
            {
                return DeserializeMethodInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(ConstructorInfo)))
            {
                return DeserializeConstructorInfo(state, true);
            }
            else if (runtimeType.IsAssignableTo(typeof(Type)))
            {
                return DeserializeType(state, default);
            }

            throw new Exception($"Unexpected type '{runtimeType}' for MemberInfo");
        }

        private SerialisedObjectTypeInfo GetOrReadSerialisedObjectTypeInfo(PicklerDeserializationState state, Type type)
        {
            var info = state.HasSeenType(type);
            if (info != null)
            {
                return info;
            }

            // If this is a well known type we need to ensure we get the same ref
            if (_wellKnownTypes.ContainsKey(type) ||
                type == typeof(AssemblyBuilder) ||
                type == typeof(ModuleBuilder) ||
                type == typeof(TypeBuilder) ||
                type == typeof(Pickler))
            {
                return GetCachedTypeInfo(type);
            }

            info = new SerialisedObjectTypeInfo(type);
            state.AddSeenType(type, info);

            // If this is a builtin type there's no need to even write out type flags
            if (IsBuiltinType(type))
            {
                info.Flags =
                    (type.IsValueType ? PickledTypeFlags.IsValueType : 0) |
                    (type.IsSealed ? PickledTypeFlags.IsSealed : 0) |
                    (type.IsAbstract ? PickledTypeFlags.IsAbstract : 0) |
                    (type.HasElementType ? PickledTypeFlags.HasElementType : 0);

                // Assume builtin, we'll type check and change that below.
                info.Mode = PickledTypeMode.IsBuiltin;
            }
            else
            {
                var infoByte = state.Reader.ReadByte();
                var flags = (PickledTypeFlags)(infoByte & 0xF);
                var mode = (PickledTypeMode)(infoByte >> 4);

                info.Flags = flags;
                info.Mode = mode;
            }

            // Fix up abstracts
            if (info.IsAbstract)
            {
                info.Mode = PickledTypeMode.IsBuiltin;
            }

            if (info.Mode == PickledTypeMode.IsError)
            {
                // This type is tagged as Error so we shouldn't actually see any instances of it.
                info.Error = $"{type} was marked unserialisable but was encountered in the pikala stream";
            }
            else if (info.Mode == PickledTypeMode.IsAutoSerialisedObject)
            {
                var currentFields = GetSerializedFields(type);

                var writtenLength = state.Reader.Read7BitEncodedInt();
                var errors = new List<string>();

                // Error out if value/refness has changed.
                if (info.IsValueType != type.IsValueType)
                {
                    var expected = info.IsValueType ? "value type" : "reference type";
                    var actual = type.IsValueType ? "value type" : "reference type";
                    errors.Add($"expected it to be a {expected} but was a {actual}");
                }

                if (currentFields.Length != writtenLength)
                {
                    errors.Add($"serialised {writtenLength} fields but type expects {currentFields.Length}");
                }

                // We still need to read the fields we have written otherwise nothing else can deserialise. And hell we might not even try and read one of these types, it might just 
                // be used as a local or something.
                info.SerialisedFields = new (SerialisedObjectTypeInfo, FieldInfo)[writtenLength];
                for (int i = 0; i < writtenLength; ++i)
                {
                    PickledFieldInfo? fieldInfo;
                    try
                    {
                        fieldInfo = DeserializeFieldInfo(state, false);
                    }
                    catch (MissingFieldException exc)
                    {
                        errors.Add($"could not find expected field '{exc.Field}'");
                        fieldInfo = null;
                    }

                    var fieldType = DeserializeType(state, default).CompleteType;
                    var fieldTypeInfo = GetOrReadSerialisedObjectTypeInfo(state, fieldType);

                    if (fieldInfo != null)
                    {
                        if (fieldInfo.FieldInfo.FieldType != fieldType)
                        {
                            errors.Add($"field '{fieldInfo.Name}' was expected to be a '{fieldType}' but was '{fieldInfo.FieldInfo.FieldType}'");
                        }
                        info.SerialisedFields[i] = (fieldTypeInfo, fieldInfo.FieldInfo);
                    }
                }

                if (errors.Count > 0)
                {
                    info.Error = $"Can not deserialize type '{type}', {string.Join(", ", errors)}";
                    info.SerialisedFields = null;
                }
            }
            else if (info.Mode == PickledTypeMode.IsEnum)
            {
                info.TypeCode = (TypeCode)state.Reader.ReadByte();

                if (!type.IsEnum)
                {
                    info.Error = $"Can not deserialize type '{type}' expected it to be an enumeration type";
                }
                else
                {
                    var typeCode = Type.GetTypeCode(type);
                    if (info.TypeCode != typeCode)
                    {
                        info.Error = $"Can not deserialize type '{type}' expected it to be an enumeration of {info.TypeCode} but was {typeCode}";
                    }
                }
            }
            else if (info.Mode == PickledTypeMode.IsDelegate)
            {
                if (!type.IsAssignableTo(typeof(MulticastDelegate)))
                {
                    info.Error = $"Can not deserialize type '{type}' expected it to be a delegate type";
                }
            }

            else if (IsNullableType(type, out var elementType))
            {
                info.IsNullable = true;
                info.Element = GetOrReadSerialisedObjectTypeInfo(state, elementType);
            }
            else if (info.Flags.HasFlag(PickledTypeFlags.HasElementType))
            {
                info.IsArray = true;
                info.Element = GetOrReadSerialisedObjectTypeInfo(state, type.GetElementType());
            }
            else if (IsTupleType(type))
            {
                info.TupleArguments = type.GetGenericArguments().Select(t => GetOrReadSerialisedObjectTypeInfo(state, t)).ToArray();
            }

            return info;
        }

        // This can't change once a type is loaded, so it's safe to cache across multiple Deserialize methods.
        // TODO: This need to be parallel safe
        private Dictionary<SerialisedObjectTypeInfo, MethodInfo> _deserializationMethods = new Dictionary<SerialisedObjectTypeInfo, MethodInfo>();

        private MethodInfo GetDeserializationMethod(SerialisedObjectTypeInfo type)
        {
            if (_deserializationMethods.TryGetValue(type, out var method))
            {
                return method;
            }

            return BuildDeserializationMethod(type);
        }

        private MethodInfo BuildDeserializationMethod(SerialisedObjectTypeInfo type)
        {
            // Deserialization methods are either (Pickler, PicklerDeserializationState, bool) for reference types.
            // Where the bool parameter is true to say that the null/memo & type has already been checked.
            // Or (Pickler, PicklerDeserializationState, bool) for value types where the bool is if this was a boxed value.
            var dynamicParameters = new Type[] { typeof(Pickler), typeof(PicklerDeserializationState), typeof(bool) };

            // All other types we build a dynamic method for it.
            var dynamicMethod = new DynamicMethod("Deserialize_" + type.Type.Name, type.Type, dynamicParameters, typeof(Pickler));
            _deserializationMethods.Add(type, dynamicMethod);

            var il = dynamicMethod.GetILGenerator();
            // Nearly every type needs access to the Reader property
            var binaryReaderProperty = typeof(PicklerDeserializationState).GetProperty("Reader");
            System.Diagnostics.Debug.Assert(binaryReaderProperty != null, "Could not lookup Reader property");
            var binaryReaderPropertyGet = binaryReaderProperty.GetMethod;
            System.Diagnostics.Debug.Assert(binaryReaderPropertyGet != null, "Reader property had no get method");


            // Most objects need MaybeReadMemo
            var maybeReadMemoMethod = typeof(Pickler).GetMethod(
                "MaybeReadMemo",
                BindingFlags.NonPublic | BindingFlags.Static,
                new Type[] { typeof(PicklerDeserializationState), Type.MakeGenericMethodParameter(0).MakeByRefType() });
            System.Diagnostics.Debug.Assert(maybeReadMemoMethod != null, "Could not lookup MaybeReadMemo method");

            // All object based methods need the ReadObjectOperation, ReadObjectType and AddMemo methods
            MethodInfo? readObjectOperationMethod = null;
            MethodInfo? readObjectTypeMethod = null;
            MethodInfo? addMemoMethod = null;
            if (!type.IsValueType && type.Error == null)
            {
                readObjectOperationMethod = typeof(Pickler).GetMethod("ReadObjectOperation", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(readObjectOperationMethod != null, "Could not lookup ReadObjectOperation method");
                readObjectOperationMethod = readObjectOperationMethod.MakeGenericMethod(type.Type);

                readObjectTypeMethod = typeof(Pickler).GetMethod("ReadObjectType", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Diagnostics.Debug.Assert(readObjectTypeMethod != null, "Could not lookup ReadObjectType method");
                readObjectTypeMethod = readObjectTypeMethod.MakeGenericMethod(type.Type);

                addMemoMethod = typeof(Pickler).GetMethod(
                    "AddMemo",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    new Type[] { Type.MakeGenericMethodParameter(0), typeof(PicklerDeserializationState), typeof(bool) });
                System.Diagnostics.Debug.Assert(addMemoMethod != null, "Could not lookup AddMemo method");
                addMemoMethod = addMemoMethod.MakeGenericMethod(type.Type);
            }

            if (type.Error == null)
            {
                maybeReadMemoMethod = maybeReadMemoMethod.MakeGenericMethod(type.Type);
            }

            if (type.Error != null)
            {
                // This type isn't actually serialisable, if it's null we're ok but otherwise throw.

                var exceptionConstructor = typeof(Exception).GetConstructor(new Type[] { typeof(string) });
                System.Diagnostics.Debug.Assert(exceptionConstructor != null, "Could not lookup Exception constructor");

                var readMethod = typeof(BinaryReader).GetMethod("ReadByte");
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                var earlyReturn = il.DefineLabel();

                // We _might_ have to write out object headers here
                if (!type.IsValueType)
                {
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Brtrue, prechecked);
                    il.Emit(OpCodes.Ldarg_2);
                    // All we care about is nullness, for which we'll write ObjectOperation.Null
                    il.Emit(OpCodes.Brtrue, prechecked);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                    il.Emit(OpCodes.Callvirt, readMethod);
                    il.Emit(OpCodes.Ldc_I4, (int)ObjectOperation.Null);
                    il.Emit(OpCodes.Ceq);
                    il.Emit(OpCodes.Brtrue, earlyReturn);
                    il.MarkLabel(prechecked);
                }

                // Throw the erorr
                il.Emit(OpCodes.Ldstr, type.Error);
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(DBNull))
            {
                // DBNull is easy, just push DBNull.Value
                var valueField = typeof(DBNull).GetField("Value");
                System.Diagnostics.Debug.Assert(valueField != null, "Could not lookup DBNull.Value field");

                il.Emit(OpCodes.Ldsfld, valueField);
                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(UIntPtr))
            {
                // UIntPtr (and IntPtr) just read their 64 bit value
                var readMethod = typeof(BinaryReader).GetMethod("ReadUInt64");
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                var constructor = typeof(UIntPtr).GetConstructor(new Type[] { typeof(ulong) });
                System.Diagnostics.Debug.Assert(constructor != null, "Could not lookup UInt64 constructor");

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                il.Emit(OpCodes.Callvirt, readMethod);
                il.Emit(OpCodes.Newobj, constructor);

                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(IntPtr))
            {
                // UIntPtr (and IntPtr) just read their 64 bit value
                var readMethod = typeof(BinaryReader).GetMethod("ReadInt64");
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                var constructor = typeof(IntPtr).GetConstructor(new Type[] { typeof(long) });
                System.Diagnostics.Debug.Assert(constructor != null, "Could not lookup Int64 constructor");

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                il.Emit(OpCodes.Callvirt, readMethod);
                il.Emit(OpCodes.Newobj, constructor);

                il.Emit(OpCodes.Ret);
            }
            else if (type.Type.IsPrimitive || type.Type == typeof(decimal))
            {
                // Lookup the read method for this type
                var readMethod = typeof(BinaryReader).GetMethod("Read" + type.Type.Name, Type.EmptyTypes);
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                // Primitive type like bool
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                il.Emit(OpCodes.Callvirt, readMethod);

                il.Emit(OpCodes.Ret);
            }
            else if (type.TypeCode != null)
            {
                #region Enumeration
                // This is an enum, lookup the write method for the inner type
                Type enumType;
                switch (type.TypeCode)
                {
                    case TypeCode.SByte:
                        enumType = typeof(sbyte);
                        break;
                    case TypeCode.Int16:
                        enumType = typeof(short);
                        break;
                    case TypeCode.Int32:
                        enumType = typeof(int);
                        break;
                    case TypeCode.Int64:
                        enumType = typeof(long);
                        break;

                    case TypeCode.Byte:
                        enumType = typeof(byte);
                        break;
                    case TypeCode.UInt16:
                        enumType = typeof(ushort);
                        break;
                    case TypeCode.UInt32:
                        enumType = typeof(uint);
                        break;
                    case TypeCode.UInt64:
                        enumType = typeof(ulong);
                        break;

                    default:
                        throw new NotSupportedException($"Invalid type code '{type.TypeCode}' for enumeration");
                }

                var readMethod = typeof(BinaryReader).GetMethod("Read" + enumType.Name);
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                il.Emit(OpCodes.Callvirt, readMethod);

                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Type == typeof(string))
            {
                #region String
                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(typeof(string));

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Brtrue, prechecked);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldloca, resultLocal);
                il.Emit(OpCodes.Call, readObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);
                il.MarkLabel(prechecked);

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                var readMethod = typeof(BinaryReader).GetMethod("ReadString");
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");
                il.Emit(OpCodes.Callvirt, readMethod);

                // Memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Call, addMemoMethod);
                il.Emit(OpCodes.Stloc, resultLocal);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.IsNullable)
            {
                #region Nullable
                // Nullable<T> always writes the same way
                var innerTypeInfo = type.Element;
                System.Diagnostics.Debug.Assert(innerTypeInfo != null, $"{type.Type} was nullable but Element was null");
                var innerMethod = GetDeserializationMethod(innerTypeInfo);

                var readMethod = typeof(BinaryReader).GetMethod("ReadBoolean");
                System.Diagnostics.Debug.Assert(readMethod != null, "Could not lookup read method");

                var constructor = type.Type.GetConstructor(new Type[] { innerTypeInfo.Type });
                System.Diagnostics.Debug.Assert(constructor != null, "Could not lookup constructor");

                var nullReturn = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                il.Emit(OpCodes.Callvirt, readMethod);
                il.Emit(OpCodes.Brfalse, nullReturn);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, innerMethod);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(nullReturn);
                var nullNullable = il.DeclareLocal(type.Type);
                il.Emit(OpCodes.Ldloca, nullNullable);
                il.Emit(OpCodes.Initobj, type.Type);
                il.Emit(OpCodes.Ldloc, nullNullable);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.IsArray)
            {
                #region Array
                // This is an array, write the type then loop over each item.
                // Theres a performance optimisation we could do here with value types,
                // we we fetch the handler only once.

                var elementType = type.Element;
                System.Diagnostics.Debug.Assert(elementType != null, "Element returned null for an array type");
                var innerMethod = GetDeserializationMethod(elementType);

                // Special case szarray (i.e. Rank 1, lower bound 0)
                var isSZ = type.Type.IsSZArray;

                var read7BitMethod = typeof(BinaryReader).GetMethod("Read7BitEncodedInt");
                System.Diagnostics.Debug.Assert(read7BitMethod != null, "Could not lookup Read7BitEncodedInt method");

                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(type.Type);

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Brtrue, prechecked);

                // If not pre-checked we _always_ need to do a memo/null check
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldloca, resultLocal);
                il.Emit(OpCodes.Call, readObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);

                // But we only need to do a type check if this array could be variant.
                // e.g. an int[] location always holds an int[] runtime value, but an object[] location could hold a string[].
                // Unexpectedly a Type[] _must_ contain a Type[] because we don't allow other static type.
                if (!elementType.IsValueType && !elementType.IsSealed)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloca, resultLocal);
                    il.Emit(OpCodes.Callvirt, readObjectTypeMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);
                }

                il.MarkLabel(prechecked);

                // If we get here we know we are trying to write an array of exactly this type.

                LocalBuilder? szLengthLocal = null;
                var dimensions = 1;
                if (isSZ)
                {
                    szLengthLocal = il.DeclareLocal(typeof(int));

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                    il.Emit(OpCodes.Callvirt, read7BitMethod);
                    il.Emit(OpCodes.Stloc, szLengthLocal);

                    il.Emit(OpCodes.Ldloc, szLengthLocal);
                    il.Emit(OpCodes.Newarr, elementType.Type);
                }
                else
                {
                    // This might just be rank 1 but with non-normal bounds
                    dimensions = type.Type.GetArrayRank();

                    // We need to call Array.CreateInstance(elementType, length, lowerBounds)
                    var lengths = il.DeclareLocal(typeof(int[]));
                    il.Emit(OpCodes.Ldc_I4, dimensions);
                    il.Emit(OpCodes.Newarr, typeof(int));
                    il.Emit(OpCodes.Stloc, lengths);

                    var lowerBounds = il.DeclareLocal(typeof(int[]));
                    il.Emit(OpCodes.Ldc_I4, dimensions);
                    il.Emit(OpCodes.Newarr, typeof(int));
                    il.Emit(OpCodes.Stloc, lowerBounds);

                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        il.Emit(OpCodes.Ldloc, lengths);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                        il.Emit(OpCodes.Callvirt, read7BitMethod);
                        il.Emit(OpCodes.Stelem, typeof(int));

                        il.Emit(OpCodes.Ldloc, lowerBounds);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, binaryReaderPropertyGet);
                        il.Emit(OpCodes.Callvirt, read7BitMethod);
                        il.Emit(OpCodes.Stelem, typeof(int));
                    }

                    var createInstance = typeof(Array).GetMethod("CreateInstance", new Type[] { typeof(Type), typeof(int[]), typeof(int[]) });
                    System.Diagnostics.Debug.Assert(createInstance != null, "Could not lookup CreateInstance method");

                    var getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle");
                    System.Diagnostics.Debug.Assert(getTypeFromHandle != null, "Could not lookup GetTypeFromHandle method");

                    il.Emit(OpCodes.Ldtoken, elementType.Type);
                    il.Emit(OpCodes.Call, getTypeFromHandle);
                    il.Emit(OpCodes.Ldloc, lengths);
                    il.Emit(OpCodes.Ldloc, lowerBounds);
                    il.Emit(OpCodes.Call, createInstance);
                }

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Call, addMemoMethod);
                il.Emit(OpCodes.Stloc, resultLocal);

                // Iterate all the items
                if (isSZ)
                {
                    System.Diagnostics.Debug.Assert(szLengthLocal != null, "Length local was not declared for sz array");

                    var startOfLoop = il.DefineLabel();
                    var endOfLoop = il.DefineLabel();
                    var indexLocal = il.DeclareLocal(typeof(int));

                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Stloc, indexLocal);

                    il.MarkLabel(startOfLoop);

                    il.Emit(OpCodes.Ldloc, szLengthLocal);
                    il.Emit(OpCodes.Ldloc, indexLocal);
                    il.Emit(OpCodes.Ceq);
                    il.Emit(OpCodes.Brtrue, endOfLoop);

                    // Read and store the next item
                    il.Emit(OpCodes.Ldloc, resultLocal);
                    il.Emit(OpCodes.Ldloc, indexLocal);
                    // Read
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, innerMethod);
                    // Store
                    il.Emit(OpCodes.Stelem, elementType.Type);
                    // Increment
                    il.Emit(OpCodes.Ldloc, indexLocal);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stloc, indexLocal);

                    il.Emit(OpCodes.Br, startOfLoop);
                    il.MarkLabel(endOfLoop);
                }
                else
                {
                    var getLowerBoundMethod = type.Type.GetMethod("GetLowerBound");
                    System.Diagnostics.Debug.Assert(getLowerBoundMethod != null, "Could not lookup GetLowerBound method");

                    var getUpperBoundMethod = type.Type.GetMethod("GetUpperBound");
                    System.Diagnostics.Debug.Assert(getUpperBoundMethod != null, "Could not lookup GetUpperBound method");

                    var setMethod = type.Type.GetMethod("Set");
                    System.Diagnostics.Debug.Assert(setMethod != null, "Could not lookup Set method");

                    // Copy values dimension by dimension
                    var variables = new (Label, Label, LocalBuilder, LocalBuilder)[dimensions];
                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var startOfLoop = il.DefineLabel();
                        var endOfLoop = il.DefineLabel();
                        var indexLocal = il.DeclareLocal(typeof(int));
                        var upperBoundLocal = il.DeclareLocal(typeof(int));

                        il.Emit(OpCodes.Ldloc, resultLocal);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Callvirt, getUpperBoundMethod);
                        il.Emit(OpCodes.Stloc, upperBoundLocal);

                        variables[dimension] = (startOfLoop, endOfLoop, indexLocal, upperBoundLocal);
                    }

                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var (startOfLoop, endOfLoop, indexLocal, upperBoundLocal) = variables[dimension];

                        // Set the index back to the lower bound for this dimension
                        il.Emit(OpCodes.Ldloc, resultLocal);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Callvirt, getLowerBoundMethod);
                        il.Emit(OpCodes.Stloc, indexLocal);

                        // And start interating until index is greater than the upper bound and then break out the loop
                        il.MarkLabel(startOfLoop);

                        // Jump to end if index greater than upperbound, i.e. loop while index <= upperBound
                        il.Emit(OpCodes.Ldloc, indexLocal);
                        il.Emit(OpCodes.Ldloc, upperBoundLocal);
                        il.Emit(OpCodes.Cgt);
                        il.Emit(OpCodes.Brtrue, endOfLoop);
                    }

                    // Read and store the next item
                    il.Emit(OpCodes.Ldloc, resultLocal);
                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var (_, _, indexLocal, _) = variables[dimension];
                        il.Emit(OpCodes.Ldloc, indexLocal);
                    }
                    // Read
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, innerMethod);
                    // Store
                    il.Emit(OpCodes.Callvirt, setMethod);

                    for (int dimension = dimensions - 1; dimension >= 0; --dimension)
                    {
                        var (startOfLoop, endOfLoop, indexLocal, _) = variables[dimension];

                        // Add one to the index and jump back to the start
                        il.Emit(OpCodes.Ldloc, indexLocal);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Stloc, indexLocal);
                        il.Emit(OpCodes.Br, startOfLoop);

                        il.MarkLabel(endOfLoop);
                    }
                }

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.TupleArguments != null)
            {
                #region Tuple
                // This is either Tuple or ValueTuple
                // N.B This isn't for any ITuple as there might be user defined types that inherit from Tuple and it's not safe to pass them in here.

                // Special case ValueTuple
                if (type.TupleArguments.Length == 0)
                {
                    var valueTuple = il.DeclareLocal(typeof(ValueTuple));
                    il.Emit(OpCodes.Ldloca, valueTuple);
                    il.Emit(OpCodes.Initobj, typeof(ValueTuple));
                    il.Emit(OpCodes.Ldloc, valueTuple);
                    il.Emit(OpCodes.Ret);
                }
                else
                {

                    var resultLocal = il.DeclareLocal(type.Type);

                    var earlyReturn = il.DefineLabel();
                    if (!type.Type.IsValueType)
                    {
                        // We _might_ have to write out object headers here
                        var prechecked = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Brtrue, prechecked);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ldloca, resultLocal);
                        il.Emit(OpCodes.Call, readObjectOperationMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, resultLocal);
                        il.Emit(OpCodes.Callvirt, readObjectTypeMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);

                        il.MarkLabel(prechecked);
                    }

                    var items = new LocalBuilder[type.TupleArguments.Length];
                    var types = new Type[type.TupleArguments.Length];
                    for (int i = 0; i < type.TupleArguments.Length; i++)
                    {
                        types[i] = type.TupleArguments[i].Type;
                        items[i] = il.DeclareLocal(types[i]);
                        var innerMethod = GetDeserializationMethod(type.TupleArguments[i]);

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Call, innerMethod);
                        il.Emit(OpCodes.Stloc, items[i]);

                        // If this is a reference to a tuple (i.e. Tuple, or boxed ValueTuple) then serialising the fields may serialise the tuple itself.
                        var skipMemo = il.DefineLabel();
                        if (type.IsValueType)
                        {
                            il.Emit(OpCodes.Ldarg_2);
                            il.Emit(OpCodes.Brfalse, skipMemo);
                        }
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, resultLocal);
                        il.Emit(OpCodes.Call, maybeReadMemoMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);
                        il.MarkLabel(skipMemo);

                    }

                    for (int i = 0; i < items.Length; i++)
                    {
                        il.Emit(OpCodes.Ldloc, items[i]);
                    }
                    var constructor = type.Type.GetConstructor(types);
                    System.Diagnostics.Debug.Assert(constructor != null, $"Could not lookup {type.Type} constructor");
                    il.Emit(OpCodes.Newobj, constructor);

                    // Memoize the result
                    if (!type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldarg_1);
                        if (type.Type.IsValueType)
                        {
                            il.Emit(OpCodes.Ldarg_2);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldc_I4_1);
                        }
                        il.Emit(OpCodes.Call, addMemoMethod);
                        il.Emit(OpCodes.Stloc, resultLocal);
                    }
                    else
                    {
                        il.Emit(OpCodes.Stloc, resultLocal);
                    }


                    il.MarkLabel(earlyReturn);
                    il.Emit(OpCodes.Ldloc, resultLocal);
                    il.Emit(OpCodes.Ret);
                }
                #endregion
            }
            else if (type.Mode == PickledTypeMode.IsDelegate)
            {
                #region Delegate
                // Delegates are always reference objects, so no worry about boxing here.
                var readDelegateMethod = typeof(Pickler).GetMethod("ReadDelegate", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Diagnostics.Debug.Assert(readDelegateMethod != null, "Could not lookup ReadDelegate method");


                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(type.Type);

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Brtrue, prechecked);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldloca, resultLocal);
                il.Emit(OpCodes.Call, readObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);
                il.MarkLabel(prechecked);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldtoken, type.Type);
                il.Emit(OpCodes.Callvirt, readDelegateMethod);
                il.Emit(OpCodes.Stloc, resultLocal);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.IsAbstract)
            {
                #region Abstract
                // Abstract types must do dynamic dispatch
                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(type.Type);

                var exceptionConstructor = typeof(Exception).GetConstructor(new Type[] { typeof(string) });
                System.Diagnostics.Debug.Assert(exceptionConstructor != null, "Could not lookup Exception constructor");

                // If this say's it's prechecked that's a bug!
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Brfalse, prechecked);
                il.Emit(OpCodes.Ldstr, "Abstract type was called as prechecked");
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);
                il.MarkLabel(prechecked);

                // We always need to do a null/memo check here
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldloca, resultLocal);
                il.Emit(OpCodes.Call, readObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);
                // And a type check because this type is abstract
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloca, resultLocal);
                il.Emit(OpCodes.Callvirt, readObjectTypeMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);

                // If we get here something has gone very wrong
                il.Emit(OpCodes.Ldstr, "Tried to serialize an abstract type");
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Mode == PickledTypeMode.IsReduced)
            {
                #region IReducer
                // Use of an IReducer causes boxing anyway so just cast up to object.
                var readReducerMethod = typeof(Pickler).GetMethod("ReadReducer", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Diagnostics.Debug.Assert(readReducerMethod != null, "Could not lookup ReadReducer method");

                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(type.Type);

                if (!type.IsValueType)
                {
                    // We _might_ have to write out object headers here
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Brtrue, prechecked);

                    // We always need to do a null/memo check here
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Ldloca, resultLocal);
                    il.Emit(OpCodes.Call, readObjectOperationMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    // But we only need to do a type check if the type is not sealed
                    if (!type.IsSealed)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, resultLocal);
                        il.Emit(OpCodes.Callvirt, readObjectTypeMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);
                    }

                    il.MarkLabel(prechecked);
                }

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, readReducerMethod);
                if (type.IsValueType)
                {
                    il.Emit(OpCodes.Unbox, type.Type);
                }
                else
                {
                    il.Emit(OpCodes.Castclass, type.Type);
                }

                // Now memoize the object
                if (!type.IsValueType)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    if (type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldarg_2);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                    }
                    il.Emit(OpCodes.Call, addMemoMethod);
                    il.Emit(OpCodes.Stloc, resultLocal);
                }
                else
                {
                    il.Emit(OpCodes.Stloc, resultLocal);
                }

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Mode == PickledTypeMode.IsBuiltin)
            {
                throw new Exception($"Unhandled built-in type: {type.Type}");
            }
            else
            {
                #region Object
                // Must be an object, try and dump all it's fields
                System.Diagnostics.Debug.Assert(type.SerialisedFields != null, "SerialisedFields was null");

                var earlyReturn = il.DefineLabel();
                var resultLocal = il.DeclareLocal(type.Type);

                if (!type.IsValueType)
                {
                    // We _might_ have to write out object headers here
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Brtrue, prechecked);

                    // We always need to do a null/memo check here
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Ldloca, resultLocal);
                    il.Emit(OpCodes.Call, readObjectOperationMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    // But we only need to do a type check if the type is not sealed
                    if (!type.IsSealed)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloca, resultLocal);
                        il.Emit(OpCodes.Callvirt, readObjectTypeMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);
                    }

                    il.MarkLabel(prechecked);
                }

                // If this is a value type we can just use initobj, else we need to use System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject
                if (type.Type.IsValueType)
                {
                    il.Emit(OpCodes.Ldloca, resultLocal);
                    il.Emit(OpCodes.Initobj, type.Type);
                    il.Emit(OpCodes.Ldloc, resultLocal);
                }
                else
                {
                    var getUninitializedObject = typeof(RuntimeHelpers).GetMethod("GetUninitializedObject");
                    System.Diagnostics.Debug.Assert(getUninitializedObject != null, "Could not lookup GetUninitializedObject method");

                    var getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle");
                    System.Diagnostics.Debug.Assert(getTypeFromHandle != null, "Could not lookup GetTypeFromHandle method");

                    il.Emit(OpCodes.Ldtoken, type.Type);
                    il.Emit(OpCodes.Call, getTypeFromHandle);
                    il.Emit(OpCodes.Call, getUninitializedObject);
                }

                // Now memoize the object
                if (!type.IsValueType)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    if (type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldarg_2);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                    }
                    il.Emit(OpCodes.Call, addMemoMethod);
                    il.Emit(OpCodes.Stloc, resultLocal);
                }
                else
                {
                    il.Emit(OpCodes.Stloc, resultLocal);
                }

                foreach (var (fieldType, field) in type.SerialisedFields)
                {
                    var innerMethod = GetDeserializationMethod(fieldType);

                    if (type.Type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldloca, resultLocal);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldloc, resultLocal);
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, innerMethod);
                    il.Emit(OpCodes.Stfld, field);
                }

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ldloc, resultLocal);
                il.Emit(OpCodes.Ret);
                #endregion
            }

            return dynamicMethod;
        }

        private object? InvokeDeserializationMethod(SerialisedObjectTypeInfo typeInfo, PicklerDeserializationState state, bool prechecked)
        {
            var deserializationMethod = GetDeserializationMethod(typeInfo);
            try
            {
                if (typeInfo.IsValueType)
                {
                    var value = deserializationMethod.Invoke(null, new object[] { this, state, prechecked });
                    System.Diagnostics.Debug.Assert(value != null, "value type was null");
                    if (prechecked)
                    {
                        // if prechecked is true this is being called as part of parsing an Object, and so the value should be boxed
                        AddMemo(state, value);
                    }
                    return value;
                }
                else
                {
                    return deserializationMethod.Invoke(null, new object[] { this, state, prechecked });
                }
            }
            catch (TargetInvocationException exc)
            {
                System.Diagnostics.Debug.Assert(exc.InnerException != null, "TargetInvocationException.InnerException was null");
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exc.InnerException);
                return null; // Unreachable
            }
        }

        /// <summary>
        /// ReadObjectOperation deals with the common logic that all reference types need to deal with, that is if it's null or memo'd.
        /// </summary>
        private static bool ReadObjectOperation<T>(PicklerDeserializationState state, bool unpickle, out T? obj)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();

            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    {
                        obj = default(T);
                        return true;
                    }

                case ObjectOperation.Memo:
                    {
                        var memo = state.ReadMemo();
                        if (unpickle && memo is PickledObject pickled)
                        {
                            memo = pickled.Get();
                        }
                        obj = (T)memo;
                        return true;
                    }

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unhandled ObjectOperation '{objectOperation}'");
            }

            obj = default(T);
            return false;
        }

        /// <summary>
        /// ReadObjectType gets the runtime type of obj, read the TypeInfo for it if needed, rechecks the memo state,
        /// and then checks if it's the expected type. If not it dynamic dispatchs to correct method.
        /// </summary>
        private bool ReadObjectType<T>(PicklerDeserializationState state, [NotNullWhen(true)] out T? obj)
        {
            var pickledType = DeserializeType(state, default);
            state.Stages.PopStages(state, 3);
            var type = pickledType.CompleteType;
            var typeInfo = GetOrReadSerialisedObjectTypeInfo(state, type);
            state.Stages.PopStages(state, 4);

            if (MaybeReadMemo(state, out obj)) return true;

            // If runtimeType == expected then return that this expected type needs reading,
            // else dynamic dispatch to the correct type but tell it headers are already set
            if (type == typeof(T))
            {
                obj = default(T);
                return false;
            }

            obj = (T)InvokeDeserializationMethod(typeInfo, state, true)!;
            return true;
        }

        private object ReadReducer(PicklerDeserializationState state)
        {
            var method = DeserializeMethodBase(state);

            object? target;
            if (method is PickledConstructorInfo)
            {
                target = null;
            }
            else if (method is PickledMethodInfo)
            {
                target = Deserialize_Object(this, state, false);
            }
            else
            {
                throw new Exception($"Invalid reduction MethodBase was '{method}'.");
            }

            var args = new object?[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < args.Length; ++i)
            {
                var arg = Deserialize_Object(this, state, false);
                args[i] = arg;
            }

            var result = method.Invoke(target, args);
            if (result == null)
            {
                throw new Exception($"Invalid reducer method, '{method}' returned null.");
            }
            return result;
        }

        private Delegate ReadDelegate(PicklerDeserializationState state, RuntimeTypeHandle delegateType)
        {
            Delegate? earlyResult;
            var invocationCount = state.Reader.Read7BitEncodedInt();
            if (invocationCount == 1)
            {
                var target = Deserialize_Object(this, state, false);
                if (MaybeReadMemo(state, out earlyResult)) return earlyResult;

                var method = Deserialize_MethodInfo(this, state, false);
                if (MaybeReadMemo(state, out earlyResult)) return earlyResult;

                var result = Delegate.CreateDelegate(Type.GetTypeFromHandle(delegateType), target, method);
                AddMemo(state, result);
                return result;
            }
            else
            {
                var invocationList = new Delegate[invocationCount];
                for (int i = 0; i < invocationList.Length; ++i)
                {
                    invocationList[i] = Deserialize_Delegate(this, state, false)!;
                    if (MaybeReadMemo(state, out earlyResult)) return earlyResult;
                }

                var result = Delegate.Combine(invocationList)!;
                AddMemo(state, result);
                return result;
            }
        }

        #region Built in serialization methods
        private static object? Deserialize_Object(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            // It's known that this IS a System.Object and it's not null or memo'd
            if (!prechecked)
            {
                if (ReadObjectOperation<object>(state, true, out var obj))
                {
                    return obj;
                }
                if (self.ReadObjectType(state, out obj))
                {
                    return obj;
                }
            }
            // Don't need to actually read anything for System.Object
            var result = new object();
            AddMemo(state, result);
            return result;
        }

        private static Delegate? Deserialize_Delegate(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            System.Diagnostics.Debug.Assert(!prechecked, "Deserialize_Delegate was called as prechecked");

            if (ReadObjectOperation<Delegate>(state, true, out var obj))
            {
                return obj;
            }
            if (self.ReadObjectType(state, out obj))
            {
                return obj;
            }

            throw new Exception("Tried to serialize an abstract Delegate");
        }

        private static Type? Deserialize_Type(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<Type>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledType = self.DeserializeType(state, default);
            state.Stages.PopStages(state);
            return pickledType.CompleteType;
        }

        private static Module? Deserialize_Module(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<Module>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledModule = self.DeserializeModule(state);
            state.Stages.PopStages(state);
            return pickledModule.Module;
        }

        private static Assembly? Deserialize_Assembly(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<Assembly>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledAssembly = self.DeserializeAssembly(state);
            state.Stages.PopStages(state);
            return pickledAssembly.Assembly;
        }

        private static MethodInfo? Deserialize_MethodInfo(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<MethodInfo>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledMethodInfo = self.DeserializeMethodInfo(state, true);
            state.Stages.PopStages(state);
            return pickledMethodInfo.MethodInfo;
        }

        private static DynamicMethod? Deserialize_DynamicMethod(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<DynamicMethod>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledDynamicMethod = self.DeserializeMethodInfo(state, true);
            state.Stages.PopStages(state);
            return pickledDynamicMethod.MethodInfo as DynamicMethod;
        }

        private static ConstructorInfo? Deserialize_ConstructorInfo(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<ConstructorInfo>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledConstructorInfo = self.DeserializeConstructorInfo(state, true);
            state.Stages.PopStages(state);
            return pickledConstructorInfo.ConstructorInfo;
        }

        private static FieldInfo? Deserialize_FieldInfo(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<FieldInfo>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledFieldInfo = self.DeserializeFieldInfo(state, true);
            state.Stages.PopStages(state);
            return pickledFieldInfo.FieldInfo;
        }

        private static PropertyInfo? Deserialize_PropertyInfo(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<PropertyInfo>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledPropertyInfo = self.DeserializePropertyInfo(state, true);
            state.Stages.PopStages(state);
            return pickledPropertyInfo.PropertyInfo;
        }

        private static EventInfo? Deserialize_EventInfo(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<EventInfo>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var pickledEventInfo = self.DeserializeEventInfo(state, true);
            state.Stages.PopStages(state);
            return pickledEventInfo.EventInfo;
        }

        private static Pickler? Deserialize_Pickler(Pickler self, PicklerDeserializationState state, bool prechecked)
        {
            if (!prechecked)
            {
                if (ReadObjectOperation<Pickler>(state, true, out var obj))
                {
                    return obj;
                }
            }

            var result = new Pickler();
            AddMemo(state, result);
            return result;
        }
        #endregion

        public object? Deserialize(Stream stream)
        {
            using var state = new PicklerDeserializationState(stream);
            // Firstly read the header to make sure it looks like a Pikala stream
            var header = state.Reader.ReadUInt32();
            if (header != _header)
            {
                throw new InvalidDataException("Input stream does not start with PKLA");
            }
            var majorVersion = state.Reader.Read7BitEncodedInt();
            var minorVersion = state.Reader.Read7BitEncodedInt();
            if (majorVersion != _pikalaVersion.Major)
            {
                // TOOD We want to support backwards compatability one day so this will change to a majorVersion <= _pikalaVersion.Major
                throw new InvalidDataException($"Input stream does not match expected version. Got {majorVersion}, expected {_pikalaVersion.Major}");
            }

            var runtimeMajor = state.Reader.Read7BitEncodedInt();
            var runtimeMinor = state.Reader.Read7BitEncodedInt();

            var result = Deserialize_Object(this, state, false);
            state.Stages.AssertEmpty();
            return result;
        }
    }
}
