using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    using DeserializationStage3 = Action<PicklerDeserializationState>;
    using DeserializationStage2 = Func<PicklerDeserializationState, Action<PicklerDeserializationState>>;

    public sealed partial class Pickler
    {
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

                case SignatureElementOperation.Modreq:
                    {
                        var elementType = DeserializeSignatureElement(state);
                        var modifier = DeserializeType(state, default);
                        return new SignatureReq(elementType, modifier.Type);
                    }

                case SignatureElementOperation.Modopt:
                    {
                        var elementType = DeserializeSignatureElement(state);
                        var modifier = DeserializeType(state, default);
                        return new SignatureOpt(elementType, modifier.Type);
                    }
            }

            throw new NotImplementedException($"Unhandled SignatureElement: {operation}");
        }

        private Signature DeserializeSignature(PicklerDeserializationState state)
        {
            var name = state.Reader.ReadString();
            var callingConvention = (CallingConventions)state.Reader.ReadByte();
            var genericParameterCount = state.Reader.Read7BitEncodedInt();
            var returnType = DeserializeSignatureElement(state);
            var parameters = new SignatureElement[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < parameters.Length; ++i)
            {
                parameters[i] = DeserializeSignatureElement(state);
            }
            return new Signature(name, callingConvention, genericParameterCount, returnType, parameters);
        }

        private void DeserializeConstructorHeader(PicklerDeserializationState state, Type[]? genericTypeParameters, PickledTypeInfoDef constructingType, out PickledConstructorInfoDef constructingConstructor)
        {
            var typeContext = new GenericTypeContext(genericTypeParameters);
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var callingConvention = (CallingConventions)state.Reader.ReadByte();

            var parameterCount = state.Reader.Read7BitEncodedInt();
            var hasModifiers = (parameterCount & 0x1) != 0;
            parameterCount >>= 1;

            Type[]? parameterTypes = null;
            Type[][]? requiredCustomModifiers = null;
            Type[][]? optionalCustomModifiers = null;
            if (parameterCount != 0)
            {
                parameterTypes = new Type[parameterCount];
                if (hasModifiers)
                {
                    requiredCustomModifiers = new Type[parameterCount][];
                    optionalCustomModifiers = new Type[parameterCount][];
                }

                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeType(state, typeContext);
                    parameterTypes[j] = parameterType.Type;

                    if (hasModifiers)
                    {
                        var mods = state.Reader.ReadByte();
                        var reqmodCount = mods >> 4;
                        var optmodCount = mods & 0xF;

                        requiredCustomModifiers[j] = new Type[reqmodCount];
                        optionalCustomModifiers[j] = new Type[optmodCount];

                        for (int k = 0; k < reqmodCount; ++k)
                        {
                            requiredCustomModifiers[j][k] = DeserializeType(state, typeContext).Type;
                        }
                        for (int k = 0; k < optmodCount; ++k)
                        {
                            optionalCustomModifiers[j][k] = DeserializeType(state, typeContext).Type;
                        }
                    }
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

        private void DeserializeMethodHeader(PicklerDeserializationState state, Type[]? genericTypeParameters, PickledTypeInfoDef constructingType, ref PickledMethodInfoDef constructingMethod)
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
            var genericParameters = methodGenericParameterNames.Length == 0 ? null : methodBuilder.DefineGenericParameters(methodGenericParameterNames);

            var typeContext = new GenericTypeContext(genericTypeParameters, genericParameters);

            var returnType = DeserializeType(state, typeContext).Type;
            Type[]? returnTypeRequiredCustomModifiers;
            Type[]? returnTypeOptionalCustomModifiers;

            {
                var mods = state.Reader.ReadByte();
                var reqmodCount = mods >> 4;
                var optmodCount = mods & 0xF;

                returnTypeRequiredCustomModifiers = new Type[reqmodCount];
                returnTypeOptionalCustomModifiers = new Type[optmodCount];

                for (int k = 0; k < reqmodCount; ++k)
                {
                    returnTypeRequiredCustomModifiers[k] = DeserializeType(state, typeContext).Type;
                }
                for (int k = 0; k < optmodCount; ++k)
                {
                    returnTypeOptionalCustomModifiers[k] = DeserializeType(state, typeContext).Type;
                }
            }

            var parameterCount = state.Reader.Read7BitEncodedInt();
            var hasModifiers = (parameterCount & 0x1) != 0;
            parameterCount >>= 1;

            Type[]? parameterTypes = null;
            Type[][]? parameterTypeRequiredCustomModifiers = null;
            Type[][]? parameterTypeOptionalCustomModifiers = null;
            if (parameterCount != 0)
            {
                parameterTypes = new Type[parameterCount];
                if (hasModifiers)
                {
                    parameterTypeRequiredCustomModifiers = new Type[parameterCount][];
                    parameterTypeOptionalCustomModifiers = new Type[parameterCount][];
                }

                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeType(state, typeContext);
                    parameterTypes[j] = parameterType.Type;

                    if (hasModifiers)
                    {
                        var mods = state.Reader.ReadByte();
                        var reqmodCount = mods >> 4;
                        var optmodCount = mods & 0xF;

                        parameterTypeRequiredCustomModifiers[j] = new Type[reqmodCount];
                        parameterTypeOptionalCustomModifiers[j] = new Type[optmodCount];

                        for (int k = 0; k < reqmodCount; ++k)
                        {
                            parameterTypeRequiredCustomModifiers[j][k] = DeserializeType(state, typeContext).Type;
                        }
                        for (int k = 0; k < optmodCount; ++k)
                        {
                            parameterTypeOptionalCustomModifiers[j][k] = DeserializeType(state, typeContext).Type;
                        }
                    }
                }
            }

            methodBuilder.SetSignature(
                returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                parameterTypes, parameterTypeRequiredCustomModifiers, parameterTypeOptionalCustomModifiers);

            constructingMethod = new PickledMethodInfoDef(constructingType, methodBuilder);
            constructingMethod.ParameterTypes = parameterTypes;
            constructingMethod.GenericParameters = genericParameters;

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
                            var fieldInfo = DeserializeFieldInfo(state);
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
            if (constantType == typeof(object))
            {
                // This has to be null
                return null;
            }
            else if (constantType == typeof(string))
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
                throw new NotImplementedException($"Unrecognized type '{constantType}' for constant");
            }
        }

        private void DeserializeTypeDefComplex(PicklerDeserializationState state, PickledTypeInfoDef constructingType, DeserializationStage2? parentStage2)
        {
            var isValueType = constructingType.TypeDef == TypeDef.Struct;
            var isInterface = constructingType.TypeDef == TypeDef.Interface;
            var typeBuilder = constructingType.TypeBuilder;

            var typeContext = new GenericTypeContext(constructingType.GenericParameters);

            var baseTypes = new List<PickledTypeInfo>();

            if (!isValueType && !isInterface)
            {
                var baseType = DeserializeType(state, typeContext);
                baseTypes.Add(baseType);
                typeBuilder.SetParent(baseType.Type);
            }

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

            var parentStage3 = InvokeStage(state, parentStage2);

            state.PushTrailer(() =>
            {
                InvokeStage(state, parentStage3);

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

            },
            null,
            () =>
            {
                var type = constructingType.Type;

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
                        }
                    }

                    if (fieldInfo == null)
                    {
                        throw new Exception($"Could not find static field '{fieldName}' on type '{type.Name}'");
                    }

                    var fieldValue = Deserialize(state, fieldInfo.FieldType);
                    fieldInfo.SetValue(null, fieldValue);
                }
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

        private void DeserializeTypeDef(PicklerDeserializationState state, PickledTypeInfoDef constructingType, DeserializationStage2? parentStage2)
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

                InvokeStage(state, InvokeStage(state, parentStage2));

                ReadCustomAttributes(state, typeBuilder.SetCustomAttribute);

                constructingType.FullyDefined = true;
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

                InvokeStage(state, InvokeStage(state, parentStage2));

                constructingType.FullyDefined = true;
            }
            else
            {
                DeserializeTypeDefComplex(state, constructingType, parentStage2);
            }
        }

        private PickledFieldInfo DeserializeFieldRef(PicklerDeserializationState state, long position)
        {
            return state.RunWithTrailers(() =>
            {
                var name = state.Reader.ReadString();
                var type = DeserializeType(state, default);
                return state.SetMemo(position, true, type.GetField(name));
            });
        }

        private PickledPropertyInfo DeserializePropertyRef(PicklerDeserializationState state, long position)
        {
            return state.RunWithTrailers(() =>
            {
                var signature = DeserializeSignature(state);
                var type = DeserializeType(state, default);
                return state.SetMemo(position, true, type.GetProperty(signature));
            });
        }

        private PickledEventInfo DeserializeEventRef(PicklerDeserializationState state, long position)
        {
            return state.RunWithTrailers(() =>
            {
                var name = state.Reader.ReadString();
                var type = DeserializeType(state, default);
                return state.SetMemo(position, true, type.GetEvent(name));
            });
        }

        private PickledConstructorInfo DeserializeConstructorRef(PicklerDeserializationState state, long position)
        {
            return state.RunWithTrailers(() =>
            {
                var signature = DeserializeSignature(state);
                var type = DeserializeType(state, default);
                return state.SetMemo(position, true, type.GetConstructor(signature));
            });
        }

        private PickledMethodInfo DeserializeMethodRef(PicklerDeserializationState state, long position)
        {
            return state.RunWithTrailers(() =>
            {
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

                var type = DeserializeType(state, default);

                var methodInfo = type.GetMethod(signature);

                if (genericArguments != null)
                {
                    return state.SetMemo(position, true, new ConstructingGenericMethod(methodInfo, genericArguments));
                }
                return state.SetMemo(position, true, methodInfo);
            });
        }

        private object? MaybeReadMemo(PicklerDeserializationState state, long position)
        {
            var offset = state.Reader.Read15BitEncodedLong();
            if (offset == 0) return null;
            return state.DoMemo(position, offset);
        }

        private object DeserializeDelegate(PicklerDeserializationState state, long position, Type delegateType)
        {
            object? earlyResult;
            var invocationCount = state.Reader.Read7BitEncodedInt();
            if (invocationCount == 1)
            {
                var target = Deserialize(state, typeof(object));
                earlyResult = MaybeReadMemo(state, position);
                if (earlyResult != null) return earlyResult;

                var method = DeserializeMethodInfo(state);

                earlyResult = MaybeReadMemo(state, position);
                if (earlyResult != null) return earlyResult;

                return state.SetMemo(position, true, Delegate.CreateDelegate(delegateType, target, method.MethodInfo));
            }
            else
            {
                var invocationList = new Delegate[invocationCount];
                for (int i = 0; i < invocationList.Length; ++i)
                {
                    invocationList[i] = (Delegate)Deserialize(state, typeof(Delegate))!;

                    earlyResult = MaybeReadMemo(state, position);
                    if (earlyResult != null) return earlyResult;
                }
                return state.SetMemo(position, true, Delegate.Combine(invocationList)!);
            }
        }

        private PickledAssemblyRef DeserializeAsesmblyRef(PicklerDeserializationState state, long position)
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
            return state.SetMemo(position, ShouldMemo(assembly), new PickledAssemblyRef(assembly));
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
                var constructor = DeserializeConstructorInfo(state);

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

        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("stage2")]
        private DeserializationStage3? InvokeStage(PicklerDeserializationState state, DeserializationStage2? stage2)
        {
            if (stage2 == null) return null;
            return stage2(state);
        }

        private void InvokeStage(PicklerDeserializationState state, DeserializationStage3? stage3)
        {
            if (stage3 != null)
            {
                stage3(state);
            }
        }

        private (PickledAssemblyDef, DeserializationStage3) DeserializeAssemblyDef(PicklerDeserializationState state, long position)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            var access = AssemblyLoadContext.IsCollectible ? AssemblyBuilderAccess.RunAndCollect : AssemblyBuilderAccess.Run;
            var assemblyBuilder = AssemblyLoadContext.DefineDynamicAssembly(assemblyName, access);
            if (assemblyBuilder == null)
            {
                throw new Exception($"Could not define assembly '{assemblyName}'");
            }

            var assemblyDef = state.SetMemo(position, true, new PickledAssemblyDef(assemblyBuilder));

            return (assemblyDef, state =>
            {
                ReadCustomAttributes(state, assemblyDef.AssemblyBuilder.SetCustomAttribute);
            }
            );
        }

        private PickledModuleRef DeserializeManifestModuleRef(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
        {
            var (assembly, assemblyTrailer) = DeserializeAssembly(state, typeContext);
            System.Diagnostics.Debug.Assert(assemblyTrailer == null, "Expected assembly trailer to be null");
            return state.SetMemo(position, ShouldMemo(assembly), new PickledModuleRef(assembly.Assembly.ManifestModule));
        }

        private PickledModuleRef DeserializeModuleRef(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
        {
            var name = state.Reader.ReadString();
            var (assembly, assemblyTrailer) = DeserializeAssembly(state, typeContext);
            System.Diagnostics.Debug.Assert(assemblyTrailer == null, "Expected assembly trailer to be null");
            var module = assembly.Assembly.GetModule(name);
            if (module == null)
            {
                throw new Exception($"Could not load module '{name}' from assembly '{assembly}'");
            }
            return state.SetMemo(position, true, new PickledModuleRef(module));
        }

        private (PickledModuleDef, DeserializationStage2?) DeserializeModuleDef(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
        {
            var name = state.Reader.ReadString();
            var (assembly, assemblyTrailer) = DeserializeAssembly(state, typeContext);
            System.Diagnostics.Debug.Assert(assembly is PickledAssemblyDef, "Expected ModuleDef assembly to be an AssemblyBuilder");

            var assemblyDef = (PickledAssemblyDef)assembly;
            var assemblyBuilder = assemblyDef.AssemblyBuilder;
            var module = assemblyBuilder.DefineDynamicModule(name);
            if (module == null)
            {
                throw new Exception($"Could not create module '{name}' in assembly '{assembly}'");
            }
            var moduleDef = new PickledModuleDef(module);
            state.SetMemo(position, true, moduleDef);

            return (moduleDef, state =>
            {
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
                        fieldBuilder = moduleDef.ModuleBuilder.DefineUninitializedData(fieldName, -fieldSize, fieldAttributes);
                    }
                    else
                    {
                        var data = state.Reader.ReadBytes(fieldSize);
                        fieldBuilder = moduleDef.ModuleBuilder.DefineInitializedData(fieldName, data, fieldAttributes);
                    }

                    // TODO This isn't right, FieldInfo needs to handle that it might be on a module
                    fields[i] = new PickledFieldInfoDef(null!, fieldBuilder);
                }

                var methodCount = state.Reader.Read7BitEncodedInt();
                var methods = new PickledMethodInfoDef[methodCount];
                for (int i = 0; i < methodCount; ++i)
                {
                    throw new NotImplementedException();
                    //DeserializeMethodHeader(state, null, constructingModule, ref methods[i]);
                    //ReadCustomAttributes(state, method.SetCustomAttribute, typeContext);
                }


                return state =>
                {
                    InvokeStage(state, assemblyTrailer);

                    ReadCustomAttributes(state, moduleDef.ModuleBuilder.SetCustomAttribute);

                    foreach (var field in fields)
                    {
                        ReadCustomAttributes(state, field.FieldBuilder.SetCustomAttribute);
                    }

                    foreach (var method in methods)
                    {
                        ReadCustomAttributes(state, method.MethodBuilder.SetCustomAttribute);
                        // Module methods can be generic, but a module itself can't be generic.
                        DeserializeMethodBody(state, new GenericTypeContext(null, method.GenericParameters), method.MethodBuilder);
                    }

                    moduleDef.ModuleBuilder.CreateGlobalFunctions();
                };
            }
            );
        }

        private PickledGenericType DeserializeGenericInstantiation(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
        {
            var genericType = DeserializeType(state, typeContext);
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            var genericArguments = new PickledTypeInfo[genericArgumentCount];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericArguments[i] = DeserializeType(state, typeContext);
            }
            return state.SetMemo(position, true, new PickledGenericType(genericType, genericArguments));
        }

        private PickledTypeInfo DeserializeGenericParameter(PicklerDeserializationState state, long position, bool isTypeParam)
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
                var method = DeserializeMethodInfo(state);
                genericParameter = method.GetGenericArgument(genericParameterPosition);
            }
            return state.SetMemo(position, true, genericParameter);
        }

        private PickledTypeInfoRef DeserializeTypeRef(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
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
                var (module, moduleTrailer) = DeserializeModule(state, typeContext);
                System.Diagnostics.Debug.Assert(moduleTrailer == null, "Expected module trailer to be null");

                var type = module.Module.GetType(typeName);
                if (type == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{module.Module.FullyQualifiedName}'");
                }

                result = new PickledTypeInfoRef(type);
            }

            state.SetMemo(position, true, result);

            return result;
        }

        private PickledTypeInfoDef DeserializeTypeDef(PicklerDeserializationState state, long position, GenericTypeContext typeContext)
        {
            return state.RunWithTrailers(() =>
            {
                var typeName = state.Reader.ReadString();
                var typeAttributes = (TypeAttributes)state.Reader.ReadInt32();
                var typeFlags = state.Reader.ReadByte();
                var isNested = (typeFlags & (int)TypeDef.Nested) != 0;
                var typeDef = (TypeDef)(typeFlags & 0x7);
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

                PickledTypeInfoDef constructingType;
                DeserializationStage2? parentTrailer;
                if (isNested)
                {
                    var callback = state.RegisterMemoCallback(position, (PickledTypeInfoDef declaringType) =>
                    {
                        var result = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, declaringType, declaringType.TypeBuilder.DefineNestedType);

                        if (genericParameters != null)
                        {
                            result.GenericParameters = result.TypeBuilder.DefineGenericParameters(genericParameters);
                        }

                        state.AddTypeDef(result);
                        return state.SetMemo(position, true, result);
                    });
                    parentTrailer = null;
                    var _ = DeserializeType(state, typeContext);
                    constructingType = callback.Invoke();
                    parentTrailer = null;
                }
                else
                {
                    var (module, moduleTrailer) = DeserializeModule(state, typeContext);
                    var moduleDef = (PickledModuleDef)module;
                    constructingType = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, null, moduleDef.ModuleBuilder.DefineType);

                    if (genericParameters != null)
                    {
                        constructingType.GenericParameters = constructingType.TypeBuilder.DefineGenericParameters(genericParameters);
                    }

                    state.AddTypeDef(constructingType);
                    state.SetMemo(position, true, constructingType);
                    parentTrailer = moduleTrailer;
                }
                DeserializeTypeDef(state, constructingType, parentTrailer);
                return constructingType;
            });
        }

        private (PickledAssembly, DeserializationStage3?) DeserializeAssembly(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var position = state.Reader.BaseStream.Position;
            var operation = (AssemblyOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case AssemblyOperation.Memo:
                    return ((PickledAssembly)state.DoMemo(), null);

                case AssemblyOperation.MscorlibAssembly:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return (new PickledAssemblyRef(mscorlib), null);

                case AssemblyOperation.AssemblyRef:
                    return (DeserializeAsesmblyRef(state, position), null);

                case AssemblyOperation.AssemblyDef:
                    return DeserializeAssemblyDef(state, position);
            }

            throw new Exception($"Unexpected operation '{operation}' for Assembly");
        }

        private (PickledModule, DeserializationStage2?) DeserializeModule(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var position = state.Reader.BaseStream.Position;
            var operation = (ModuleOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case ModuleOperation.Memo:
                    return ((PickledModule)state.DoMemo(), null);

                case ModuleOperation.MscorlibModule:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return (new PickledModuleRef(mscorlib.ManifestModule), null);

                case ModuleOperation.ManifestModuleRef:
                    return (DeserializeManifestModuleRef(state, position, typeContext), null);

                case ModuleOperation.ModuleRef:
                    return (DeserializeModuleRef(state, position, typeContext), null);

                case ModuleOperation.ModuleDef:
                    return DeserializeModuleDef(state, position, typeContext);
            }

            throw new Exception($"Unexpected operation '{operation}' for Module");
        }

        private PickledTypeInfo DeserializeType(PicklerDeserializationState state, GenericTypeContext typeContext)
        {
            var position = state.Reader.BaseStream.Position;
            var operation = (TypeOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case TypeOperation.Memo:
                    return (PickledTypeInfo)state.DoMemo();

                case TypeOperation.ArrayType:
                    {
                        var rank = state.Reader.ReadByte();
                        var memo = state.RegisterMemoCallback(position, (PickledTypeInfo elementType) =>
                        {
                            return state.SetMemo(position, true, new PickledArrayType(elementType, rank));
                        });
                        var _ = DeserializeType(state, typeContext);
                        return memo.Invoke();
                    }

                case TypeOperation.GenericInstantiation:
                    return DeserializeGenericInstantiation(state, position, typeContext);

                case TypeOperation.GenericTypeParameter:
                case TypeOperation.GenericMethodParameter:
                    return DeserializeGenericParameter(state, position, operation == TypeOperation.GenericTypeParameter);

                case TypeOperation.MVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (typeContext.GenericMethodParameters == null)
                        {
                            throw new Exception("Encountered an MVar operation without a current method context");
                        }
                        return state.SetMemo(position, true, PickledTypeInfo.FromType(typeContext.GenericMethodParameters[genericParameterPosition]));
                    }

                case TypeOperation.TVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (typeContext.GenericTypeParameters == null)
                        {
                            throw new Exception("Encountered an TVar operation without a current type context");
                        }
                        return state.SetMemo(position, true, PickledTypeInfo.FromType(typeContext.GenericTypeParameters[genericParameterPosition]));
                    }

                case TypeOperation.TypeRef:
                    return DeserializeTypeRef(state, position, typeContext);

                case TypeOperation.TypeDef:
                    return DeserializeTypeDef(state, position, typeContext);

                default:
                    foreach (var kv in _wellKnownTypes)
                    {
                        if (kv.Value == operation) return state.SetMemo(position, true, new PickledTypeInfoRef(kv.Key));
                    }
                    break;
            }

            throw new Exception($"Unexpected operation '{operation}' for Type");
        }

        private PickledFieldInfo DeserializeFieldInfo(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for FieldInfo");

                case ObjectOperation.Memo:
                    return (PickledFieldInfo)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for FieldInfo");
            }

            var position = state.Reader.BaseStream.Position;
            return DeserializeFieldRef(state, position);
        }

        private PickledConstructorInfo DeserializeConstructorInfo(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for ConstructorInfo");

                case ObjectOperation.Memo:
                    return (PickledConstructorInfo)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for ConstructorInfo");
            }

            var position = state.Reader.BaseStream.Position;
            return DeserializeConstructorRef(state, position);
        }

        private PickledMethodInfo DeserializeMethodInfo(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for MethodInfo");

                case ObjectOperation.Memo:
                    return (PickledMethodInfo)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for MethodInfo");
            }

            var position = state.Reader.BaseStream.Position;
            return DeserializeMethodRef(state, position);
        }

        private PickledMethodBase DeserializeMethodBase(PicklerDeserializationState state)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for MethodBase");

                case ObjectOperation.Memo:
                    return (PickledMethodBase)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for MethodBase");
            }

            var runtimeType = DeserializeType(state, default).Type;

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MethodBase)), "Expected a MethodBase type");

            var position = state.Reader.BaseStream.Position;
            if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, position);
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, position);
            }

            throw new Exception($"Unexpected type '{runtimeType}' for MethodBase");
        }

        private PickledMemberInfo DeserializeMemberInfo(PicklerDeserializationState state)
        {
            var position = state.Reader.BaseStream.Position;
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for MemberInfo");

                case ObjectOperation.Memo:
                    return (PickledMemberInfo)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for MemberInfo");
            }

            var runtimeType = DeserializeType(state, default).Type;

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MemberInfo)), "Expected a MemberInfo type");

            if (runtimeType == typeof(FieldInfo))
            {
                return DeserializeFieldRef(state, position);
            }
            else if (runtimeType == typeof(PropertyInfo))
            {
                return DeserializePropertyRef(state, position);
            }
            else if (runtimeType == typeof(EventInfo))
            {
                return DeserializeEventRef(state, position);
            }
            else if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, position);
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, position);
            }
            else if (runtimeType == typeof(Type))
            {
                return DeserializeType(state, default);
            }

            throw new Exception($"Unexpected type '{runtimeType}' for MemberInfo");
        }

        private Array DeserializeArray(PicklerDeserializationState state, long position, Type arrayType)
        {
            var elementType = arrayType.GetElementType();
            System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for an array type");

            Array array;
            if (arrayType.IsSZArray)
            {
                var length = state.Reader.Read7BitEncodedInt();
                array = Array.CreateInstance(elementType, length);
                state.SetMemo(position, true, array);
            }
            else
            {
                var rank = arrayType.GetArrayRank();
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

                var arrayHandle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    unsafe
                    {
                        var pin = (byte*)arrayHandle.AddrOfPinnedObject().ToPointer();
                        while (byteCount > 0)
                        {
                            // Read upto 4k at a time
                            var length = (int)Math.Min(byteCount, 4096);

                            var span = new Span<byte>(pin, length);
                            state.Reader.Read(span);

                            pin += length;
                            byteCount -= length;
                        }
                    }
                }
                finally
                {
                    arrayHandle.Free();

                }
            }
            else
            {
                if (arrayType.IsSZArray)
                {
                    for (int index = 0; index < array.Length; ++index)
                    {
                        array.SetValue(Deserialize(state, elementType), index);
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

                            var item = Deserialize(state, elementType);
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

        private object DeserializeTuple(PicklerDeserializationState state, bool shouldMemo, long position, Type runtimeType)
        {
            Type[] genericArguments = runtimeType.GetGenericArguments();

            // if length == null short circuit to just return a new ValueTuple
            if (genericArguments.Length == 0)
            {
                System.Diagnostics.Debug.Assert(runtimeType == typeof(ValueTuple), "Tuple length was zero but it wasn't a value tuple");
                return new ValueTuple();
            }

            var items = new object?[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                items[i] = Deserialize(state, genericArguments[i]);

                // Don't want to spam memo lookups if this is a ValueType
                if (shouldMemo)
                {
                    var earlyResult = MaybeReadMemo(state, position);
                    if (earlyResult != null) return earlyResult;
                }
            }

            var genericParameters = new Type[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericParameters[i] = Type.MakeGenericMethodParameter(i);
            }

            var tupleType = runtimeType.IsValueType ? typeof(System.ValueTuple) : typeof(System.Tuple);
            var openCreateMethod = tupleType.GetMethod("Create", genericArguments.Length, genericParameters);
            System.Diagnostics.Debug.Assert(openCreateMethod != null, "GetMethod for Tuple.Create returned null");
            var closedCreateMethod = openCreateMethod.MakeGenericMethod(genericArguments);
            var tupleObject = closedCreateMethod.Invoke(null, items);
            System.Diagnostics.Debug.Assert(tupleObject != null, "Tuple.Create returned null");

            return tupleObject;
        }

        private object DeserializeReducer(PicklerDeserializationState state)
        {
            var method = DeserializeMethodBase(state);

            object? target;
            if (method is PickledConstructorInfo)
            {
                target = null;
            }
            else if (method is PickledMethodInfo)
            {
                target = Deserialize(state, typeof(object));
            }
            else
            {
                throw new Exception($"Invalid reduction MethodBase was '{method}'.");
            }

            var args = new object?[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < args.Length; ++i)
            {
                var arg = Deserialize(state, typeof(object));
                args[i] = arg;
            }

            var result = method.Invoke(target, args);
            if (result == null)
            {
                throw new Exception($"Invalid reducer method, '{method}' returned null.");
            }
            return result;
        }

        private object DeserializeObject(PicklerDeserializationState state, long position, bool shouldMemo, Type objectType, SerialisedObjectTypeInfo typeInfo)
        {
            var uninitalizedObject = state.SetMemo(position, shouldMemo, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(objectType));

            System.Diagnostics.Debug.Assert(typeInfo.SerialisedFields != null, "Error was null, but so was Fields");

            for (int i = 0; i < typeInfo.SerialisedFields.Length; ++i)
            {
                var (fieldType, toSet) = typeInfo.SerialisedFields[i];

                object? value = Deserialize(state, fieldType.Type);

                toSet.SetValue(uninitalizedObject, value);
            }

            return uninitalizedObject;
        }

        private SerialisedObjectTypeInfo GetOrReadSerialisedObjectTypeInfo(PicklerDeserializationState state, Type type)
        {
            var info = state.HasSeenType(type);
            if (info != null)
            {
                return info;
            }

            info = new SerialisedObjectTypeInfo(type);
            state.AddSeenType(type, info);

            // If this is a builtin type there's no need to even write out type flags
            if (!IsBuiltinType(type))
            {
                var infoByte = state.Reader.ReadByte();
                var flags = (PickledTypeFlags)(infoByte & 0xF);
                var mode = (PickledTypeMode)(infoByte >> 4);

                info.Flags = flags;
                info.Mode = mode;
            }
            else
            {
                info.Flags =
                    (type.IsValueType ? PickledTypeFlags.IsValueType : 0) |
                    (type.IsSealed ? PickledTypeFlags.IsSealed : 0) |
                    (type.IsAbstract ? PickledTypeFlags.IsAbstract : 0) |
                    (type.HasElementType ? PickledTypeFlags.HasElementType : 0);

                // Assume builtin, we'll type check and change that below.
                info.Mode = PickledTypeMode.IsBuiltin;
            }



            if (info.Mode == PickledTypeMode.IsEnum)
            {
                if (!type.IsEnum)
                {
                    info.Error = $"Can not deserialise {type} expected it to be an enumeration type";
                }
            }
            else if (info.Mode == PickledTypeMode.IsDelegate)
            {
                if (!type.IsAssignableTo(typeof(MulticastDelegate)))
                {
                    info.Error = $"Can not deserialise {type} expected it to be a delegate type";
                }
            }

            if (info.Mode == PickledTypeMode.IsAutoSerialisedObject)
            {
                var currentFields = GetSerializedFields(type);

                var writtenLength = state.Reader.Read7BitEncodedInt();
                if (currentFields.Length != writtenLength)
                {
                    info.Error = $"Can not deserialize type '{type}', serialised {writtenLength} fields but type expects {currentFields.Length}";
                }

                // We still need to read the fields we have written otherwise nothing else can deserialise. And hell we might not even try and read one of these types, it might just 
                // be used as a local or something.
                info.SerialisedFields = new (SerialisedObjectTypeInfo, FieldInfo)[writtenLength];
                for (int i = 0; i < writtenLength; ++i)
                {
                    var fieldName = state.Reader.ReadString();
                    var fieldType = DeserializeType(state, default).CompleteType;
                    var fieldInfo = GetOrReadSerialisedObjectTypeInfo(state, fieldType);

                    FieldInfo? toSet = null;
                    foreach (var field in currentFields)
                    {
                        if (field.Name == fieldName)
                        {
                            toSet = field;
                            break;
                        }
                    }

                    if (toSet == null)
                    {
                        info.Error = $"Can not deserialize type '{type}', could not find expected field '{fieldName}'";
                    }

                    info.SerialisedFields[i] = (fieldInfo, toSet);
                }
            }
            else if (info.Mode == PickledTypeMode.IsEnum)
            {
                info.TypeCode = (TypeCode)state.Reader.ReadByte();

                if (!type.IsEnum)
                {
                    info.Error = $"Can not deserialise {type} expected it to be an enumeration type";
                }

                var typeCode = Type.GetTypeCode(type);
                if (info.TypeCode != typeCode && info.Error == null)
                {
                    info.Error = $"Can not deserialise {type} expected it to be an enumeration of {info.TypeCode} but was {typeCode}";
                }
            }
            else if (info.Mode == PickledTypeMode.IsDelegate)
            {
                if (!type.IsAssignableTo(typeof(MulticastDelegate)))
                {
                    info.Error = $"Can not deserialise {type} expected it to be a delegate type";
                }
            }

            if (IsNullableType(type, out var elementType))
            {
                info.Element = GetOrReadSerialisedObjectTypeInfo(state, elementType);
            }
            else if (info.Flags.HasFlag(PickledTypeFlags.HasElementType))
            {
                info.Element = GetOrReadSerialisedObjectTypeInfo(state, type.GetElementType());
            }
            else if (IsTupleType(type))
            {
                info.TupleArguments = type.GetGenericArguments().Select(t => GetOrReadSerialisedObjectTypeInfo(state, t)).ToArray();
            }

            return info;
        }

        private object? Deserialize(PicklerDeserializationState state, Type staticType)
        {
            System.Diagnostics.Debug.Assert(SanatizeType(staticType) == staticType, "Static type didn't match sanatized static type");

            var staticInfo = GetOrReadSerialisedObjectTypeInfo(state, staticType);
            if (IsNullableType(staticType, out var nullableInnerType))
            {
                // Nullable<T> always works the same, if the 
                var hasValue = state.Reader.ReadBoolean();
                if (hasValue)
                {
                    return Deserialize(state, nullableInnerType);
                }
                return null;
            }

            var shouldMemo = !staticInfo.Flags.HasFlag(PickledTypeFlags.IsValueType);

            var runtimeType = staticType;
            var runtimeInfo = staticInfo;
            if (shouldMemo)
            {
                var objectOperation = (ObjectOperation)state.Reader.ReadByte();
                switch (objectOperation)
                {
                    case ObjectOperation.Null:
                        return null;

                    case ObjectOperation.Memo:
                        {
                            var obj = state.DoMemo();
                            if (obj is PickledObject pickledObject)
                            {
                                return pickledObject.Get();
                            }
                            return obj;
                        }

                    case ObjectOperation.Object:
                        break;

                    default:
                        throw new Exception($"Unhandled ObjectOperation '{objectOperation}'");
                }

                var rootElementType = GetRootElementType(staticInfo);

                var isSealed = rootElementType.Flags.HasFlag(PickledTypeFlags.IsSealed) || rootElementType.Flags.HasFlag(PickledTypeFlags.IsValueType);

                if (!reflectionTypes.Contains(rootElementType.Type) && !isSealed)
                {
                    // TODO We'd like to use CompleteType elsewhere in this repo but ReadCustomAttributes currently relies on this method which means types might still be constructing.
                    runtimeType = DeserializeType(state, default).CompleteType;
                    runtimeInfo = GetOrReadSerialisedObjectTypeInfo(state, runtimeType);
                }
            }

            if (runtimeInfo.Error != null)
            {
                throw new Exception(runtimeInfo.Error);
            }

            var position = state.Reader.BaseStream.Position;

            if (runtimeType.IsEnum)
            {
                System.Diagnostics.Debug.Assert(runtimeInfo.TypeCode != null, "Expected enumeration type to have a TypeCode");

                var result = Enum.ToObject(runtimeType, ReadEnumerationValue(state.Reader, runtimeInfo.TypeCode.Value));
                state.SetMemo(position, shouldMemo, result);
                return result;
            }

            else if (runtimeType.IsArray)
            {
                return DeserializeArray(state, position, runtimeType);
            }

            else if (runtimeType == typeof(FieldInfo))
            {
                return DeserializeFieldRef(state, position).FieldInfo;
            }
            else if (runtimeType == typeof(PropertyInfo))
            {
                return DeserializePropertyRef(state, position).PropertyInfo;
            }
            else if (runtimeType == typeof(EventInfo))
            {
                return DeserializeEventRef(state, position).EventInfo;
            }
            else if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, position).MethodInfo;
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, position).ConstructorInfo;
            }

            // TODO we want to do this via info flags eventually but due to the dumb way we handle arrays it easier to do this for now
            else if (runtimeType.IsAssignableTo(typeof(MulticastDelegate)))
            {
                return DeserializeDelegate(state, position, runtimeType);
            }

            else if (IsTupleType(runtimeType))
            {
                return state.SetMemo(position, shouldMemo, DeserializeTuple(state, shouldMemo, position, runtimeType));
            }

            else if (runtimeType == typeof(bool))
            {
                var result = (object)state.Reader.ReadBoolean();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(char))
            {
                var result = (object)state.Reader.ReadChar();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(byte))
            {
                var result = (object)state.Reader.ReadByte();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(ushort))
            {
                var result = (object)state.Reader.ReadUInt16();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(uint))
            {
                var result = (object)state.Reader.ReadUInt32();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(ulong))
            {
                var result = (object)state.Reader.ReadUInt64();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(sbyte))
            {
                var result = (object)state.Reader.ReadSByte();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(short))
            {
                var result = (object)state.Reader.ReadInt16();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(int))
            {
                var result = (object)state.Reader.ReadInt32();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(long))
            {
                var result = (object)state.Reader.ReadInt64();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(float))
            {
                var result = (object)state.Reader.ReadSingle();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(double))
            {
                var result = (object)state.Reader.ReadDouble();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(decimal))
            {
                var result = (object)state.Reader.ReadDecimal();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(DBNull))
            {
                var result = (object)DBNull.Value;
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(string))
            {
                var result = (object)state.Reader.ReadString();
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(UIntPtr))
            {
                var result = (object)new UIntPtr(state.Reader.ReadUInt64());
                state.SetMemo(position, shouldMemo, result);
                return result;
            }
            else if (runtimeType == typeof(IntPtr))
            {
                var result = (object)new IntPtr(state.Reader.ReadInt64());
                state.SetMemo(position, shouldMemo, result);
                return result;
            }

            else if (runtimeType == typeof(Assembly))
            {
                var (assembly, assemblyStage3) = DeserializeAssembly(state, default);
                InvokeStage(state, assemblyStage3);
                return assembly.Assembly;
            }
            else if (runtimeType == typeof(Module))
            {
                var (module, moduleStage2) = DeserializeModule(state, default);
                InvokeStage(state, InvokeStage(state, moduleStage2));
                return module.Module;
            }
            else if (runtimeType == typeof(Type))
            {
                return DeserializeType(state, default).CompleteType;
            }

            else if (runtimeInfo.Mode == PickledTypeMode.IsReduced)
            {
                return state.SetMemo(position, shouldMemo, DeserializeReducer(state));
            }

            return DeserializeObject(state, position, shouldMemo, runtimeType, runtimeInfo);
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
            var majorVersion = state.Reader.Read7BitEncodedInt();
            var minorVersion = state.Reader.Read7BitEncodedInt();
            if (majorVersion != _pikalaVersion.Major)
            {
                // TOOD We want to support backwards compatability one day so this will change to a majorVersion <= _pikalaVersion.Major
                throw new InvalidDataException($"Input stream does not match expected version. Got {majorVersion}, expected {_pikalaVersion.Major}");
            }

            var runtimeMajor = state.Reader.Read7BitEncodedInt();
            var runtimeMinor = state.Reader.Read7BitEncodedInt();

            var result = Deserialize(state, typeof(object));
            state.DoStaticFields();
            return result;
        }
    }
}
