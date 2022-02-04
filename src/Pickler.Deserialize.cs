﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    struct DeserializationTypeContext
    {
        public readonly Type[]? GenericTypeParameters;
        public readonly Type[]? GenericMethodParameters;
        public readonly PickledTypeInfo? ContextType;

        public DeserializationTypeContext(Type[]? genericTypeParameters, Type[]? genericMethodParameters, PickledTypeInfo? contextType)
        {
            GenericTypeParameters = genericTypeParameters;
            GenericMethodParameters = genericMethodParameters;
            ContextType = contextType;
        }
    }

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
            var typeContext = new DeserializationTypeContext(genericTypeParameters, null, null);
            var methodAttributes = (MethodAttributes)state.Reader.ReadInt32();
            var callingConvention = (CallingConventions)state.Reader.ReadInt32();

            var parameterCount = state.Reader.Read7BitEncodedInt();
            Type[]? parameterTypes = null;
            if (parameterCount != 0)
            {
                parameterTypes = new Type[parameterCount];
                for (int j = 0; j < parameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeType(state, typeContext);
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
                locals[j] = DeserializeType(state, typeContext);
            }

            constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, parameterTypes, locals);

            var usedTypes = state.Reader.Read7BitEncodedInt();
            for (int j = 0; j < usedTypes; ++j)
            {
                // We can just discared the type here, we just need it in the stream before the method body is done
                DeserializeType(state, typeContext);
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

            var typeContext = new DeserializationTypeContext(genericTypeParameters, constructingMethod.GenericParameters, null);

            var returnType = DeserializeType(state, typeContext);
            var parameterCount = state.Reader.Read7BitEncodedInt();
            if (parameterCount != 0)
            {
                constructingMethod.ParameterTypes = new Type[parameterCount];
                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeType(state, typeContext);
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
                    constructingMethod.Locals[j] = DeserializeType(state, typeContext);
                }

                var usedTypes = state.Reader.Read7BitEncodedInt();
                for (int j = 0; j < usedTypes; ++j)
                {
                    // We can just discared the type here, we just need it in the stream before the method body is done
                    DeserializeType(state, typeContext);
                }
            }
        }

        private void DeserializeMethodBody(PicklerDeserializationState state, DeserializationTypeContext typeContext, PickledTypeInfo[] locals, ILGenerator ilGenerator)
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
                            var memberInfo = DeserializeMemberInfo(state, null);
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
                            var fieldInfo = DeserializeFieldInfo(state, null);
                            ilGenerator.Emit(opCode, fieldInfo.FieldInfo);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodBase = DeserializeMethodBase(state, null);
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
            var isInterface = constructingType.TypeDef == TypeDef.Interface;
            var typeBuilder = constructingType.TypeBuilder;

            var typeContext = new DeserializationTypeContext(constructingType.GenericParameters, null, null);

            if (!isValueType && !isInterface)
            {
                var baseType = DeserializeType(state, typeContext);
                typeBuilder.SetParent(baseType.Type);
            }

            var interfaceCount = state.Reader.Read7BitEncodedInt();
            var interfaceMap = new List<(PickledMethodInfo, Signature)>();
            for (int i = 0; i < interfaceCount; ++i)
            {
                var interfaceType = DeserializeType(state, typeContext);
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
                var propertyType = DeserializeType(state, typeContext);
                var propertyParametersCount = state.Reader.Read7BitEncodedInt();
                var propertyParameters = new Type[propertyParametersCount];
                for (int j = 0; j < propertyParametersCount; ++j)
                {
                    propertyParameters[j] = DeserializeType(state, typeContext).Type;
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
                    DeserializeMethodBody(state, new DeserializationTypeContext(typeContext.GenericTypeParameters, null, null), constructor.Locals!, ilGenerator);
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
                        DeserializeMethodBody(state, new DeserializationTypeContext(typeContext.GenericTypeParameters, method.GenericParameters, null), method.Locals!, ilGenerator);
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

                    var fieldValue = Deserialize(state, fieldInfo.FieldType);
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

                case TypeDef.Interface:
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

                var typeContext = new DeserializationTypeContext(constructingType.GenericParameters, null, null);

                var constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, constructorParameters, null);
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

                constructingType.FullyDefined = true;

                state.PushTrailer(null, () => constructingType.CreateType(), null);
            }
            else
            {
                DeserializeTypeDefComplex(state, constructingType);
            }
        }

        private Array DeserializeArray(PicklerDeserializationState state, long position, Type arrayType)
        {
            state.Reader.ReadByte(); // Dumb padding byte

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

        private object DeserializeTuple(PicklerDeserializationState state, Type runtimeType)
        {
            Type[] genericArguments = runtimeType.GetGenericArguments();

            // if length == null short circuit to just return a new ValueTuple
            if (genericArguments.Length == 0)
            {
                System.Diagnostics.Debug.Assert(runtimeType == typeof(ValueTuple), "Tuple length was zero but it wasn't a value tuple");
                return new ValueTuple();
            }

            var genericParameters = new Type[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericParameters[i] = Type.MakeGenericMethodParameter(i);
            }

            var items = new object?[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                items[i] = Deserialize(state, genericArguments[i]);
            }

            var tupleType = runtimeType.IsValueType ? typeof(System.ValueTuple) : typeof(System.Tuple);
            var openCreateMethod = tupleType.GetMethod("Create", genericArguments.Length, genericParameters);
            System.Diagnostics.Debug.Assert(openCreateMethod != null, "GetMethod for Tuple.Create returned null");
            var closedCreateMethod = openCreateMethod.MakeGenericMethod(genericArguments);
            var tupleObject = closedCreateMethod.Invoke(null, items);
            System.Diagnostics.Debug.Assert(tupleObject != null, "Tuple.Create returned null");

            return tupleObject;
        }

        private object DeserializeISerializable(PicklerDeserializationState state, Type type)
        {
            var memberCount = state.Reader.Read7BitEncodedInt();

            var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
            var info = new System.Runtime.Serialization.SerializationInfo(type, new System.Runtime.Serialization.FormatterConverter());

            for (int i = 0; i < memberCount; ++i)
            {
                var name = state.Reader.ReadString();
                var value = Deserialize(state, typeof(object));
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

        private object DeserializeReducer(PicklerDeserializationState state)
        {
            var method = DeserializeMethodBase(state, default);

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
            var uninitalizedObject = state.SetMemo(position, shouldMemo, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(objectType));

            System.Diagnostics.Debug.Assert(typeInfo.SerialisedFields != null, "Error was null, but so was Fields");

            for (int i = 0; i < typeInfo.SerialisedFields.Length; ++i)
            {
                var (fieldType, toSet) = typeInfo.SerialisedFields[i];

                object? value = Deserialize(state, fieldType.Type);

                toSet.SetValue(uninitalizedObject, value);
            }

            return uninitalizedObject;
        }

        private PickledFieldInfo DeserializeFieldRef(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            return state.RunWithTrailers(() =>
            {
                var position = state.Reader.BaseStream.Position;
                var name = state.Reader.ReadString();
                var type = contextType;
                if (type == null)
                {
                    type = DeserializeType(state, default);
                }
                return state.SetMemo(position, true, type.GetField(name));
            });
        }

        private PickledPropertyInfo DeserializePropertyRef(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            return state.RunWithTrailers(() =>
            {
                var position = state.Reader.BaseStream.Position;
                var signature = DeserializeSignature(state);
                var type = contextType;
                if (type == null)
                {
                    type = DeserializeType(state, default);
                }
                return state.SetMemo(position, true, type.GetProperty(signature));
            });
        }

        private PickledEventInfo DeserializeEventRef(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            return state.RunWithTrailers(() =>
            {
                var position = state.Reader.BaseStream.Position;
                var name = state.Reader.ReadString();
                var type = contextType;
                if (type == null)
                {
                    type = DeserializeType(state, default);
                }
                return state.SetMemo(position, true, type.GetEvent(name));
            });
        }

        private PickledConstructorInfo DeserializeConstructorRef(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            return state.RunWithTrailers(() =>
            {
                var position = state.Reader.BaseStream.Position;
                var signature = DeserializeSignature(state);
                var type = contextType;
                if (type == null)
                {
                    type = DeserializeType(state, default);
                }
                return state.SetMemo(position, true, type.GetConstructor(signature));
            });
        }

        private PickledMethodInfo DeserializeMethodRef(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            return state.RunWithTrailers(() =>
            {
                var position = state.Reader.BaseStream.Position;
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

                var type = contextType;
                if (type == null)
                {
                    type = DeserializeType(state, default);
                }

                var methodInfo = type.GetMethod(signature);

                if (genericArguments != null)
                {
                    return state.SetMemo(position, true, new ConstructingGenericMethod(methodInfo, genericArguments));
                }
                return state.SetMemo(position, true, methodInfo);
            });
        }

        private object DeserializeDelegate(PicklerDeserializationState state, long position, Type delegateType)
        {
            var invocationList = new Delegate[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < invocationList.Length; ++i)
            {
                var target = Deserialize(state, typeof(object));
                var method = DeserializeMethodInfo(state, default);
                invocationList[i] = Delegate.CreateDelegate(delegateType, target, method.MethodInfo);
            }
            return state.SetMemo(position, true, Delegate.Combine(invocationList)!);
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
            return state.SetMemo(position, ShouldMemo(assembly), assembly);
        }

        private void ReadCustomAttributes(PicklerDeserializationState state, Action<CustomAttributeBuilder> setCustomAttribute)
        {
            var attributeCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < attributeCount; ++i)
            {
                var attributeType = DeserializeType(state, default);

                var constructor = DeserializeConstructorInfo(state, attributeType);
                var arguments = new object?[state.Reader.Read7BitEncodedInt()];
                for (int j = 0; j < arguments.Length; ++j)
                {
                    arguments[j] = Deserialize(state, typeof(object));
                }

                var namedProperties = new PropertyInfo[state.Reader.Read7BitEncodedInt()];
                var propertyValues = new object?[namedProperties.Length];
                for (int j = 0; j < namedProperties.Length; ++j)
                {
                    var propertyInfo = DeserializePropertyInfo(state, attributeType);
                    namedProperties[j] = propertyInfo.PropertyInfo;
                    propertyValues[j] = Deserialize(state, namedProperties[j].PropertyType);
                }

                var namedFields = new FieldInfo[state.Reader.Read7BitEncodedInt()];
                var fieldValues = new object?[namedFields.Length];
                for (int j = 0; j < namedFields.Length; ++j)
                {
                    var pickledField = DeserializeFieldInfo(state, attributeType);
                    namedFields[j] = pickledField.FieldInfo;
                    fieldValues[j] = Deserialize(state, namedFields[j].FieldType);
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

        private Assembly DeserializeAsesmblyDef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
        {
            return state.RunWithTrailers(() =>
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

                state.PushTrailer(() =>
                {
                    ReadCustomAttributes(state, assembly.SetCustomAttribute);
                }, () => { }, null);

                return assembly;
            });
        }

        private Module DeserializeManifestModuleRef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
        {
            var assembly = DeserializeAssembly(state, typeContext);
            return state.SetMemo(position, ShouldMemo(assembly), assembly.ManifestModule);
        }

        private Module DeserializeModuleRef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
        {
            var name = state.Reader.ReadString();
            var assembly = DeserializeAssembly(state, typeContext);
            var module = assembly.GetModule(name);
            if (module == null)
            {
                throw new Exception($"Could not load module '{name}' from assembly '{assembly}'");
            }
            return state.SetMemo(position, true, module);
        }

        private ModuleBuilder DeserializeModuleDef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
        {
            return state.RunWithTrailers(() =>
            {
                var name = state.Reader.ReadString();
                var callback = state.RegisterMemoCallback(position, (AssemblyBuilder assembly) =>
                {
                    var module = assembly.DefineDynamicModule(name);
                    if (module == null)
                    {
                        throw new Exception($"Could not create module '{name}' in assembly '{assembly}'");
                    }
                    return state.SetMemo(position, true, module);
                });
                var _ = DeserializeAssembly(state, typeContext);
                var moduleBuilder = callback.Invoke();

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

                state.PushTrailer(() =>
                {
                    ReadCustomAttributes(state, moduleBuilder.SetCustomAttribute);

                    foreach (var field in fields)
                    {
                        ReadCustomAttributes(state, field.FieldBuilder.SetCustomAttribute);
                    }

                    foreach (var method in methods)
                    {
                        ReadCustomAttributes(state, method.MethodBuilder.SetCustomAttribute);
                        var ilGenerator = method.MethodBuilder.GetILGenerator();
                        DeserializeMethodBody(state, new DeserializationTypeContext(null, method.GenericParameters, null), method.Locals!, ilGenerator);
                    }
                },
                () => moduleBuilder.CreateGlobalFunctions(),
                () => { });

                return moduleBuilder;
            });
        }

        private PickledGenericType DeserializeGenericInstantiation(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
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
                var method = DeserializeMethodInfo(state, default);
                genericParameter = method.GetGenericArgument(genericParameterPosition);
            }
            return state.SetMemo(position, true, genericParameter);
        }

        private PickledTypeInfoRef DeserializeTypeRef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
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
                var module = DeserializeModule(state, typeContext);
                var type = module.GetType(typeName);
                if (type == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{module.FullyQualifiedName}'");
                }

                result = new PickledTypeInfoRef(type);
            }

            state.SetMemo(position, true, result);

            return result;
        }

        private PickledTypeInfoDef DeserializeTypeDef(PicklerDeserializationState state, long position, DeserializationTypeContext typeContext)
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
                if (isNested)
                {
                    var callback = state.RegisterMemoCallback(position, (PickledTypeInfoDef declaringType) =>
                    {
                        var result = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, declaringType.TypeBuilder.DefineNestedType);

                        if (genericParameters != null)
                        {
                            result.GenericParameters = result.TypeBuilder.DefineGenericParameters(genericParameters);
                        }

                        state.AddTypeDef(result);
                        return state.SetMemo(position, true, result);
                    });
                    var _ = DeserializeType(state, typeContext);
                    constructingType = callback.Invoke();
                }
                else
                {

                    var callback = state.RegisterMemoCallback(position, (ModuleBuilder module) =>
                    {
                        var result = ConstructingTypeForTypeDef(typeDef, typeName, typeAttributes, module.DefineType);

                        if (genericParameters != null)
                        {
                            result.GenericParameters = result.TypeBuilder.DefineGenericParameters(genericParameters);
                        }

                        state.AddTypeDef(result);
                        return state.SetMemo(position, true, result);
                    });
                    var _ = DeserializeModule(state, typeContext);
                    constructingType = callback.Invoke();
                }
                DeserializeTypeDef(state, constructingType);
                return constructingType;
            });
        }

        private Assembly DeserializeAssembly(PicklerDeserializationState state, DeserializationTypeContext typeContext)
        {
            var position = state.Reader.BaseStream.Position;
            var operation = (AssemblyOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case AssemblyOperation.Memo:
                    return (Assembly)state.DoMemo();

                case AssemblyOperation.MscorlibAssembly:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return mscorlib;

                case AssemblyOperation.AssemblyRef:
                    return DeserializeAsesmblyRef(state, position);

                case AssemblyOperation.AssemblyDef:
                    return DeserializeAsesmblyDef(state, position, typeContext);
            }

            throw new Exception($"Unexpected operation '{operation}' for Assembly");
        }

        private Module DeserializeModule(PicklerDeserializationState state, DeserializationTypeContext typeContext)
        {
            var position = state.Reader.BaseStream.Position;
            var operation = (ModuleOperation)state.Reader.ReadByte();

            switch (operation)
            {
                case ModuleOperation.Memo:
                    return (Module)state.DoMemo();

                case ModuleOperation.MscorlibModule:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return mscorlib.ManifestModule;

                case ModuleOperation.ManifestModuleRef:
                    return DeserializeManifestModuleRef(state, position, typeContext);

                case ModuleOperation.ModuleRef:
                    return DeserializeModuleRef(state, position, typeContext);

                case ModuleOperation.ModuleDef:
                    return DeserializeModuleDef(state, position, typeContext);
            }

            throw new Exception($"Unexpected operation '{operation}' for Module");
        }

        private PickledTypeInfo DeserializeType(PicklerDeserializationState state, DeserializationTypeContext typeContext)
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

        private PickledFieldInfo DeserializeFieldInfo(PicklerDeserializationState state, PickledTypeInfo? contextType)
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

            return DeserializeFieldRef(state, contextType);
        }

        private PickledPropertyInfo DeserializePropertyInfo(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
            var objectOperation = (ObjectOperation)state.Reader.ReadByte();
            switch (objectOperation)
            {
                case ObjectOperation.Null:
                    throw new Exception($"Unexpected null for PropertyInfo");

                case ObjectOperation.Memo:
                    return (PickledPropertyInfo)state.DoMemo();

                case ObjectOperation.Object:
                    break;

                default:
                    throw new Exception($"Unexpected operation '{objectOperation}' for PropertyInfo");
            }

            return DeserializePropertyRef(state, contextType);
        }

        private PickledConstructorInfo DeserializeConstructorInfo(PicklerDeserializationState state, PickledTypeInfo? contextType)
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

            return DeserializeConstructorRef(state, contextType);
        }

        private PickledMethodInfo DeserializeMethodInfo(PicklerDeserializationState state, PickledTypeInfo? contextType)
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

            return DeserializeMethodRef(state, contextType);
        }

        private PickledMethodBase DeserializeMethodBase(PicklerDeserializationState state, PickledTypeInfo? contextType)
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
            var runtimeTypeInfo = GetOrReadSerialisedObjectTypeInfo(state, runtimeType);

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MethodBase)), "Expected a MethodBase type");

            if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, contextType);
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, contextType);
            }

            throw new Exception($"Unexpected type '{runtimeType}' for MethodBase");
        }

        private PickledMemberInfo DeserializeMemberInfo(PicklerDeserializationState state, PickledTypeInfo? contextType)
        {
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
            var runtimeTypeInfo = GetOrReadSerialisedObjectTypeInfo(state, runtimeType);

            System.Diagnostics.Debug.Assert(runtimeType.IsAssignableTo(typeof(MemberInfo)), "Expected a MemberInfo type");

            if (runtimeType == typeof(FieldInfo))
            {
                return DeserializeFieldRef(state, contextType);
            }
            else if (runtimeType == typeof(PropertyInfo))
            {
                return DeserializePropertyRef(state, contextType);
            }
            else if (runtimeType == typeof(EventInfo))
            {
                return DeserializeEventRef(state, contextType);
            }
            else if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, contextType);
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, contextType);
            }
            else if (runtimeType == typeof(Type))
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
                    var fieldType = DeserializeType(state, default).Type;
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

                if (!reflectionTypes.Contains(staticType) && !isSealed)
                {
                    // TODO We'd like to use CompleteType elsewhere in this repo but ReadCustomAttributes currently relies on this method which means types might still be constructing.
                    runtimeType = DeserializeType(state, default).CompleteType;
                }

                runtimeInfo = GetOrReadSerialisedObjectTypeInfo(state, runtimeType);
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
                return DeserializeFieldRef(state, default).FieldInfo;
            }
            else if (runtimeType == typeof(PropertyInfo))
            {
                return DeserializePropertyRef(state, default).PropertyInfo;
            }
            else if (runtimeType == typeof(EventInfo))
            {
                return DeserializeEventRef(state, default).EventInfo;
            }
            else if (runtimeType == typeof(MethodInfo))
            {
                return DeserializeMethodRef(state, default).MethodInfo;
            }
            else if (runtimeType == typeof(ConstructorInfo))
            {
                return DeserializeConstructorRef(state, default).ConstructorInfo;
            }

            // TODO we want to do this via info flags eventually but due to the dumb way we handle arrays it easier to do this for now
            else if (runtimeType.IsAssignableTo(typeof(MulticastDelegate)))
            {
                return DeserializeDelegate(state, position, runtimeType);
            }

            else if (IsTupleType(runtimeType))
            {
                return state.SetMemo(position, shouldMemo, DeserializeTuple(state, runtimeType));
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
                return DeserializeAssembly(state, default);
            }
            else if (runtimeType == typeof(Module))
            {
                return DeserializeModule(state, default);
            }
            else if (runtimeType == typeof(Type))
            {
                return DeserializeType(state, default).Type;
            }

            else if (runtimeInfo.Mode == PickledTypeMode.IsReduced)
            {
                return state.SetMemo(position, shouldMemo, DeserializeReducer(state));
            }

            else if (runtimeInfo.Mode == PickledTypeMode.IsISerializable)
            {
                return state.SetMemo(position, shouldMemo, DeserializeISerializable(state, runtimeType));
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
