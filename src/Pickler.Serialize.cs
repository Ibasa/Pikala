using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    public sealed partial class Pickler
    {
        private static HashSet<Type> reflectionTypes = new HashSet<Type>()
        {
            typeof(Type),
            typeof(FieldInfo),
            typeof(PropertyInfo),
            typeof(MethodInfo),
            typeof(ConstructorInfo),
            typeof(EventInfo),
            typeof(Module),
            typeof(Assembly),
        };

        private Type SanatizeType(Type type)
        {
            if (type.IsArray)
            {
                // Sanatize the inner type
                var elementType = SanatizeType(type.GetElementType()!);
                if (type.IsSZArray)
                {
                    return elementType.MakeArrayType();
                }
                else
                {
                    return elementType.MakeArrayType(type.GetArrayRank());
                }
            }

            // We do a sanitisation pass here for reflection types, so that we don't see things like RuntimeType, just Type.
            if (type.IsAssignableTo(typeof(Assembly)))
            {
                // We only support serialising the actual runtime assembly type (either a real runtime assembly, or an assemblybuilder)
                if (!type.IsAssignableTo(runtimeAssemblyType) && type != runtimeAssemblyBuilderType && type != typeof(Assembly))
                {
                    throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from Assembly.");
                }
                return typeof(Assembly);
            }
            else if (type.IsAssignableTo(typeof(Module)))
            {
                if (!type.IsAssignableTo(runtimeModuleType) && type != runtimeModuleBuilderType && type != typeof(Module))
                {
                    throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from Module.");
                }
                return typeof(Module);
            }
            else if (type.IsAssignableTo(typeof(MemberInfo)))
            {
                if (type.IsAssignableTo(typeof(Type)))
                {
                    if (!type.IsAssignableTo(runtimeTypeType) && type != typeof(Type))
                    {
                        throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from Type.");
                    }
                    return typeof(Type);
                }
                else if (type.IsAssignableTo(typeof(FieldInfo)))
                {
                    if (!type.IsAssignableTo(runtimeFieldInfoType) && type != typeof(FieldInfo))
                    {
                        throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from FieldInfo.");
                    }
                    return typeof(FieldInfo);
                }
                else if (type.IsAssignableTo(typeof(PropertyInfo)))
                {
                    if (!type.IsAssignableTo(runtimePropertyInfoType) && type != typeof(PropertyInfo))
                    {
                        throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from PropertyInfo.");
                    }
                    return typeof(PropertyInfo);
                }
                else if (type.IsAssignableTo(typeof(EventInfo)))
                {
                    if (!type.IsAssignableTo(runtimeEventInfoType) && type != typeof(EventInfo))
                    {
                        throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from EventInfo.");
                    }
                    return typeof(EventInfo);
                }
                else if (type.IsAssignableTo(typeof(MethodBase)))
                {
                    if (type.IsAssignableTo(typeof(ConstructorInfo)) && type != typeof(ConstructorInfo))
                    {
                        if (!type.IsAssignableTo(runtimeConstructorInfoType))
                        {
                            throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from ConstructorInfo.");
                        }
                        return typeof(ConstructorInfo);
                    }
                    else if (type.IsAssignableTo(typeof(MethodInfo)))
                    {
                        if (!type.IsAssignableTo(runtimeMethodInfoType) && type != typeof(MethodInfo))
                        {
                            throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from MethodInfo.");
                        }
                        return typeof(MethodInfo);
                    }
                    else if (type != typeof(MethodBase))
                    {
                        throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from MethodBase.");
                    }
                    return typeof(MethodBase);
                }
                else if (type != typeof(MemberInfo))
                {
                    throw new Exception($"Type '{type}' is not automaticly serializable as it inherits from MemberInfo.");
                }
                return typeof(MemberInfo);
            }

            return type;
        }

        /// <summary>
        /// There are some objects that we shouldn't bother to memoise because it's cheaper to just write their tokens.
        /// </summary>
        private bool ShouldMemo(object? obj)
        {
            // Don't bother memoing the well known types, they only take a byte to write out anyway
            foreach (var type in _wellKnownTypes)
            {
                if (Object.ReferenceEquals(type, obj)) return false;
            }

            // mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib)) { return false; }

            // The manifest module for mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib.ManifestModule)) { return false; }

            return true;
        }

        private void AddMemo(PicklerSerializationState state, bool isValueType, long position, object obj)
        {
            if (!isValueType)
            {
                System.Diagnostics.Debug.Assert(ShouldMemo(obj), "Tried to call AddMemo for an object that shouldn't be memoised");
                state.AddMemo(position, obj);
            }
        }

        private bool PickleByValue(Assembly assembly)
        {
            // We never pickle mscorlib by value, don't even give the user a choice
            if (assembly == mscorlib) { return false; }
            // We also never pickle pikala itself.
            // Firstly you need pikala present already on both sides so that you can even start the deserialisation.
            // Secondly it looks like an LGPL violation.
            if (assembly == pikala) { return false; }

            var mode = _assemblyPickleMode(assembly);

            switch (mode)
            {
                case AssemblyPickleMode.PickleByValue: return true;
                case AssemblyPickleMode.PickleByReference: return false;
                default:
                    return assembly.IsDynamic || assembly.Location == "";
            }
        }

        private static void WriteEnumerationValue(BinaryWriter writer, TypeCode typeCode, object value)
        {
            switch (typeCode)
            {
                case TypeCode.SByte:
                    writer.Write((sbyte)value);
                    return;
                case TypeCode.Int16:
                    writer.Write((short)value);
                    return;
                case TypeCode.Int32:
                    writer.Write((int)value);
                    return;
                case TypeCode.Int64:
                    writer.Write((long)value);
                    return;

                case TypeCode.Byte:
                    writer.Write((byte)value);
                    return;
                case TypeCode.UInt16:
                    writer.Write((ushort)value);
                    return;
                case TypeCode.UInt32:
                    writer.Write((uint)value);
                    return;
                case TypeCode.UInt64:
                    writer.Write((ulong)value);
                    return;
            }

            throw new Exception($"Invalid type code '{typeCode}' for enumeration");
        }

        private void SerializeSignatureElement(PicklerSerializationState state, SignatureElement signature)
        {
            if (signature is SignatureType st)
            {
                state.Writer.Write((byte)SignatureElementOperation.Type);
                SerializeType(state, st.Type, null, null);
            }
            else if (signature is SignatureGenericParameter sgp)
            {
                var operation = sgp.IsGenericTypeParameter ? SignatureElementOperation.TVar : SignatureElementOperation.MVar;
                state.Writer.Write((byte)operation);
                state.Writer.Write7BitEncodedInt(sgp.GenericParameterPosition);
            }
            else if (signature is SignatureConstructedGenericType scgt)
            {
                state.Writer.Write((byte)SignatureElementOperation.Generic);
                SerializeType(state, scgt.GenericTypeDefinition, null, null);
                state.Writer.Write7BitEncodedInt(scgt.GenericArguments.Length);
                foreach (var genericArgument in scgt.GenericArguments)
                {
                    SerializeSignatureElement(state, genericArgument);
                }
            }
            else if (signature is SignatureArray sa)
            {
                state.Writer.Write((byte)SignatureElementOperation.Array);
                state.Writer.Write7BitEncodedInt(sa.Rank);
                SerializeSignatureElement(state, sa.ElementType);
            }
            else if (signature is SignatureByRef sbr)
            {
                state.Writer.Write((byte)SignatureElementOperation.ByRef);
                SerializeSignatureElement(state, sbr.ElementType);
            }
            else if (signature is SignaturePointer si)
            {
                state.Writer.Write((byte)SignatureElementOperation.Pointer);
                SerializeSignatureElement(state, si.ElementType);
            }
            else if (signature is SignatureReq sr)
            {
                state.Writer.Write((byte)SignatureElementOperation.Modreq);
                SerializeSignatureElement(state, sr.ElementType);
                SerializeType(state, sr.RequiredModifier, null, null);
            }
            else if (signature is SignatureOpt so)
            {
                state.Writer.Write((byte)SignatureElementOperation.Modopt);
                SerializeSignatureElement(state, so.ElementType);
                SerializeType(state, so.OptionalModifier, null, null);
            }
            else
            {
                throw new NotImplementedException($"Unhandled SignatureElement: {signature}");
            }
        }

        private void SerializeSignature(PicklerSerializationState state, Signature signature)
        {
            state.Writer.Write(signature.Name);
            state.Writer.Write7BitEncodedInt(signature.GenericParameterCount);
            SerializeSignatureElement(state, signature.ReturnType);
            state.Writer.Write7BitEncodedInt(signature.Parameters.Length);
            foreach (var param in signature.Parameters)
            {
                SerializeSignatureElement(state, param);
            }
        }

        private void SerializeConstructorHeader(PicklerSerializationState state, Type[]? genericTypeParameters, ConstructorInfo constructor)
        {
            state.Writer.Write((int)constructor.Attributes);
            state.Writer.Write((int)constructor.CallingConvention);

            var constructorParameters = constructor.GetParameters();

            bool hasmodifiers = false;
            foreach (var parameter in constructorParameters)
            {
                if (parameter.GetRequiredCustomModifiers().Length > 0 || parameter.GetOptionalCustomModifiers().Length > 0)
                {
                    hasmodifiers = true;
                    break;
                }
            }

            state.Writer.Write7BitEncodedInt((constructorParameters.Length << 1) | (hasmodifiers ? 1 : 0));

            foreach (var parameter in constructorParameters)
            {
                SerializeType(state, parameter.ParameterType, genericTypeParameters, null);

                if (hasmodifiers)
                {
                    // Combine the count of required and optional parameters and write that out
                    var reqmods = parameter.GetRequiredCustomModifiers();
                    var optmods = parameter.GetOptionalCustomModifiers();

                    if (reqmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 required modifiers"); }
                    if (optmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 optional modifiers"); }

                    var interleave = (reqmods.Length << 4) | optmods.Length;

                    state.Writer.Write((byte)interleave);
                    foreach (var reqmod in reqmods)
                    {
                        SerializeType(state, reqmod, genericTypeParameters, null);
                    }
                    foreach (var optmod in optmods)
                    {
                        SerializeType(state, optmod, genericTypeParameters, null);
                    }
                }
            }
            foreach (var parameter in constructorParameters)
            {
                state.Writer.WriteNullableString(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);

                if (parameter.Attributes.HasFlag(ParameterAttributes.HasDefault))
                {
                    WriteConstant(state, parameter.DefaultValue, parameter.ParameterType);
                }
            }

            var methodBody = constructor.GetMethodBody();
            System.Diagnostics.Debug.Assert(methodBody != null, "GetMethodBody returned null for a constructor");

            state.Writer.Write(methodBody.InitLocals);

            state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
            foreach (var local in methodBody.LocalVariables)
            {
                SerializeType(state, local.LocalType, genericTypeParameters, null);
            }

            var collectedTypes = CollectTypes(genericTypeParameters, constructor.Module, null, methodBody);
            state.Writer.Write7BitEncodedInt(collectedTypes.Count);
            foreach (var type in collectedTypes)
            {
                SerializeType(state, type, genericTypeParameters, null);
            }
        }

        private void SerializeMethodHeader(PicklerSerializationState state, Type[]? genericTypeParameters, MethodInfo method)
        {
            state.Writer.Write(method.Name);
            state.Writer.Write((int)method.Attributes);
            state.Writer.Write((int)method.MethodImplementationFlags);
            state.Writer.Write((int)method.CallingConvention);

            var genericMethodParameters = method.GetGenericArguments();
            state.Writer.Write7BitEncodedInt(genericMethodParameters.Length);
            foreach (var parameter in genericMethodParameters)
            {
                state.Writer.Write(parameter.Name);
            }

            {
                SerializeType(state, method.ReturnType, genericTypeParameters, genericMethodParameters);

                // Combine the count of required and optional parameters and write that out
                var reqmods = method.ReturnParameter.GetRequiredCustomModifiers();
                var optmods = method.ReturnParameter.GetOptionalCustomModifiers();

                if (reqmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 required modifiers"); }
                if (optmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 optional modifiers"); }

                var interleave = (reqmods.Length << 4) | optmods.Length;

                state.Writer.Write((byte)interleave);
                foreach (var reqmod in reqmods)
                {
                    SerializeType(state, reqmod, genericTypeParameters, null);
                }
                foreach (var optmod in optmods)
                {
                    SerializeType(state, optmod, genericTypeParameters, null);
                }
            }

            var methodParameters = method.GetParameters();
            bool hasmodifiers = false;
            foreach (var parameter in methodParameters)
            {
                if (parameter.GetRequiredCustomModifiers().Length > 0 || parameter.GetOptionalCustomModifiers().Length > 0)
                {
                    hasmodifiers = true;
                    break;
                }
            }

            state.Writer.Write7BitEncodedInt((methodParameters.Length << 1) | (hasmodifiers ? 1 : 0));

            foreach (var parameter in methodParameters)
            {
                SerializeType(state, parameter.ParameterType, genericTypeParameters, genericMethodParameters);

                if (hasmodifiers)
                {
                    // Combine the count of required and optional parameters and write that out
                    var reqmods = parameter.GetRequiredCustomModifiers();
                    var optmods = parameter.GetOptionalCustomModifiers();

                    if (reqmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 required modifiers"); }
                    if (optmods.Length > 7) { throw new NotSupportedException("Pikala does not support more than 7 optional modifiers"); }

                    var interleave = (reqmods.Length << 4) | optmods.Length;

                    state.Writer.Write((byte)interleave);
                    foreach (var reqmod in reqmods)
                    {
                        SerializeType(state, reqmod, genericTypeParameters, null);
                    }
                    foreach (var optmod in optmods)
                    {
                        SerializeType(state, optmod, genericTypeParameters, null);
                    }
                }
            }
            foreach (var parameter in methodParameters)
            {
                // 22.33: 9. Name can be null or non-null
                state.Writer.WriteNullableString(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);

                if (parameter.Attributes.HasFlag(ParameterAttributes.HasDefault))
                {
                    WriteConstant(state, parameter.DefaultValue, parameter.ParameterType);
                }
            }

            if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || method.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || method.Attributes.HasFlag(MethodAttributes.Abstract))
            {
                var methodBody = method.GetMethodBody();
                System.Diagnostics.Debug.Assert(methodBody == null, "GetMethodBody returned non-null unexpectedly");
            }
            else
            {
                var methodBody = method.GetMethodBody();
                System.Diagnostics.Debug.Assert(methodBody != null, "GetMethodBody returned null unexpectedly");

                state.Writer.Write(methodBody.InitLocals);

                state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
                foreach (var local in methodBody.LocalVariables)
                {
                    SerializeType(state, local.LocalType, genericTypeParameters, genericMethodParameters);
                }

                var collectedTypes = CollectTypes(genericTypeParameters, method.Module, genericMethodParameters, methodBody);
                state.Writer.Write7BitEncodedInt(collectedTypes.Count);
                foreach (var type in collectedTypes)
                {
                    SerializeType(state, type, genericTypeParameters, genericMethodParameters);
                }
            }
        }

        private HashSet<Type> CollectTypes(Type[]? genericTypeParameters, Module methodModule, Type[]? genericMethodParameters, MethodBody methodBody)
        {
            OpCode ReadOpCode(BinaryReader reader)
            {
                var opCodeByte = reader.ReadByte();
                if (opCodeByte == 0xfe)
                {
                    opCodeByte = reader.ReadByte();
                    return _twoByteOpCodes[opCodeByte];
                }
                else
                {
                    return _oneByteOpCodes[opCodeByte];
                }
            }

            var types = new HashSet<Type>();
            var ilStream = new MemoryStream(methodBody.GetILAsByteArray());
            var ilReader = new BinaryReader(ilStream);
            while (ilStream.Position < ilStream.Length)
            {
                var opCode = ReadOpCode(ilReader);

                // Write the operaand in a way that can be deserialized on the other side
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;

                    case OperandType.InlineSwitch:
                        {
                            int length = ilReader.ReadInt32();
                            for (int i = 0; i < length; ++i)
                            {
                                ilReader.ReadInt32();
                            }
                            break;
                        }

                    case OperandType.InlineString:
                        {
                            ilReader.ReadInt32();
                            break;
                        }

                    case OperandType.InlineType:
                        {
                            var typeToken = ilReader.ReadInt32();
                            var typeInfo = methodModule.ResolveType(typeToken, genericTypeParameters, genericMethodParameters);
                            types.Add(typeInfo);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldToken = ilReader.ReadInt32();
                            var fieldInfo = methodModule.ResolveField(fieldToken, genericTypeParameters, genericMethodParameters);
                            System.Diagnostics.Debug.Assert(fieldInfo != null, "ResolveField unexpectedly returned null");
                            if (fieldInfo.DeclaringType != null)
                            {
                                types.Add(fieldInfo.DeclaringType);
                            }
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodToken = ilReader.ReadInt32();
                            var methodInfo = methodModule.ResolveMethod(methodToken, genericTypeParameters, genericMethodParameters);
                            System.Diagnostics.Debug.Assert(methodInfo != null, "ResolveMethod unexpectedly returned null");
                            if (methodInfo.DeclaringType != null)
                            {
                                types.Add(methodInfo.DeclaringType);
                            }
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var memberToken = ilReader.ReadInt32();
                            var memberInfo = methodModule.ResolveMember(memberToken, genericTypeParameters, genericMethodParameters);
                            System.Diagnostics.Debug.Assert(memberInfo != null, "ResolveMember unexpectedly returned null");
                            if (memberInfo.DeclaringType != null)
                            {
                                types.Add(memberInfo.DeclaringType);
                            }
                            break;
                        }

                    case OperandType.ShortInlineI:
                        {
                            ilReader.ReadByte();
                            break;
                        }

                    case OperandType.InlineI:
                        {
                            ilReader.ReadInt32();
                            break;
                        }

                    case OperandType.ShortInlineR:
                        {
                            ilReader.ReadSingle();
                            break;
                        }

                    case OperandType.InlineR:
                        {
                            ilReader.ReadDouble();
                            break;
                        }

                    case OperandType.ShortInlineVar:
                        {
                            ilReader.ReadByte();
                            break;
                        }

                    case OperandType.InlineVar:
                        {
                            ilReader.ReadInt16();
                            break;
                        }

                    case OperandType.ShortInlineBrTarget:
                        {
                            ilReader.ReadByte();
                            break;
                        }

                    case OperandType.InlineBrTarget:
                        {
                            ilReader.ReadInt32();
                            break;
                        }

                    default:
                        throw new NotImplementedException($"Unknown Opcode.OperandType {opCode.OperandType}");
                }
            }

            return types;
        }

        private void SerializeMethodBody(PicklerSerializationState state, Type[]? genericTypeParameters, Module methodModule, Type[]? genericMethodParameters, MethodBody methodBody)
        {
            var ilStream = new MemoryStream(methodBody.GetILAsByteArray());
            var ilReader = new BinaryReader(ilStream);

            while (ilStream.Position < ilStream.Length)
            {
                // Copy the opcode to the output
                var opCodeByte = ilReader.ReadByte();
                state.Writer.Write(opCodeByte);
                OpCode opCode;
                if (opCodeByte == 0xfe)
                {
                    opCodeByte = ilReader.ReadByte();
                    state.Writer.Write(opCodeByte);
                    opCode = _twoByteOpCodes[opCodeByte];
                }
                else
                {
                    opCode = _oneByteOpCodes[opCodeByte];
                }

                // Write the operaand in a way that can be deserialized on the other side
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;

                    case OperandType.InlineSwitch:
                        {
                            int length = ilReader.ReadInt32();
                            state.Writer.Write(length);
                            for (int i = 0; i < length; ++i)
                            {
                                var offset = ilReader.ReadInt32();
                                state.Writer.Write(offset);
                            }
                            break;
                        }

                    case OperandType.InlineString:
                        {
                            var stringToken = ilReader.ReadInt32();
                            var stringValue = methodModule.ResolveString(stringToken);
                            state.Writer.Write(stringValue);
                            break;
                        }

                    case OperandType.InlineType:
                        {
                            var typeToken = ilReader.ReadInt32();
                            var typeInfo = methodModule.ResolveType(typeToken, genericTypeParameters, genericMethodParameters);
                            if (typeInfo == null) throw new Exception($"Could not look up type for metadata token: 0x{typeToken:x}");
                            SerializeType(state, typeInfo, genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldToken = ilReader.ReadInt32();
                            var fieldInfo = methodModule.ResolveField(fieldToken, genericTypeParameters, genericMethodParameters);
                            if (fieldInfo == null) throw new Exception($"Could not look up field for metadata token: 0x{fieldToken:x}");
                            SerializeFieldInfo(state, fieldInfo);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodToken = ilReader.ReadInt32();
                            var methodBase = methodModule.ResolveMethod(methodToken, genericTypeParameters, genericMethodParameters);
                            if (methodBase == null) throw new Exception($"Could not look up method for metadata token: 0x{methodToken:x}");
                            SerializeMethodBase(state, methodBase);
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var memberToken = ilReader.ReadInt32();
                            var memberInfo = methodModule.ResolveMember(memberToken, genericTypeParameters, genericMethodParameters);
                            if (memberInfo == null) throw new Exception($"Could not look up member for metadata token: 0x{memberToken:x}");
                            SerializeMemberInfo(state, memberInfo);
                            break;
                        }

                    case OperandType.ShortInlineI:
                        {
                            state.Writer.Write(ilReader.ReadByte());
                            break;
                        }

                    case OperandType.InlineI:
                        {
                            state.Writer.Write(ilReader.ReadInt32());
                            break;
                        }

                    case OperandType.ShortInlineR:
                        {
                            state.Writer.Write(ilReader.ReadSingle());
                            break;
                        }

                    case OperandType.InlineR:
                        {
                            state.Writer.Write(ilReader.ReadDouble());
                            break;
                        }

                    case OperandType.ShortInlineVar:
                        {
                            state.Writer.Write(ilReader.ReadByte());
                            break;
                        }

                    case OperandType.InlineVar:
                        {
                            state.Writer.Write(ilReader.ReadInt16());
                            break;
                        }

                    case OperandType.ShortInlineBrTarget:
                        {
                            state.Writer.Write(ilReader.ReadByte());
                            break;
                        }

                    case OperandType.InlineBrTarget:
                        {
                            state.Writer.Write(ilReader.ReadInt32());
                            break;
                        }

                    default:
                        throw new NotImplementedException($"Unknown Opcode.OperandType {opCode.OperandType}");
                }
            }

            // 0xFF to mark the end of the method body
            state.Writer.Write((byte)0xFF);
        }

        private Func<PicklerSerializationState, Action<PicklerSerializationState>>? SerializeModuleDef(PicklerSerializationState state, Module module, long position)
        {
            state.Writer.Write((byte)ModuleOperation.ModuleDef);
            state.Writer.Write(module.Name);
            var assemblyPhase3 = SerializeAssembly(state, module.Assembly);

            AddMemo(state, false, position, module);

            return state =>
            {
                var fields = module.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var field in fields)
                {
                    state.Writer.Write(field.Name);
                    state.Writer.Write((int)field.Attributes);

                    // We expect all module fields to be RVA fields
                    System.Diagnostics.Debug.Assert(field.Attributes.HasFlag(FieldAttributes.HasFieldRVA), "Module field was not an RVA field");
                    // with a StructLayoutAttribute
                    System.Diagnostics.Debug.Assert(field.FieldType.StructLayoutAttribute != null, "RVA field did not have struct layout attribute");

                    var size = field.FieldType.StructLayoutAttribute.Size;
                    var value = field.GetValue(null);

                    var pin = System.Runtime.InteropServices.GCHandle.Alloc(value, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        var addr = pin.AddrOfPinnedObject();
                        unsafe
                        {
                            var bytes = new ReadOnlySpan<byte>(addr.ToPointer(), size);

                            var allZero = true;
                            for (int i = 0; i < size; ++i)
                            {
                                if (bytes[i] != 0)
                                {
                                    allZero = false;
                                    break;
                                }
                            }

                            if (allZero)
                            {
                                state.Writer.Write(-size);
                            }
                            else
                            {
                                state.Writer.Write(size);
                                state.Writer.Write(bytes);
                            }
                        }
                    }
                    finally
                    {
                        pin.Free();
                    }
                }

                var methods = module.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                state.Writer.Write7BitEncodedInt(methods.Length);
                foreach (var method in methods)
                {
                    SerializeMethodHeader(state, null, method);
                }

                return state =>
                {
                    InvokePhase(state, assemblyPhase3);

                    WriteCustomAttributes(state, module.CustomAttributes.ToArray());

                    foreach (var field in fields)
                    {
                        WriteCustomAttributes(state, field.CustomAttributes.ToArray());
                    }

                    foreach (var method in methods)
                    {
                        WriteCustomAttributes(state, method.CustomAttributes.ToArray());
                        SerializeMethodBody(state, null, method.Module, method.GetGenericArguments(), method.GetMethodBody());
                    }
                };
            };
        }

        private void SerializeDef(PicklerSerializationState state, Type type, Type[]? genericParameters, Func<PicklerSerializationState, Action<PicklerSerializationState>>? parentPhase2)
        {
            if (type.IsValueType)
            {
                // Value types don't ever have any base class
            }
            else if (type.IsInterface)
            {
                // Interface types don't have any base class
            }
            else
            {
                System.Diagnostics.Debug.Assert(type.BaseType != null, "Got user defined type that had no base type");
                SerializeType(state, type.BaseType, genericParameters, null);
            }

            var interfaces = type.GetInterfaces();
            state.Writer.Write7BitEncodedInt(interfaces.Length);
            foreach (var interfaceType in interfaces)
            {
                SerializeType(state, interfaceType, genericParameters, null);

                var interfaceMap = type.GetInterfaceMap(interfaceType);
                var mappedMethods = new List<(Signature, Signature)>();
                for (int i = 0; i < interfaceMap.InterfaceMethods.Length; ++i)
                {
                    var targetMethod = interfaceMap.TargetMethods[i];

                    if (targetMethod.DeclaringType != interfaceMap.TargetType)
                    {
                        // We only care about storing in the map methods that THIS class needs to override. Any interface
                        // methods that our base classes are implementing don
                    }
                    else
                    {
                        var interfaceMethodSignature = Signature.GetSignature(interfaceMap.InterfaceMethods[i]);
                        var targetMethodSignature = Signature.GetSignature(targetMethod);
                        var isNewSlot = targetMethod.Attributes.HasFlag(MethodAttributes.NewSlot);

                        if (interfaceMethodSignature != targetMethodSignature || isNewSlot)
                        {
                            mappedMethods.Add((interfaceMethodSignature, targetMethodSignature));
                        }
                    }
                }

                state.Writer.Write7BitEncodedInt(mappedMethods.Count);
                foreach (var (interfaceMethod, targetMethod) in mappedMethods)
                {
                    SerializeSignature(state, interfaceMethod);
                    SerializeSignature(state, targetMethod);
                }
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(fields.Length);
            foreach (var field in fields)
            {
                state.Writer.Write(field.Name);
                state.Writer.Write((int)field.Attributes);
                SerializeType(state, field.FieldType, genericParameters, null);
                if (field.Attributes.HasFlag(FieldAttributes.Literal))
                {
                    WriteConstant(state, field.GetRawConstantValue(), field.FieldType);
                }
            }

            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(constructors.Length);
            foreach (var constructor in constructors)
            {
                SerializeConstructorHeader(state, genericParameters, constructor);
            }

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(methods.Length);
            foreach (var method in methods)
            {
                SerializeMethodHeader(state, genericParameters, method);
            }

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(properties.Length);
            foreach (var property in properties)
            {
                state.Writer.Write(property.Name);
                state.Writer.Write((int)property.Attributes);
                SerializeType(state, property.PropertyType, genericParameters, null);
                var indexParameters = property.GetIndexParameters();
                state.Writer.Write7BitEncodedInt(indexParameters.Length);
                foreach (var indexParameter in indexParameters)
                {
                    SerializeType(state, indexParameter.ParameterType, genericParameters, null);
                }

                var accessors = property.GetAccessors(true);
                var getter = property.GetMethod;
                var setter = property.SetMethod;
                var otherCount = accessors.Length - (
                        (getter == null ? 0 : 1) +
                        (setter == null ? 0 : 1));

                var count = (otherCount << 2) + (setter == null ? 0 : 2) + (getter == null ? 0 : 1);
                state.Writer.Write7BitEncodedInt(count);
                // Make sure get and set are first
                // GetAccessors should return get first
                System.Diagnostics.Debug.Assert(getter == null || accessors[0] == getter);
                // GetAccessors should return set after get, that is either first if get is null, or second.
                System.Diagnostics.Debug.Assert(setter == null || accessors[getter == null ? 0 : 1] == setter);
                foreach (var accessor in accessors)
                {
                    SerializeSignature(state, Signature.GetSignature(accessor));
                }
            }

            var events = type.GetEvents(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(events.Length);
            foreach (var evt in events)
            {
                state.Writer.Write(evt.Name);
                state.Writer.Write((int)evt.Attributes);
                SerializeType(state, evt.EventHandlerType, genericParameters, null);

                var others = evt.GetOtherMethods();
                var raiser = evt.RaiseMethod;

                var count = (others.Length << 1) + (raiser == null ? 0 : 1);
                state.Writer.Write7BitEncodedInt(count);
                // add and remove look to be required so we don't include them in count
                // 22.13 Event : 0x14
                // 9. For each row, there shall be one add_ and one remove_ row in the MethodSemantics table [ERROR]
                SerializeSignature(state, Signature.GetSignature(evt.AddMethod!));
                SerializeSignature(state, Signature.GetSignature(evt.RemoveMethod!));

                if (raiser != null)
                {
                    SerializeSignature(state, Signature.GetSignature(raiser));
                }
                foreach (var other in others)
                {
                    SerializeSignature(state, Signature.GetSignature(other));
                }
            }

            var parentPhase3 = InvokePhase(state, parentPhase2);

            state.PushTrailer(() =>
            {
                InvokePhase(state, parentPhase3);

                // Custom attributes might be self referencing so make sure all ctors and things are setup first
                WriteCustomAttributes(state, type.CustomAttributes.ToArray());

                foreach (var field in fields)
                {
                    WriteCustomAttributes(state, field.CustomAttributes.ToArray());
                }
                foreach (var constructor in constructors)
                {
                    WriteCustomAttributes(state, constructor.CustomAttributes.ToArray());
                    SerializeMethodBody(state, genericParameters, constructor.Module, null, constructor.GetMethodBody());
                }
                foreach (var method in methods)
                {
                    WriteCustomAttributes(state, method.CustomAttributes.ToArray());
                    var methodBody = method.GetMethodBody();
                    if (methodBody != null)
                    {
                        SerializeMethodBody(state, genericParameters, method.Module, method.GetGenericArguments(), methodBody);
                    }
                }
                foreach (var property in properties)
                {
                    WriteCustomAttributes(state, property.CustomAttributes.ToArray());
                }
                foreach (var evt in events)
                {
                    WriteCustomAttributes(state, evt.CustomAttributes.ToArray());
                }
            }, () =>
            {
                var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in staticFields)
                {
                    if (!field.IsLiteral && !field.IsInitOnly)
                    {
                        state.Writer.Write(field.Name);
                        var value = field.GetValue(null);
                        Serialize(state, value, field.FieldType);
                    }
                }
            });
        }

        private void WriteConstant(PicklerSerializationState state, object? value, Type constantType)
        {
            if (constantType == typeof(object))
            {
                // This has to be null
                return;
            }
            else if (constantType == typeof(string))
            {
                state.Writer.WriteNullableString((string?)value);
                return;
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (constantType.IsEnum)
            {
                WriteEnumerationValue(state.Writer, Type.GetTypeCode(constantType), value);
            }
            else if (constantType == typeof(bool))
            {
                state.Writer.Write((bool)value);
            }
            else if (constantType == typeof(char))
            {
                state.Writer.Write((char)value);
            }
            else if (constantType == typeof(byte))
            {
                state.Writer.Write((byte)value);
            }
            else if (constantType == typeof(sbyte))
            {
                state.Writer.Write((sbyte)value);
            }
            else if (constantType == typeof(short))
            {
                state.Writer.Write((short)value);
            }
            else if (constantType == typeof(ushort))
            {
                state.Writer.Write((ushort)value);
            }
            else if (constantType == typeof(int))
            {
                state.Writer.Write((int)value);
            }
            else if (constantType == typeof(uint))
            {
                state.Writer.Write((uint)value);
            }
            else if (constantType == typeof(long))
            {
                state.Writer.Write((long)value);
            }
            else if (constantType == typeof(ulong))
            {
                state.Writer.Write((ulong)value);
            }
            else if (constantType == typeof(float))
            {
                state.Writer.Write((float)value);
            }
            else if (constantType == typeof(double))
            {
                state.Writer.Write((double)value);
            }
            else if (constantType == typeof(decimal))
            {
                state.Writer.Write((decimal)value);
            }
            else
            {
                throw new NotImplementedException($"Unrecognized type '{constantType}' for constant");
            }
        }

        private void WriteCustomAttributes(PicklerSerializationState state, CustomAttributeData[] attributes)
        {
            void WriteType(Type type)
            {
                if (type.IsPrimitive)
                {
                    switch (Type.GetTypeCode(type))
                    {
                        case TypeCode.SByte:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SByte);
                            break;
                        case TypeCode.Byte:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Byte);
                            break;
                        case TypeCode.Char:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Char);
                            break;
                        case TypeCode.Boolean:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Boolean);
                            break;
                        case TypeCode.Int16:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int16);
                            break;
                        case TypeCode.UInt16:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt16);
                            break;
                        case TypeCode.Int32:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int32);
                            break;
                        case TypeCode.UInt32:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt32);
                            break;
                        case TypeCode.Int64:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Int64);
                            break;
                        case TypeCode.UInt64:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.UInt64);
                            break;
                        case TypeCode.Single:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Single);
                            break;
                        case TypeCode.Double:
                            state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Double);
                            break;
                        default:
                            throw new Exception("Invalid primitive type for attribute");
                    }
                }
                else if (type.IsEnum)
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Enum);
                    SerializeType(state, type, null, null);
                }
                else if (type == typeof(string))
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.String);
                }
                else if (type == typeof(Type))
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.Type);
                }
                else if (type.IsArray)
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SZArray);
                    WriteType(type.GetElementType()!);
                }
                else
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.TaggedObject);
                }
            }


            void WriteValue(Type type, object? value)
            {
                if (type.IsEnum)
                {
                    switch (Type.GetTypeCode(Enum.GetUnderlyingType(type)))
                    {
                        case TypeCode.SByte:
                            state.Writer.Write((sbyte)value!);
                            break;
                        case TypeCode.Byte:
                            state.Writer.Write((byte)value!);
                            break;
                        case TypeCode.Int16:
                            state.Writer.Write((short)value!);
                            break;
                        case TypeCode.UInt16:
                            state.Writer.Write((ushort)value!);
                            break;
                        case TypeCode.Int32:
                            state.Writer.Write((int)value!);
                            break;
                        case TypeCode.UInt32:
                            state.Writer.Write((uint)value!);
                            break;
                        case TypeCode.Int64:
                            state.Writer.Write((long)value!);
                            break;
                        case TypeCode.UInt64:
                            state.Writer.Write((ulong)value!);
                            break;
                        default:
                            throw new Exception("Invalid base type for enum");
                    }
                }
                else if (type == typeof(string))
                {
                    state.Writer.WriteNullableString(value as string);
                }
                else if (type == typeof(Type))
                {
                    if (value == null)
                    {
                        state.Writer.Write(false);
                    }
                    else
                    {
                        // SerializeType doesn't support null so we write a bool flag out first to tell if this is null or not
                        state.Writer.Write(true);
                        SerializeType(state, type, null, null);
                    }
                }
                else if (type.IsArray)
                {
                    if (value == null)
                    {
                        state.Writer.Write7BitEncodedInt(-1);
                    }
                    else
                    {
                        Type elementType = type.GetElementType()!;

                        // argument might be a ReadOnlyCollection[CustomAttributeTypedArgument] but we should write that as just an array of values
                        if (value is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> collection)
                        {
                            state.Writer.Write7BitEncodedInt(collection.Count);
                            for (int i = 0; i < collection.Count; ++i)
                            {
                                WriteValue(elementType, collection[i].Value);
                            }
                        }
                        else if (value is Array arr)
                        {
                            state.Writer.Write7BitEncodedInt(arr.Length);
                            for (int i = 0; i < arr.Length; ++i)
                            {
                                WriteValue(elementType, arr.GetValue(i));
                            }
                        }
                        else
                        {
                            throw new Exception($"Unsupported array type for attrtibute value: {value.GetType()}");
                        }
                    }
                }
                else if (type.IsPrimitive)
                {
                    switch (Type.GetTypeCode(type))
                    {
                        case TypeCode.SByte:
                            state.Writer.Write((sbyte)value!);
                            break;
                        case TypeCode.Byte:
                            state.Writer.Write((byte)value!);
                            break;
                        case TypeCode.Char:
                            state.Writer.Write((char)value!);
                            break;
                        case TypeCode.Boolean:
                            state.Writer.Write((bool)value!);
                            break;
                        case TypeCode.Int16:
                            state.Writer.Write((short)value!);
                            break;
                        case TypeCode.UInt16:
                            state.Writer.Write((ushort)value!);
                            break;
                        case TypeCode.Int32:
                            state.Writer.Write((int)value!);
                            break;
                        case TypeCode.UInt32:
                            state.Writer.Write((uint)value!);
                            break;
                        case TypeCode.Int64:
                            state.Writer.Write((long)value!);
                            break;
                        case TypeCode.UInt64:
                            state.Writer.Write((ulong)value!);
                            break;
                        case TypeCode.Single:
                            state.Writer.Write((float)value!);
                            break;
                        case TypeCode.Double:
                            state.Writer.Write((double)value!);
                            break;
                        default:
                            throw new Exception("Invalid primitive type for attribute");
                    }
                }
                else if (type == typeof(object))
                {
                    // Tagged object case. Type instances aren't actually Type, they're some subclass (such as RuntimeType or
                    // TypeBuilder), so we need to canonicalize this case back to Type. If we have a null value we follow the convention
                    // used by C# and emit a null typed as a string (it doesn't really matter what type we pick as long as it's a
                    // reference type).
                    Type ot = value == null ? typeof(string) : value is Type ? typeof(Type) : value.GetType();

                    WriteType(ot);
                    WriteValue(ot, value);
                }
                else
                {
                    throw new Exception($"Unsupported type for attrtibute value: {type}");
                }
            }

            state.Writer.Write7BitEncodedInt(attributes.Length);
            foreach (var attribute in attributes)
            {
                SerializeConstructorInfo(state, attribute.Constructor);

                foreach (var argument in attribute.ConstructorArguments)
                {
                    WriteValue(argument.ArgumentType, argument.Value);
                }

                var fieldCount = attribute.NamedArguments.Count(argument => argument.IsField);
                var propertyCount = attribute.NamedArguments.Count - fieldCount;

                state.Writer.Write7BitEncodedInt(fieldCount);
                state.Writer.Write7BitEncodedInt(propertyCount);

                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.IsField)
                    {
                        var value = argument.TypedValue;
                        WriteType(value.ArgumentType);
                        state.Writer.Write(argument.MemberName);
                        WriteValue(value.ArgumentType, value.Value);
                    }
                }

                foreach (var argument in attribute.NamedArguments)
                {
                    if (!argument.IsField)
                    {
                        var value = argument.TypedValue;
                        WriteType(value.ArgumentType);
                        state.Writer.Write(argument.MemberName);
                        WriteValue(value.ArgumentType, value.Value);
                    }
                }
            }
        }

        private Action<PicklerSerializationState>? InvokePhase(PicklerSerializationState state, Func<PicklerSerializationState, Action<PicklerSerializationState>?>? phase2)
        {
            if (phase2 == null) return null;
            return phase2(state);
        }

        private void InvokePhase(PicklerSerializationState state, Action<PicklerSerializationState>? phase3)
        {
            if (phase3 != null)
            {
                phase3(state);
            }
        }

        private Action<PicklerSerializationState>? SerializeAssembly(PicklerSerializationState state, Assembly assembly, long? position = null)
        {
            if (Object.ReferenceEquals(assembly, null))
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (position == null)
            {
                if (ShouldMemo(assembly) && state.MaybeWriteMemo(assembly, (byte)AssemblyOperation.Memo))
                {
                    return null;
                }
                position = state.Writer.BaseStream.Position;
            }

            // This is an assembly, we need to emit an assembly name so it can be reloaded

            // Is this mscorlib? If so we write out a single token for it
            if (assembly == mscorlib)
            {
                state.Writer.Write((byte)AssemblyOperation.MscorlibAssembly);
                return null;
            }
            // Is this assembly one we should save by value?
            else if (PickleByValue(assembly))
            {
                // Write out an assembly definition, we'll build a dynamic assembly for this on the other side
                state.Writer.Write((byte)AssemblyOperation.AssemblyDef);
                state.Writer.Write(assembly.FullName);
                AddMemo(state, false, position.Value, assembly);
                return state =>
                {
                    WriteCustomAttributes(state, assembly.CustomAttributes.ToArray());
                };
            }
            else
            {
                // Just write out an assembly refernce
                state.Writer.Write((byte)AssemblyOperation.AssemblyRef);
                state.Writer.Write(assembly.FullName);
                AddMemo(state, false, position.Value, assembly);
                return null;
            }
        }

        private Func<PicklerSerializationState, Action<PicklerSerializationState>>? SerializeModule(PicklerSerializationState state, Module module, long? position = null)
        {
            if (Object.ReferenceEquals(module, null))
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (position == null)
            {
                if (ShouldMemo(module) && state.MaybeWriteMemo(module, (byte)ModuleOperation.Memo))
                {
                    return null;
                }
                position = state.Writer.BaseStream.Position;
            }

            // This is a module, we need to emit a reference to the assembly it's found in and it's name

            // Is this assembly one we should save by value?
            if (PickleByValue(module.Assembly))
            {
                return SerializeModuleDef(state, module, position.Value);
            }
            else
            {
                if (module == mscorlib.ManifestModule)
                {
                    state.Writer.Write((byte)ModuleOperation.MscorlibModule);
                    return null;
                }
                // We can just write a ref here, lets check if this is the ONLY module on the assembly (i.e. the ManifestModule)
                // because we can then write out a token instead of a name
                else if (module == module.Assembly.ManifestModule)
                {
                    state.Writer.Write((byte)ModuleOperation.ManifestModuleRef);
                }
                else
                {
                    state.Writer.Write((byte)ModuleOperation.ModuleRef);
                    state.Writer.Write(module.Name);
                }
                var assemblyPhase3 = SerializeAssembly(state, module.Assembly);
                System.Diagnostics.Debug.Assert(assemblyPhase3 == null, "Expected assembly phase 3 to be null");
                AddMemo(state, false, position.Value, module);
                return null;
            }
        }

        private void SerializeType(PicklerSerializationState state, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters, long? position = null)
        {
            if (Object.ReferenceEquals(type, null))
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (position == null)
            {
                if (ShouldMemo(type) && state.MaybeWriteMemo(type, (byte)TypeOperation.Memo))
                {
                    return;
                }
                position = state.Writer.BaseStream.Position;
            }

            // This is a type, we need to emit a TypeRef or Def so it can be reconstructed

            // Constructed generic types are always handled the same way, we write out a GenericDef, the unconstructed generic type and then the generic arguments
            if (type.IsConstructedGenericType)
            {
                state.Writer.Write((byte)TypeOperation.GenericInstantiation);
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                SerializeType(state, genericTypeDefinition, genericTypeParameters, genericMethodParameters);
                state.Writer.Write7BitEncodedInt(type.GenericTypeArguments.Length);
                foreach (var arg in type.GenericTypeArguments)
                {
                    SerializeType(state, arg, genericTypeParameters, genericMethodParameters);
                }
                AddMemo(state, false, position.Value, type);
            }

            // Arrays aren't simple generic types, we need to write out the rank and element type
            else if (type.IsArray)
            {
                // This is wrong but we'll fix it as part of removing MemoCallbacks
                AddMemo(state, false, position.Value, type);

                state.Writer.Write((byte)TypeOperation.ArrayType);
                if (type.IsSZArray)
                {
                    state.Writer.Write((byte)0);
                }
                else
                {
                    var rank = type.GetArrayRank();
                    if (rank > byte.MaxValue)
                    {
                        // Who has 256 rank arrays!? The runtime specification says this can be at most 32.
                        throw new NotImplementedException($"Pikala does not support arrays of rank higher than {byte.MaxValue}, got {rank}");
                    }
                    state.Writer.Write((byte)rank);
                }
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for an array type");
                SerializeType(state, elementType, genericTypeParameters, genericMethodParameters);
            }

            else if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod != null)
                {
                    if (genericMethodParameters == null)
                    {
                        state.Writer.Write((byte)TypeOperation.GenericMethodParameter);
                        state.Writer.Write7BitEncodedInt(type.GenericParameterPosition);
                        // Generic constructors aren't supported so this must be a MethodInfo.
                        var methodInfo = (MethodInfo)type.DeclaringMethod;
                        SerializeMethodInfo(state, methodInfo);
                    }
                    else
                    {
                        state.Writer.Write((byte)TypeOperation.MVar);
                        state.Writer.Write7BitEncodedInt(type.GenericParameterPosition);
                    }
                }
                else if (type.DeclaringType != null)
                {
                    if (genericTypeParameters == null)
                    {
                        state.Writer.Write((byte)TypeOperation.GenericTypeParameter);
                        state.Writer.Write7BitEncodedInt(type.GenericParameterPosition);
                        SerializeType(state, type.DeclaringType, genericTypeParameters, genericMethodParameters);
                    }
                    else
                    {
                        state.Writer.Write((byte)TypeOperation.TVar);
                        state.Writer.Write7BitEncodedInt(type.GenericParameterPosition);
                    }
                }
                else
                {
                    throw new Exception($"'{type}' is a generic parameter but is not bound to a type or method");
                }
                AddMemo(state, false, position.Value, type);
            }

            // Is this assembly one we should save by value?
            else if (PickleByValue(type.Assembly))
            {
                state.RunWithTrailers(() =>
                {
                    // This is an unreferanceable assembly so on the other side it will be a dynamic assembly and we need to rebuild types
                    state.Writer.Write((byte)TypeOperation.TypeDef);

                    // This is wrong but we'll move it as part of removing MemoCallbacks
                    AddMemo(state, false, position.Value, type);

                    if (type.DeclaringType != null)
                    {
                        state.Writer.Write(type.Name);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(type.Namespace))
                        {
                            state.Writer.Write(type.Name);
                        }
                        else
                        {
                            state.Writer.Write(type.Namespace + "." + type.Name);
                        }
                    }

                    state.Writer.Write((int)type.Attributes);

                    var typeFlags = type.DeclaringType != null ? TypeDef.Nested : (TypeDef)0;

                    if (type.IsEnum)
                    {
                        typeFlags |= TypeDef.Enum;
                    }
                    else if (type.IsAssignableTo(typeof(Delegate)))
                    {
                        typeFlags |= TypeDef.Delegate;
                    }
                    else if (type.IsValueType)
                    {
                        typeFlags |= TypeDef.Struct;
                    }
                    else
                    {
                        typeFlags |= TypeDef.Class;
                    }

                    state.Writer.Write((byte)typeFlags);

                    Type[]? genericParameters = null;
                    if (!type.IsEnum)
                    {
                        // Enums never have generic parameters so we don't even write out a count for them
                        genericParameters = type.GetGenericArguments();
                        state.Writer.Write7BitEncodedInt(genericParameters.Length);
                        foreach (var parameter in genericParameters)
                        {
                            state.Writer.Write(parameter.Name);
                        }
                    }

                    Func<PicklerSerializationState, Action<PicklerSerializationState>>? parentPhase2;
                    if (type.DeclaringType != null)
                    {
                        SerializeType(state, type.DeclaringType, genericTypeParameters, genericMethodParameters);
                        parentPhase2 = null;
                    }
                    else
                    {
                        parentPhase2 = SerializeModule(state, type.Module);
                    }

                    if (type.IsEnum)
                    {
                        // enums are nice and simple, just a TypeCode and some string primitive fields
                        var typeCode = Type.GetTypeCode(type);
                        state.Writer.Write((byte)typeCode);

                        var values = type.GetEnumValues();
                        var names = type.GetEnumNames();

                        state.Writer.Write7BitEncodedInt(values.Length);
                        for (int i = 0; i < values.Length; ++i)
                        {
                            state.Writer.Write(names[i]);
                            var value = values.GetValue(i);
                            System.Diagnostics.Debug.Assert(value != null);
                            WriteEnumerationValue(state.Writer, typeCode, value);
                        }

                        InvokePhase(state, InvokePhase(state, parentPhase2));

                        WriteCustomAttributes(state, type.CustomAttributes.ToArray());
                    }
                    else if (type.IsAssignableTo(typeof(Delegate)))
                    {
                        // delegates are a name, optionally generic parameters, a return type and parameter types
                        var invoke = type.GetMethod("Invoke");
                        System.Diagnostics.Debug.Assert(invoke != null, "GetMethod(\"Invoke\") unexpectedly returned null for a delegate type");

                        SerializeType(state, invoke.ReturnType, genericTypeParameters, genericMethodParameters);
                        var parameters = invoke.GetParameters();
                        state.Writer.Write7BitEncodedInt(parameters.Length);
                        foreach (var parameter in parameters)
                        {
                            state.Writer.WriteNullableString(parameter.Name);
                            SerializeType(state, parameter.ParameterType, genericTypeParameters, genericMethodParameters);
                        }

                        InvokePhase(state, InvokePhase(state, parentPhase2));
                    }
                    else
                    {
                        SerializeDef(state, type, genericParameters, parentPhase2);
                    }
                });
            }
            else
            {
                if (_wellKnownTypes.TryGetValue(type, out var op))
                {
                    state.Writer.Write((byte)op);
                    return;
                }

                // Just write out a refernce to the type
                state.Writer.Write((byte)TypeOperation.TypeRef);
                // Is nested so we know if we need to read a module or type reference
                state.Writer.Write(type.DeclaringType != null);

                if (type.DeclaringType != null)
                {
                    state.Writer.Write(type.Name);
                    SerializeType(state, type.DeclaringType, genericTypeParameters, genericMethodParameters);
                }
                else
                {
                    if (string.IsNullOrEmpty(type.Namespace))
                    {
                        state.Writer.Write(type.Name);
                    }
                    else
                    {
                        state.Writer.Write(type.Namespace + "." + type.Name);
                    }
                    var moduleTrailer = SerializeModule(state, type.Module);
                    System.Diagnostics.Debug.Assert(moduleTrailer == null, "Expected module trailer to be null");
                }

                AddMemo(state, false, position.Value, type);
            }
        }

        private void SerializeFieldInfo(PicklerSerializationState state, FieldInfo field, long? position = null)
        {
            if (Object.ReferenceEquals(field, null))
            {
                throw new ArgumentNullException(nameof(field));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(field, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            state.RunWithTrailers(() =>
            {
                state.Writer.Write(field.Name);
                SerializeType(state, field.ReflectedType, null, null);
                AddMemo(state, false, position.Value, field);
            });
        }

        private void SerializePropertyInfo(PicklerSerializationState state, PropertyInfo property, long? position = null)
        {
            if (Object.ReferenceEquals(property, null))
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(property, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            state.RunWithTrailers(() =>
            {
                SerializeSignature(state, Signature.GetSignature(property));
                SerializeType(state, property.ReflectedType, null, null);
                AddMemo(state, false, position.Value, property);
            });
        }

        private void SerializeEventInfo(PicklerSerializationState state, EventInfo evt, long? position = null)
        {
            if (Object.ReferenceEquals(evt, null))
            {
                throw new ArgumentNullException(nameof(evt));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(evt, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            state.RunWithTrailers(() =>
            {
                state.Writer.Write(evt.Name);
                SerializeType(state, evt.ReflectedType, null, null);
                AddMemo(state, false, position.Value, evt);
            });
        }

        private void SerializeMethodInfo(PicklerSerializationState state, MethodInfo methodInfo, long? position = null)
        {
            if (Object.ReferenceEquals(methodInfo, null))
            {
                throw new ArgumentNullException(nameof(methodInfo));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(methodInfo, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            state.RunWithTrailers(() =>
            {
                // This is wrong but we'll fix it as part of removing MemoCallbacks
                AddMemo(state, false, position.Value, methodInfo);

                if (methodInfo.IsConstructedGenericMethod)
                {
                    var genericArguments = methodInfo.GetGenericArguments();
                    SerializeSignature(state, Signature.GetSignature(methodInfo.GetGenericMethodDefinition()));
                    state.Writer.Write7BitEncodedInt(genericArguments.Length);
                    foreach (var generic in genericArguments)
                    {
                        SerializeType(state, generic, null, null);
                    }
                }
                else
                {
                    SerializeSignature(state, Signature.GetSignature(methodInfo));
                    state.Writer.Write7BitEncodedInt(0);
                }

                SerializeType(state, methodInfo.ReflectedType, null, null);
            });
        }

        private void SerializeConstructorInfo(PicklerSerializationState state, ConstructorInfo constructor, long? position = null)
        {
            if (Object.ReferenceEquals(constructor, null))
            {
                throw new ArgumentNullException(nameof(constructor));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(constructor, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            state.RunWithTrailers(() =>
            {
                // This is wrong but we'll fix it as part of removing MemoCallbacks
                AddMemo(state, false, position.Value, constructor);

                SerializeSignature(state, Signature.GetSignature(constructor));
                SerializeType(state, constructor.ReflectedType, null, null);
            });
        }

        private void SerializeMethodBase(PicklerSerializationState state, MethodBase methodBase, long? position = null)
        {
            if (Object.ReferenceEquals(methodBase, null))
            {
                throw new ArgumentNullException(nameof(methodBase));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(methodBase, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                position = state.Writer.BaseStream.Position;
            }

            if (methodBase is MethodInfo methodInfo)
            {
                SerializeType(state, typeof(MethodInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeMethodInfo(state, methodInfo, position);
            }
            else if (methodBase is ConstructorInfo constructorInfo)
            {
                SerializeType(state, typeof(ConstructorInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeConstructorInfo(state, constructorInfo, position);
            }
            else
            {
                throw new Exception($"Unexpected type '{methodBase.GetType()}' for MethodBase");
            }
        }

        private void SerializeMemberInfo(PicklerSerializationState state, MemberInfo memberInfo, long? position = null)
        {
            if (Object.ReferenceEquals(memberInfo, null))
            {
                throw new ArgumentNullException(nameof(memberInfo));
            }

            if (position == null)
            {
                if (state.MaybeWriteMemo(memberInfo, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

            if (memberInfo is MethodInfo methodInfo)
            {
                SerializeType(state, typeof(MethodInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeMethodInfo(state, methodInfo, position);
            }
            else if (memberInfo is ConstructorInfo constructorInfo)
            {
                SerializeType(state, typeof(ConstructorInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeConstructorInfo(state, constructorInfo, position);
            }
            else if (memberInfo is FieldInfo fieldInfo)
            {
                SerializeType(state, typeof(FieldInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeFieldInfo(state, fieldInfo, position);
            }
            else if (memberInfo is PropertyInfo propertyInfo)
            {
                SerializeType(state, typeof(PropertyInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializePropertyInfo(state, propertyInfo, position);
            }
            else if (memberInfo is EventInfo eventInfo)
            {
                SerializeType(state, typeof(EventInfo), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeEventInfo(state, eventInfo, position);
            }
            else if (memberInfo is Type type)
            {
                SerializeType(state, typeof(Type), null, null);
                position = state.Writer.BaseStream.Position;
                SerializeType(state, type, null, null, position);
            }
            else
            {
                throw new Exception($"Unexpected type '{memberInfo.GetType()}' for MemberInfo");
            }
        }

        private void SerializeArray(PicklerSerializationState state, Array obj, long position, Type objType)
        {
            System.Diagnostics.Debug.Assert(obj.GetType() == objType, "GetType did not match passed in Type");

            // This is an array, write the type then loop over each item.
            // Theres a performance optimisation we could do here with value types,
            // we we fetch the handler only once.

            var elementType = objType.GetElementType();
            System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for an array type");

            // Special case szarray (i.e. Rank 1, lower bound 0)
            if (objType.IsSZArray)
            {
                state.Writer.Write7BitEncodedInt(obj.Length);
            }
            else
            {
                // This might just be rank 1 but with non-normal bounds
                for (int dimension = 0; dimension < obj.Rank; ++dimension)
                {
                    state.Writer.Write7BitEncodedInt(obj.GetLength(dimension));
                    state.Writer.Write7BitEncodedInt(obj.GetLowerBound(dimension));
                }
            }

            AddMemo(state, false, position, obj);

            // If this is a primitive type just block copy it across to the stream
            if (elementType.IsPrimitive)
            {
                var arrayHandle = System.Runtime.InteropServices.GCHandle.Alloc(obj, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    // TODO We should just use Unsafe.SizeOf here but that's a net5.0 addition
                    long byteCount;
                    if (elementType == typeof(bool))
                    {
                        byteCount = obj.LongLength;
                    }
                    else if (elementType == typeof(char))
                    {
                        byteCount = 2 * obj.LongLength;
                    }
                    else
                    {
                        byteCount = System.Runtime.InteropServices.Marshal.SizeOf(elementType) * obj.LongLength;
                    }

                    unsafe
                    {
                        var pin = (byte*)arrayHandle.AddrOfPinnedObject().ToPointer();
                        while (byteCount > 0)
                        {
                            // Write upto 4k at a time
                            var length = (int)Math.Min(byteCount, 4096);

                            var span = new ReadOnlySpan<byte>(pin, length);
                            state.Writer.Write(span);

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
                foreach (var item in obj)
                {
                    // TODO If we know all items are the same type we can save calling MakeInfo on each one
                    Serialize(state, item, elementType);
                }
            }
        }

        private void SerializeMulticastDelegate(PicklerSerializationState state, long position, MulticastDelegate multicastDelegate)
        {
            // Delegates are just a target and a method
            var invocationList = multicastDelegate.GetInvocationList();
            state.Writer.Write7BitEncodedInt(invocationList.Length);

            // We need to memoise delegates correctly, if the invocation list has a single element we write out the target and method,
            // but if the invocation list has multiple elements we need to recurse them through Serialize so they can be memoised correctly
            if (invocationList.Length == 1)
            {
                Serialize(state, invocationList[0].Target, typeof(object));
                // Serialise the sub-objects may recurse and serialise this delegate so early out if that's the case
                if (state.MaybeWriteMemo(multicastDelegate, null)) return;
                state.Writer.Write15BitEncodedLong(0);

                Serialize(state, invocationList[0].Method, typeof(MethodInfo));
                if (state.MaybeWriteMemo(multicastDelegate, null)) return;
                state.Writer.Write15BitEncodedLong(0);
            }
            else
            {
                foreach (var invocation in invocationList)
                {
                    Serialize(state, invocation, typeof(Delegate));
                    if (state.MaybeWriteMemo(multicastDelegate, null)) return;
                    state.Writer.Write15BitEncodedLong(0);
                }
            }
            AddMemo(state, false, position, multicastDelegate);
        }

        private void SerializeTuple(PicklerSerializationState state, bool isValueType, long position, System.Runtime.CompilerServices.ITuple tuple, Type[] genericArguments)
        {
            System.Diagnostics.Debug.Assert(genericArguments.Length == tuple.Length, "genericArguments length did not match tuple length");

            // Write out the values
            for (int i = 0; i < tuple.Length; ++i)
            {
                var item = tuple[i];
                Serialize(state, item, genericArguments[i]);
                // If this is a reference to a tuple (i.e. Tuple, or boxed ValueTuple) then serialising the fields may serialise the tuple itself.
                if (!isValueType)
                {
                    if (state.MaybeWriteMemo(tuple, null)) return;
                    state.Writer.Write15BitEncodedLong(0);
                }
            }
            AddMemo(state, isValueType, position, tuple);
        }

        private void SerializeReducer(PicklerSerializationState state, object obj, IReducer reducer, Type runtimeType)
        {
            // We've got a reducer for the type (or its generic variant)
            var (method, target, args) = reducer.Reduce(runtimeType, obj);

            SerializeMethodBase(state, method);

            // Assert properties of the reduction
            if (method is ConstructorInfo constructorInfo)
            {
                if (target != null)
                {
                    throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was a ConstructorInfo but Target was not null.");
                }

                if (constructorInfo.DeclaringType != runtimeType)
                {
                    throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was a ConstructorInfo for '{constructorInfo.DeclaringType}'.");
                }

                // We don't write target for ConstructorInfo, it must be null.
            }
            else if (method is MethodInfo methodInfo)
            {
                if (methodInfo.ReturnType != runtimeType)
                {
                    throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was a MethodInfo that returns '{methodInfo.ReturnType}'.");
                }

                Serialize(state, target, typeof(object));
            }
            else
            {
                throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was '{method}'.");
            }

            state.Writer.Write7BitEncodedInt(args.Length);
            foreach (var arg in args)
            {
                Serialize(state, arg, typeof(object));
            }
        }

        private void SerializeObject(PicklerSerializationState state, object obj, (SerialisedObjectTypeInfo, FieldInfo)[] fields)
        {
            // Must be an object, try and dump all it's fields

            foreach (var (fieldType, field) in fields)
            {
                System.Diagnostics.Debug.Assert(fieldType.Type == field.FieldType, "FieldType didn't match");

                var value = field.GetValue(obj);
                Serialize(state, value, field.FieldType);
            }
        }

        private void BuildSerialisedObjectTypeInfo(SerialisedObjectTypeInfo info, Func<Type, SerialisedObjectTypeInfo> recurse)
        {
            var type = info.Type;

            info.Flags =
                (type.IsValueType ? PickledTypeFlags.IsValueType : 0) |
                (type.IsSealed ? PickledTypeFlags.IsSealed : 0) |
                (type.IsAbstract ? PickledTypeFlags.IsAbstract : 0) |
                (type.HasElementType ? PickledTypeFlags.HasElementType : 0);

            // Assume builtin, we'll type check and change that below.
            info.Mode = PickledTypeMode.IsBuiltin;

            if (!type.IsAbstract)
            {
                // Work out what sort of operation this type needs
                if (type.IsPointer || type == typeof(Pointer))
                {
                    info.Error = $"Pointer types are not serializable: '{type}'";
                }

                else if (type.IsArray)
                {
                    info.Element = recurse(type.GetElementType());
                }

                else if (IsNullableType(type, out var nullableElement))
                {
                    info.Element = recurse(nullableElement);
                }

                // Reflection
                else if (type.IsAssignableTo(typeof(Assembly)))
                {
                }
                else if (type.IsAssignableTo(typeof(Module)))
                {
                }
                else if (type.IsAssignableTo(typeof(MemberInfo)))
                {

                }
                // End of reflection handlers

                // Tuples!
                else if (IsTupleType(type))
                {
                    info.TupleArguments = type.GetGenericArguments().Select(recurse).ToArray();
                }

                else if (IsBuiltinType(type))
                {
                    // Builtin do nothing
                }

                else if (type.IsEnum)
                {
                    info.Mode = PickledTypeMode.IsEnum;
                    info.TypeCode = Type.GetTypeCode(type);
                }

                else if (type.IsAssignableTo(typeof(MulticastDelegate)))
                {
                    info.Mode = PickledTypeMode.IsDelegate;
                }

                else if (_reducers.TryGetValue(type, out var reducer) || (type.IsGenericType && _reducers.TryGetValue(type.GetGenericTypeDefinition(), out reducer)))
                {
                    info.Reducer = reducer;
                    info.Mode = PickledTypeMode.IsReduced;
                }

                else if (type.IsAssignableTo(typeof(MarshalByRefObject)))
                {
                    info.Error = $"Type '{type}' is not automaticly serializable as it inherits from MarshalByRefObject.";
                }

                else
                {
                    var fields = GetSerializedFields(type);
                    info.Mode = PickledTypeMode.IsAutoSerialisedObject;

                    info.SerialisedFields = new (SerialisedObjectTypeInfo, FieldInfo)[fields.Length];
                    for (int i = 0; i < fields.Length; ++i)
                    {
                        info.SerialisedFields[i] = (recurse(fields[i].FieldType), fields[i]);
                    }
                }
            }
        }

        private void WriteSerialisedObjectTypeInfo(PicklerSerializationState state, SerialisedObjectTypeInfo info)
        {
            if (!IsBuiltinType(info.Type))
            {
                var flags = (int)info.Flags;
                var mode = (int)info.Mode;

                System.Diagnostics.Debug.Assert(flags < (1 << 4), "Expected flags to fit in 4 bits");
                System.Diagnostics.Debug.Assert(mode < (1 << 4), "Expected mode to fit in 4 bits");

                state.Writer.Write((byte)(mode << 4 | flags));
            }

            if (info.Mode == PickledTypeMode.IsEnum)
            {
                // If it's an enum write out the typecode, we need to ensure we read back the same type code size
                state.Writer.Write((byte)info.TypeCode);
            }
            if (info.Mode == PickledTypeMode.IsAutoSerialisedObject)
            {
                System.Diagnostics.Debug.Assert(info.SerialisedFields != null, "Mode was IsAutoSerialisedObject but SerialisedFields was null");

                var fields = info.SerialisedFields;
                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var (fieldType, fieldInfo) in fields)
                {
                    state.Writer.Write(fieldInfo.Name);
                    SerializeType(state, fieldType.Type, null, null);
                    MaybeWriteTypeInfo(state, fieldType);
                }
            }

            // Array, Nullable and Tuple need their subtypes written out
            if (info.Element != null)
            {
                MaybeWriteTypeInfo(state, info.Element);
            }
            else if (info.TupleArguments != null)
            {
                foreach (var subinfo in info.TupleArguments)
                {
                    MaybeWriteTypeInfo(state, subinfo);
                }
            }
        }
        private SerialisedObjectTypeInfo GetCachedTypeInfo(Type type)
        {
            if (!_typeInfo.TryGetValue(type, out var maybeInfo))
            {
                maybeInfo = new SerialisedObjectTypeInfo(type);
                _typeInfo.Add(type, maybeInfo);
                BuildSerialisedObjectTypeInfo(maybeInfo, GetCachedTypeInfo);
            }
            return maybeInfo;
        }

        private void MaybeWriteTypeInfo(PicklerSerializationState state, SerialisedObjectTypeInfo info)
        {
            if (!state.SeenTypes.Contains(info.Type))
            {
                state.SeenTypes.Add(info.Type);
                WriteSerialisedObjectTypeInfo(state, info);
            }
        }

        private void Serialize(PicklerSerializationState state, object? obj, Type staticType)
        {
            // Early out for types we can't possibly deal with
            if (staticType.IsPointer)
            {
                throw new Exception($"Pointer types are not serializable: '{staticType}'");
            }

            {
                // In a block because nothing else should refer to sanatizedStaticType, if we get past this block it's equal to staticType.
                var sanatizedStaticType = SanatizeType(staticType);
                // Check that we don't have a static type for a derived reflection type. 
                // For example if staticType is RuntimeAssembly we might need to emit an AssemblyBuilder which wouldn't match
                if (sanatizedStaticType != staticType)
                {
                    throw new Exception($"Pikala can not serialise type {staticType}, try {sanatizedStaticType}");
                }
            }

            var typeInfo = GetCachedTypeInfo(staticType);
            MaybeWriteTypeInfo(state, typeInfo);

            if (IsNullableType(staticType, out var nullableInnerType))
            {
                // Nullable<T> always writes the same way
                if (Object.ReferenceEquals(obj, null))
                {
                    state.Writer.Write(false);
                    return;
                }

                state.Writer.Write(true);
                Serialize(state, obj, nullableInnerType);
                return;
            }

            // If this is a null it doesn't have a runtime type, and if it's memo'd then well we don't care because we just memo'd it. But else we'll be picking how 
            // to deserialise it based on its type. However often the static type will be sufficent to also tell us the runtime type.
            Type runtimeType;
            if (!staticType.IsValueType)
            {
                if (Object.ReferenceEquals(obj, null))
                {
                    state.Writer.Write((byte)ObjectOperation.Null);
                    return;
                }

                if (state.MaybeWriteMemo(obj, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);

                // If the static type is a reflection type or sealed then we don't need to write out the runtime type
                // All arrays are sealed but what actually matters for arrays is if the element type is sealed.
                // e.g. object[] is sealed but we still need to write out the runtime type for, while string[] is 
                // also sealed but so is string so we don't need to write the runtime type out for it. Likewise
                // for int[]. This also applies to arrays of reflection objects, we don't need a type definition for 
                // Type[].

                var rootElementType = GetRootElementType(typeInfo);

                if (reflectionTypes.Contains(rootElementType.Type))
                {
                    // Need to check that reflection types are the runtime reflection types, SanatizeType will throw if this is a non-runtime type.
                    runtimeType = SanatizeType(obj.GetType());
                }
                else
                {
                    var isSealed = rootElementType.Flags.HasFlag(PickledTypeFlags.IsSealed) || rootElementType.Flags.HasFlag(PickledTypeFlags.IsValueType);

                    if (isSealed)
                    {
                        System.Diagnostics.Debug.Assert(staticType == obj.GetType(), "Elided runtime type but it didn't match the static type");

                        runtimeType = staticType;
                    }
                    else
                    {
                        runtimeType = SanatizeType(obj.GetType());

                        SerializeType(state, runtimeType, null, null);

                        // This will be a no-op for most well known types but also sealed types which we will of written out for the static value
                        typeInfo = GetCachedTypeInfo(runtimeType);
                        MaybeWriteTypeInfo(state, typeInfo);
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.Assert(obj != null, "Static type was a ValueType but obj was null");
                System.Diagnostics.Debug.Assert(staticType == obj.GetType(), "Static type was a ValueType but didn't match runtime type");
                runtimeType = staticType;
            }

            System.Diagnostics.Debug.Assert(obj != null, "Object was unexpectedly null");

            var position = state.Writer.BaseStream.Position;

            if (typeInfo.Error != null)
            {
                throw new Exception(typeInfo.Error);
            }

            if (runtimeType.IsEnum)
            {
                // typeCode for an enum will be something like Int32
                WriteEnumerationValue(state.Writer, Type.GetTypeCode(runtimeType), obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }

            else if (obj is Array arr)
            {
                SerializeArray(state, arr, position, runtimeType);
                return;
            }

            else if (obj is FieldInfo fieldInfo)
            {
                SerializeFieldInfo(state, fieldInfo, position);
                return;
            }
            else if (obj is PropertyInfo propertyInfo)
            {
                SerializePropertyInfo(state, propertyInfo, position);
                return;
            }
            else if (obj is EventInfo eventInfo)
            {
                SerializeEventInfo(state, eventInfo, position);
                return;
            }
            else if (obj is MethodInfo methodInfo)
            {
                SerializeMethodInfo(state, methodInfo, position);
                return;
            }
            else if (obj is ConstructorInfo constructorInfo)
            {
                SerializeConstructorInfo(state, constructorInfo, position);
                return;
            }

            else if (obj is MulticastDelegate multicastDelegate)
            {
                SerializeMulticastDelegate(state, position, multicastDelegate);
                return;
            }

            else if (IsTupleType(runtimeType))
            {
                // N.B This isn't for any ITuple there might be user defined types that inherit from Tuple and it's not safe to pass them in here.
                SerializeTuple(state, staticType.IsValueType, position, (System.Runtime.CompilerServices.ITuple)obj, runtimeType.GetGenericArguments());
                return;
            }

            if (runtimeType == typeof(bool))
            {
                state.Writer.Write((bool)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(char))
            {
                state.Writer.Write((char)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(byte))
            {
                state.Writer.Write((byte)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(ushort))
            {
                state.Writer.Write((ushort)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(uint))
            {
                state.Writer.Write((uint)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(ulong))
            {
                state.Writer.Write((ulong)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(sbyte))
            {
                state.Writer.Write((sbyte)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(short))
            {
                state.Writer.Write((short)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(int))
            {
                state.Writer.Write((int)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(long))
            {
                state.Writer.Write((long)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(float))
            {
                state.Writer.Write((float)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(double))
            {
                state.Writer.Write((double)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(decimal))
            {
                state.Writer.Write((decimal)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(UIntPtr))
            {
                state.Writer.Write(((UIntPtr)obj).ToUInt64());
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(IntPtr))
            {
                state.Writer.Write(((IntPtr)obj).ToInt64());
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }
            else if (runtimeType == typeof(DBNull))
            {
                return;
            }
            else if (runtimeType == typeof(string))
            {
                state.Writer.Write((string)obj);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }

            else if (obj is Assembly assembly)
            {
                var phase3 = SerializeAssembly(state, assembly, position);
                InvokePhase(state, phase3);
                return;
            }
            else if (obj is Module module)
            {
                var phase2 = SerializeModule(state, module, position);
                InvokePhase(state, InvokePhase(state, phase2));
                return;
            }
            else if (obj is Type type)
            {
                SerializeType(state, type, null, null, position);
                return;
            }

            else if (typeInfo.Reducer != null)
            {
                SerializeReducer(state, obj, typeInfo.Reducer, runtimeType);
                AddMemo(state, staticType.IsValueType, position, obj);
                return;
            }

            System.Diagnostics.Debug.Assert(typeInfo.SerialisedFields != null);
            AddMemo(state, staticType.IsValueType, position, obj);
            SerializeObject(state, obj, typeInfo.SerialisedFields);
            return;
        }

        public void Serialize(Stream stream, object? rootObject)
        {
            var state = new PicklerSerializationState(stream);

            // Always start the pickler stream with a header for sanity checking inputs
            state.Writer.Write(_header);
            state.Writer.Write7BitEncodedInt(_pikalaVersion.Major);
            state.Writer.Write7BitEncodedInt(_pikalaVersion.Minor);
            // We're writing out the runtime version here, mostly just for debugging
            state.Writer.Write7BitEncodedInt(Environment.Version.Major);
            state.Writer.Write7BitEncodedInt(Environment.Version.Minor);

            Serialize(state, rootObject, typeof(object));
            state.CheckTrailers();
        }
    }
}
