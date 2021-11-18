using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using System.Diagnostics.CodeAnalysis;

namespace Ibasa.Pikala
{
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
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, null);
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

            ReadCustomAttributes(state, constructorBuilder.SetCustomAttribute, genericTypeParameters, null);

            constructorBuilder.InitLocals = state.Reader.ReadBoolean();

            var locals = new PickledTypeInfo[state.Reader.Read7BitEncodedInt()];
            for (int j = 0; j < locals.Length; ++j)
            {
                // We can't actually DECLARE locals here, so store them on the ConstructingMethod till construction time
                locals[j] = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, null);
            }

            constructingConstructor = new PickledConstructorInfoDef(constructingType, constructorBuilder, parameters, parameterTypes, locals);

            var usedTypes = state.Reader.Read7BitEncodedInt();
            for (int j = 0; j < usedTypes; ++j)
            {
                // We can just discared the type here, we just need it in the stream before the method body is done
                DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, null);
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

            var returnType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, constructingMethod.GenericParameters);
            var parameterCount = state.Reader.Read7BitEncodedInt();
            if (parameterCount != 0)
            {
                constructingMethod.ParameterTypes = new Type[parameterCount];
                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, constructingMethod.GenericParameters);
                    constructingMethod.ParameterTypes[j] = parameterType.Type;
                }

                methodBuilder.SetSignature(returnType.Type, null, null, constructingMethod.ParameterTypes, null, null);

                constructingMethod.Parameters = new ParameterBuilder[parameterCount];
                for (int j = 0; j < constructingMethod.ParameterTypes.Length; ++j)
                {
                    var parameterName = state.Reader.ReadString();
                    var parameterAttributes = (ParameterAttributes)state.Reader.ReadInt32();
                    constructingMethod.Parameters[j] = methodBuilder.DefineParameter(1 + j, parameterAttributes, parameterName);
                }
            }
            else
            {
                methodBuilder.SetSignature(returnType.Type, null, null, null, null, null);
            }

            ReadCustomAttributes(state, methodBuilder.SetCustomAttribute, genericTypeParameters, constructingMethod.GenericParameters);

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
                    constructingMethod.Locals[j] = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, constructingMethod.GenericParameters);
                }

                var usedTypes = state.Reader.Read7BitEncodedInt();
                for (int j = 0; j < usedTypes; ++j)
                {
                    // We can just discared the type here, we just need it in the stream before the method body is done
                    Deserialize(state, typeof(Type), genericTypeParameters, constructingMethod.GenericParameters);
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
                            var memberInfo = DeserializeNonNull<PickledMemberInfo>(state, typeof(MemberInfo), genericTypeParameters, genericMethodParameters);
                            memberInfo.Emit(ilGenerator, opCode);
                            break;
                        }

                    case OperandType.InlineType:
                        {
                            var typeInfo = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
                            ilGenerator.Emit(opCode, typeInfo.Type);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldInfo = DeserializeNonNull<PickledFieldInfo>(state, typeof(FieldInfo), genericTypeParameters, genericMethodParameters);
                            ilGenerator.Emit(opCode, fieldInfo.FieldInfo);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodBase = DeserializeNonNull<PickledMethodBase>(state, typeof(MethodBase), genericTypeParameters, genericMethodParameters);
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
                var baseType = Deserialize<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
                if (baseType != null)
                {
                    typeBuilder.SetParent(baseType.Type);
                }
            }

            var interfaceCount = state.Reader.Read7BitEncodedInt();
            var interfaceMap = new List<(PickledMethodInfo, string)>();
            for (int i = 0; i < interfaceCount; ++i)
            {
                var interfaceType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
                typeBuilder.AddInterfaceImplementation(interfaceType.Type);

                var mapCount = state.Reader.Read7BitEncodedInt();
                for (int j = 0; j < mapCount; ++j)
                {
                    var interfaceMethodSignature = state.Reader.ReadString();
                    var targetMethodSignature = state.Reader.ReadString();
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
                var fieldType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
                var fieldBuilder = typeBuilder.DefineField(fieldName, fieldType.Type, fieldAttributes);
                ReadCustomAttributes(state, fieldBuilder.SetCustomAttribute, constructingType.GenericParameters, null);
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

            var propertyCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < propertyCount; ++i)
            {
                var propertyName = state.Reader.ReadString();
                var propertyAttributes = (PropertyAttributes)state.Reader.ReadInt32();
                var propertyType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
                var propertyParametersCount = state.Reader.Read7BitEncodedInt();
                var propertyParameters = new Type[propertyParametersCount];
                for (int j = 0; j < propertyParametersCount; ++j)
                {
                    propertyParameters[j] = (DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null)).Type;
                }

                var propertyBuilder = typeBuilder.DefineProperty(propertyName, propertyAttributes, propertyType.Type, propertyParameters);
                ReadCustomAttributes(state, propertyBuilder.SetCustomAttribute, constructingType.GenericParameters, null);

                var getMethod = state.Reader.ReadNullableString();
                var setMethod = state.Reader.ReadNullableString();
                foreach (var method in constructingType.Methods)
                {
                    if (getMethod == null && setMethod == null) break;

                    if (method.GetSignature() == getMethod)
                    {
                        propertyBuilder.SetGetMethod(method.MethodBuilder);
                        getMethod = null;
                    }
                    if (method.GetSignature() == setMethod)
                    {
                        propertyBuilder.SetSetMethod(method.MethodBuilder);
                        setMethod = null;
                    }
                }
            }

            ReadCustomAttributes(state, typeBuilder.SetCustomAttribute, constructingType.GenericParameters, null);

            state.PushTrailer(() =>
            {
                foreach (var constructor in constructingType.Constructors)
                {
                    var ilGenerator = constructor.ConstructorBuilder.GetILGenerator();
                    DeserializeMethodBody(state, constructingType.GenericParameters, null, constructor.Locals!, ilGenerator);
                }
                foreach (var method in constructingType.Methods)
                {
                    var methodBuilder = method.MethodBuilder;
                    if (methodBuilder.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || methodBuilder.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || methodBuilder.Attributes.HasFlag(MethodAttributes.Abstract))
                    {

                    }
                    else
                    {
                        var ilGenerator = methodBuilder.GetILGenerator();
                        DeserializeMethodBody(state, constructingType.GenericParameters, method.GenericParameters, method.Locals!, ilGenerator);
                    }
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

                    try
                    {
                        var fieldValue = Deserialize(state, fieldInfo.FieldType, null, null);
                        fieldInfo.SetValue(null, fieldValue);
                    }
                    catch (MemoException exc)
                    {
                        state.RegisterFixup(exc.Position, value => fieldInfo.SetValue(null, value));
                    }
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

                ReadCustomAttributes(state, typeBuilder.SetCustomAttribute, null, null);

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

                var returnType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
                var parameterCount = state.Reader.Read7BitEncodedInt();
                var parameterNames = new string[parameterCount];
                var parameterTypes = new Type[parameterCount];
                for (int i = 0; i < parameterCount; ++i)
                {
                    parameterNames[i] = state.Reader.ReadString();
                    var parameterType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), constructingType.GenericParameters, null);
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

        private Array DeserializeArray(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var elementType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            var rank = state.Reader.ReadByte();

            if (rank == 0)
            {
                var length = state.Reader.Read7BitEncodedInt();
                var array = Array.CreateInstance(elementType.Type, length);
                state.SetMemo(position, array);
                for (int index = 0; index < length; ++index)
                {
                    array.SetValue(ReducePickle(Deserialize(state, elementType.Type, genericTypeParameters, genericMethodParameters)), index);
                }
                return array;
            }
            else
            {
                var lengths = new int[rank];
                var lowerBounds = new int[rank];
                var indices = new int[rank];
                for (int dimension = 0; dimension < rank; ++dimension)
                {
                    lengths[dimension] = state.Reader.Read7BitEncodedInt();
                    lowerBounds[dimension] = indices[dimension] = state.Reader.Read7BitEncodedInt();
                }
                var array = Array.CreateInstance(elementType.Type, lengths, lowerBounds);

                void Iterate()
                {
                    // The first time we call into Iterate we know the array is non-empty, and indices is equal to lowerBounds (i.e the first element)
                    // If we reach the last element we don't call back into Iterate

                    var item = ReducePickle(Deserialize(state, elementType.Type, genericTypeParameters, genericMethodParameters));
                    array.SetValue(item, indices);

                    // Increment indices to the next position, we work through the dimensions backwards because that matches the order that GetEnumerator returns when we serialise out the items
                    var didBreak = false;
                    for (int dimension = rank - 1; dimension >= 0; --dimension)
                    {
                        var next = indices[dimension] + 1;
                        if (next < lowerBounds[dimension] + lengths[dimension])
                        {
                            indices[dimension] = next;
                            didBreak = true;
                            break;
                        }
                        else
                        {
                            indices[dimension] = lowerBounds[dimension];
                        }
                    }

                    if (didBreak) { Iterate(); }
                }

                // If the array is empty (any length == 0) we save calling into Iterate
                bool isEmpty = false;
                for (int dimension = 0; dimension < rank; ++dimension)
                {
                    isEmpty |= lengths[dimension] == 0;
                }

                if (!isEmpty) { Iterate(); }

                return array;
            }
        }

        private object DeserializeISerializable(PicklerDeserializationState state, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var memberCount = state.Reader.Read7BitEncodedInt();

            var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
            var info = new System.Runtime.Serialization.SerializationInfo(type, new System.Runtime.Serialization.FormatterConverter());

            for (int i = 0; i < memberCount; ++i)
            {
                var name = state.Reader.ReadString();
                var value = ReducePickle(Deserialize(state, typeof(object), genericTypeParameters, genericMethodParameters));
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
            var method = Deserialize<PickledMethodBase>(state, typeof(MethodBase), genericTypeParameters, genericMethodParameters);
            if (method is PickledConstructorInfo constructorInfo)
            {
                var args = ReducePickle(DeserializeNonNull<object[]>(state, typeof(object[]), genericTypeParameters, genericMethodParameters));
                return constructorInfo.Invoke(args);
            }
            else if (method is PickledMethodInfo methodInfo)
            {
                var target = ReducePickle(Deserialize(state, typeof(object), genericTypeParameters, genericMethodParameters));
                var args = ReducePickle(DeserializeNonNull<object[]>(state, typeof(object[]), genericTypeParameters, genericMethodParameters));
                return methodInfo.Invoke(target, args);
            }
            else
            {
                throw new Exception($"Invalid reduction MethodBase was '{method}'.");
            }
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

                object? value = ReducePickle(Deserialize(state, toSet.FieldType, genericTypeParameters, genericMethodParameters));

                toSet.SetValue(uninitializedObject, value);
            }
        }

        // TODO It would be good to only return PickledObject things as part of typedef construction and not have that recurse through Deserialize
        [return:NotNullIfNotNull("obj")]
        private object? ReducePickle(object? obj)
        {
            if (obj is PickledObject pickledObject)
            {
                return pickledObject.Get();
            }
            return obj;
        }

        private object?[] ReducePickle(object?[] obj)
        {
            object?[] result = new object?[obj.Length];
            for (int i = 0; i < result.Length; ++i)
            {
                result[i] = ReducePickle(obj[i]);
            }
            return result;
        }


        private PickledFieldInfo DeserializeFieldInfo(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var type = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            var name = state.Reader.ReadString();
            return state.SetMemo(position, type.GetField(name));
        }

        private PropertyInfo DeserializePropertyInfo(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var type = Deserialize(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            var name = state.Reader.ReadString();

            PropertyInfo? result;
            if (type is PickledTypeInfoDef constructingType)
            {
                result = null;
                if (constructingType.Properties != null)
                {
                    foreach (var property in constructingType.Properties)
                    {
                        if (property.Name == name)
                        {
                            result = property;
                            break;
                        }
                    }
                }

                if (result == null)
                {
                    throw new Exception($"Could not load property '{name}' from type '{constructingType.TypeBuilder.FullName}'");
                }
            }
            else if (type is PickledTypeInfoRef typeInfo)
            {
                result = typeInfo.Type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (result == null)
                {
                    throw new Exception($"Could not load property '{name}' from type '{typeInfo.Type.FullName}'");
                }
            }
            else
            {
                throw new Exception($"Unexpected parent '{type}' for property '{name}'");
            }
            return state.SetMemo(position, result);
        }

        private PickledConstructorInfo DeserializeConstructorInfo(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var signature = state.Reader.ReadString();
            var (callback, _) = DeserializeWithMemo(state, position, (PickledTypeInfo type) =>
            {
                var constructorInfo = type.GetConstructor(signature);
                return state.SetMemo(position, constructorInfo);
            }, typeof(Type), genericTypeParameters, genericMethodParameters);
            return callback.Invoke();
        }

        private PickledMethodInfo DeserializeMethodInfo(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var signature = state.Reader.ReadString();
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            PickledTypeInfo[]? genericArguments = null;
            if (genericArgumentCount != 0)
            {
                genericArguments = new PickledTypeInfo[genericArgumentCount];
                for (int i = 0; i < genericArgumentCount; ++i)
                {
                    genericArguments[i] = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
                }
            }
            var (callback, _) = DeserializeWithMemo(state, position, (PickledTypeInfo type) =>
            {
                var methodInfo = type.GetMethod(signature);

                if (genericArguments != null)
                {
                    return state.SetMemo(position, new ConstructingGenericMethod(methodInfo, genericArguments));
                }
                return state.SetMemo(position, methodInfo);
            }, typeof(Type), genericTypeParameters, genericMethodParameters);
            return callback.Invoke();
        }

        private object DeserializeDelegate(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var objType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            var invocationList = new Delegate[state.Reader.Read7BitEncodedInt()];
            for (int i = 0; i < invocationList.Length; ++i)
            {
                var target = Deserialize(state, typeof(object), genericTypeParameters, genericMethodParameters);
                var method = DeserializeNonNull<PickledMethodInfo>(state, typeof(MethodInfo), genericTypeParameters, genericMethodParameters);
                invocationList[i] = Delegate.CreateDelegate(objType.Type, target, method.MethodInfo);
            }
            return state.SetMemo(position, Delegate.Combine(invocationList));
        }

        private Assembly DeserializeAsesmblyRef(PicklerDeserializationState state, long position)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            var assembly = Assembly.Load(assemblyName);
            if (assembly == null)
            {
                throw new Exception($"Could not load assembly '{assemblyName}'");
            }
            return state.SetMemo(position, assembly);
        }

        private void ReadCustomAttributes(PicklerDeserializationState state, Action<CustomAttributeBuilder> setCustomAttribute, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var attributeCount = state.Reader.Read7BitEncodedInt();
            for (int i = 0; i < attributeCount; ++i)
            {
                var constructor = DeserializeNonNull<PickledConstructorInfo>(state, typeof(ConstructorInfo), genericTypeParameters, genericMethodParameters);
                var arguments = new object?[state.Reader.Read7BitEncodedInt()];
                for (int j = 0; j < arguments.Length; ++j)
                {
                    arguments[j] = ReducePickle(Deserialize(state, typeof(object), genericTypeParameters, genericMethodParameters));
                }

                var namedProperties = new PropertyInfo[state.Reader.Read7BitEncodedInt()];
                var propertyValues = new object?[namedProperties.Length];
                for (int j = 0; j < namedProperties.Length; ++j)
                {
                    var propertyInfo = DeserializeNonNull<PropertyInfo>(state, typeof(PropertyInfo), genericTypeParameters, genericMethodParameters);
                    namedProperties[j] = propertyInfo;
                    propertyValues[j] = ReducePickle(Deserialize(state, propertyInfo.PropertyType, genericTypeParameters, genericMethodParameters));
                }

                var namedFields = new FieldInfo[state.Reader.Read7BitEncodedInt()];
                var fieldValues = new object?[namedFields.Length];
                for (int j = 0; j < namedFields.Length; ++j)
                {
                    var pickledField = DeserializeNonNull<PickledFieldInfo>(state, typeof(FieldInfo), genericTypeParameters, genericMethodParameters);
                    namedFields[j] = pickledField.FieldInfo;
                    fieldValues[j] = ReducePickle(Deserialize(state, pickledField.FieldInfo.FieldType, genericTypeParameters, genericMethodParameters));
                }

                var customBuilder = new CustomAttributeBuilder(constructor.ConstructorInfo, arguments, namedProperties, propertyValues, namedFields, fieldValues);

                setCustomAttribute(customBuilder);
            }
        }

        private Assembly DeserializeAsesmblyDef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var assemblyName = new AssemblyName(state.Reader.ReadString());
            var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect, null);
            if (assembly == null)
            {
                throw new Exception($"Could not define assembly '{assemblyName}'");
            }
            state.SetMemo(position, assembly);
            ReadCustomAttributes(state, assembly.SetCustomAttribute, genericTypeParameters, genericMethodParameters);
            return assembly;
        }

        private Module DeserializeModuleRef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var name = state.Reader.ReadString();
            var assembly = DeserializeNonNull<Assembly>(state, typeof(Assembly), genericTypeParameters, genericMethodParameters);
            var module = assembly.GetModule(name);
            if (module == null)
            {
                throw new Exception($"Could not load module '{name}' from assembly '{assembly}'");
            }
            return state.SetMemo(position, module);
        }

        private ModuleBuilder DeserializeModuleDef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var name = state.Reader.ReadString();
            var (callback, _) = DeserializeWithMemo(state, position, (AssemblyBuilder assembly) =>
            {
                var module = assembly.DefineDynamicModule(name);
                if (module == null)
                {
                    throw new Exception($"Could not create module '{name}' in assembly '{assembly}'");
                }
                return state.SetMemo(position, module);
            }, typeof(Assembly), genericTypeParameters, genericMethodParameters);
            var moduleBuilder = callback.Invoke();

            ReadCustomAttributes(state, moduleBuilder.SetCustomAttribute, genericTypeParameters, genericMethodParameters);

            var fieldCount = state.Reader.Read7BitEncodedInt();
            var fields = new PickledFieldInfoDef[fieldCount];
            for (int i = 0; i < fieldCount; ++i)
            {
                var fieldName = state.Reader.ReadString();
                var fieldAttributes = (FieldAttributes)state.Reader.ReadInt32();
                var fieldType = Deserialize<PickledTypeInfo>(state, typeof(Type), null, null);
                throw new NotImplementedException();
                //constructingModule.Fields[i] = new PickledFieldInfoDef(constructingModule, typeBuilder.DefineField(fieldName, fieldType.Type, fieldAttributes));
            }

            var methodCount = state.Reader.Read7BitEncodedInt();
            var methods = new PickledMethodInfoDef[methodCount];
            for (int i = 0; i < methodCount; ++i)
            {
                throw new NotImplementedException();
                //DeserializeMethodHeader(state, null, constructingModule, ref methods[i]);
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
            () =>
            {
                for (int i = 0; i < fields.Length; ++i)
                {
                    var fieldName = state.Reader.ReadString();
                    FieldInfo? fieldInfo = null;
                    for (int j = 0; j < fields.Length; ++j)
                    {
                        if (fieldName == fields[j].FieldInfo.Name)
                        {
                            fieldInfo = fields[j].FieldInfo;
                        }
                    }

                    if (fieldInfo == null)
                    {
                        throw new Exception();
                    }

                    try
                    {
                        var fieldValue = Deserialize(state, fieldInfo.FieldType, null, null);
                        fieldInfo.SetValue(null, fieldValue);
                    }
                    catch (MemoException exc)
                    {
                        state.RegisterFixup(exc.Position, value => fieldInfo.SetValue(null, value));
                    }
                }
            });

            return moduleBuilder;
        }

        private PickledGenericType DeserializeGenericInstantiation(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var genericType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            var genericArgumentCount = state.Reader.Read7BitEncodedInt();
            var genericArguments = new PickledTypeInfo[genericArgumentCount];
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                genericArguments[i] = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
            }
            return state.SetMemo(position, new PickledGenericType(genericType, genericArguments));
        }

        private PickledTypeInfo DeserializeGenericParameter(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var genericTypeOrMethod = DeserializeNonNull<PickledMemberInfo>(state, typeof(MemberInfo), genericTypeParameters, genericMethodParameters);
            var genericParameter = state.Reader.Read7BitEncodedInt();
            return state.SetMemo(position, genericTypeOrMethod.GetGenericArgument(genericParameter));
        }

        private PickledTypeInfoRef DeserializeTypeRef(PicklerDeserializationState state, long position, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var parent = Deserialize(state, typeof(object), genericTypeParameters, genericMethodParameters);
            var typeName = state.Reader.ReadString();

            PickledTypeInfoRef? result;
            if (parent is Module module)
            {
                result = new PickledTypeInfoRef(module.GetType(typeName));
                if (result == null)
                {
                    throw new Exception($"Could not load type '{typeName}' from module '{module.FullyQualifiedName}'");
                }
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
            return state.SetMemo(position, result);
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
                    return state.SetMemo(position, result);
                }, typeof(object), genericTypeParameters, genericMethodParameters);
                var constructingType = callback.Invoke();
                DeserializeTypeDef(state, constructingType);
                return constructingType;
            });
        }

        private object DeserializeComplex(PicklerDeserializationState state, PickleOperation operation, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            // Operation won't be any of the primitive operations once this is called

            // -1 for the operation code we've already read
            var position = state.Reader.BaseStream.Position - 1;

            switch (operation)
            {
                case PickleOperation.Array:
                    return DeserializeArray(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Mscorlib:
                    // We don't memo mscorlib, it's cheaper to just have the single byte token
                    return mscorlib;

                case PickleOperation.AssemblyRef:
                    return DeserializeAsesmblyRef(state, position);

                case PickleOperation.AssemblyDef:
                    return DeserializeAsesmblyDef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ModuleRef:
                    return DeserializeModuleRef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ModuleDef:
                    return DeserializeModuleDef(state, position, genericTypeParameters, genericMethodParameters);

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
                        return state.SetMemo(position, new PickledTypeInfoRef(genericMethodParameters[genericParameterPosition]));
                    }

                case PickleOperation.TVar:
                    {
                        var genericParameterPosition = state.Reader.Read7BitEncodedInt();
                        if (genericTypeParameters == null)
                        {
                            throw new Exception("Encountered an TVar operation without a current type context");
                        }
                        return state.SetMemo(position, new PickledTypeInfoRef(genericTypeParameters[genericParameterPosition]));
                    }

                case PickleOperation.TypeRef:
                    return DeserializeTypeRef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.TypeDef:
                    return DeserializeTypeDef(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.FieldRef:
                    return DeserializeFieldInfo(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.PropertyRef:
                    return DeserializePropertyInfo(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.MethodRef:
                    return DeserializeMethodInfo(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.ConstructorRef:
                    return DeserializeConstructorInfo(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Delegate:
                    return DeserializeDelegate(state, position, genericTypeParameters, genericMethodParameters);

                case PickleOperation.Reducer:
                    {
                        return state.SetMemo(position, DeserializeReducer(state, genericTypeParameters, genericMethodParameters));
                    }

                case PickleOperation.ISerializable:
                    {
                        var objType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
                        return state.SetMemo(position, DeserializeISerializable(state, objType.Type, genericTypeParameters, genericMethodParameters));
                    }

                case PickleOperation.Object:
                    {
                        var (callback, objType) = DeserializeWithMemo(state, position, (PickledTypeInfo objType) =>
                        {
                            var result = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(objType.Type);
                            return state.SetMemo(position, result);
                        }, typeof(Type), genericTypeParameters, genericMethodParameters);

                        var uninitalizedObject = callback.Invoke();
                        DeserializeObject(state, uninitalizedObject, objType.Type, genericTypeParameters, genericMethodParameters);
                        return uninitalizedObject;
                    }

                default:
                    {
                        throw new Exception($"Unhandled PickleOperation '{operation}'");
                    }
            }
        }

        private (MemoCallback<R>, T) DeserializeWithMemo<T, R>(PicklerDeserializationState state, long position, Func<T, R> callback, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class where R : class
        {
            var memo = state.RegisterMemoCallback(position, callback);
            var result = DeserializeNonNull<T>(state, staticType, genericTypeParameters, genericMethodParameters);
            return (memo, result);
        }

        [return:MaybeNull]
        private T Deserialize<T>(PicklerDeserializationState state, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class
        {
            var obj = Deserialize(state, staticType, genericTypeParameters, genericMethodParameters);
            return obj as T;
        }

        private T DeserializeNonNull<T>(PicklerDeserializationState state, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters) where T : class
        {
            var position = state.Reader.BaseStream.Position;
            var obj = Deserialize(state, staticType, genericTypeParameters, genericMethodParameters);
            var result = obj as T;
            if (result == null)
            {
                throw new Exception($"Got unexpected null object at {position}");
            }
            return result;
        }

        private object? Deserialize(PicklerDeserializationState state, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            var position = state.Reader.BaseStream.Position;

            if (staticType.IsValueType)
            {
                // This is a static value type, we probably didn't write an operation out for this
                if (staticType.IsEnum)
                {
                    var enumTypeCode = Type.GetTypeCode(staticType);
                    var result = Enum.ToObject(staticType, ReadEnumerationValue(state.Reader, enumTypeCode));
                    return result;
                }
                else if (staticType == typeof(bool))
                {
                    return state.Reader.ReadBoolean();
                }
                else if (staticType == typeof(char))
                {
                    return state.Reader.ReadChar();
                }
                else if (staticType == typeof(sbyte))
                {
                    return state.Reader.ReadSByte();
                }
                else if (staticType == typeof(short))
                {
                    return state.Reader.ReadInt16();
                }
                else if (staticType == typeof(int))
                {
                    return state.Reader.ReadInt32();
                }
                else if (staticType == typeof(long))
                {
                    return state.Reader.ReadInt64();
                }
                else if (staticType == typeof(byte))
                {
                    return state.Reader.ReadByte();
                }
                else if (staticType == typeof(ushort))
                {
                    return state.Reader.ReadUInt16();
                }
                else if (staticType == typeof(uint))
                {
                    return state.Reader.ReadUInt32();
                }
                else if (staticType == typeof(ulong))
                {
                    return state.Reader.ReadUInt64();
                }
                else if (staticType == typeof(float))
                {
                    return state.Reader.ReadSingle();
                }
                else if (staticType == typeof(double))
                {
                    return state.Reader.ReadDouble();
                }
                else if (staticType == typeof(decimal))
                {
                    return state.Reader.ReadDecimal();
                }
                else if (staticType == typeof(IntPtr))
                {
                    return new IntPtr(state.Reader.ReadInt64());
                }
                else if (staticType == typeof(UIntPtr))
                {
                    return new UIntPtr(state.Reader.ReadUInt64());
                }
                else
                {
                    // A more complex type, but we know the type already and don't need memoisation
                    var operation = (PickleOperation)state.Reader.ReadByte();
                    switch (operation)
                    {
                        case PickleOperation.Reducer:
                            return DeserializeReducer(state, genericTypeParameters, genericMethodParameters);

                        case PickleOperation.ISerializable:
                            return DeserializeISerializable(state, staticType, genericTypeParameters, genericMethodParameters);

                        case PickleOperation.Object:
                            var uninitializedObject = Activator.CreateInstance(staticType);
                            DeserializeObject(state, uninitializedObject, staticType, genericTypeParameters, genericMethodParameters);
                            return uninitializedObject;

                        default:
                            throw new Exception($"Unexpected operation {operation} in a static type context of {staticType}");
                    }
                }
            }
            else
            {
                var operation = (PickleOperation)state.Reader.ReadByte();
                switch (operation)
                {
                    case PickleOperation.Null:
                        return null;

                    case PickleOperation.Memo:
                            return state.DoMemo();

                    case PickleOperation.Boolean:
                        {
                            var result = (object)state.Reader.ReadBoolean();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Char:
                        {
                            var result = (object)state.Reader.ReadChar();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Byte:
                        {
                            var result = (object)state.Reader.ReadByte();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Int16:
                        {
                            var result = (object)state.Reader.ReadInt16();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Int32:
                        {
                            var result = (object)state.Reader.ReadInt32();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Int64:
                        {
                            var result = (object)state.Reader.ReadInt64();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.SByte:
                        {
                            var result = (object)state.Reader.ReadSByte();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.UInt16:
                        {
                            var result = (object)state.Reader.ReadUInt16();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.UInt32:
                        {
                            var result = (object)state.Reader.ReadUInt32();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.UInt64:
                        {
                            var result = (object)state.Reader.ReadUInt64();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Single:
                        {
                            var result = (object)state.Reader.ReadSingle();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Double:
                        {
                            var result = (object)state.Reader.ReadDouble();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.Decimal:
                        {
                            var result = (object)state.Reader.ReadDecimal();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.DBNull:
                        return DBNull.Value;
                    case PickleOperation.String:
                        {
                            var result = state.Reader.ReadString();
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.IntPtr:
                        {
                            var result = new IntPtr(state.Reader.ReadInt64());
                            state.SetMemo(position, result);
                            return result;
                        }
                    case PickleOperation.UIntPtr:
                        {
                            var result = new UIntPtr(state.Reader.ReadUInt64());
                            state.SetMemo(position, result);
                            return result;
                        }

                    case PickleOperation.Enum:
                        {
                            var pickledEnumType = DeserializeNonNull<PickledTypeInfo>(state, typeof(Type), genericTypeParameters, genericMethodParameters);
                            var enumType = pickledEnumType.Type; 
                            var enumTypeCode = Type.GetTypeCode(enumType);
                            var result = Enum.ToObject(enumType, ReadEnumerationValue(state.Reader, enumTypeCode));
                            state.SetMemo(position, result);
                            return result;
                        }
                }

                return DeserializeComplex(state, operation, staticType, genericTypeParameters, genericMethodParameters);
            }
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

            var result = Deserialize(state, typeof(object), null, null);
            if (result is PickledObject pickledObject)
            {
                return pickledObject.Get();
            }
            state.DoFixups();
            state.DoStaticFields();
            return result;
        }
    }
}
