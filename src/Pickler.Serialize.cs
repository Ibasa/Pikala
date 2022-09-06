using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;

namespace Ibasa.Pikala
{
    public sealed partial class Pickler
    {
        /// <summary>
        /// There are some objects that we shouldn't bother to memoise because it's cheaper to just write their tokens.
        /// </summary>
        private static bool ShouldMemo(object? obj)
        {
            // Don't bother memoing the well known types, they only take a byte to write out anyway
            foreach (var type in _wellKnownTypes)
            {
                if (Object.ReferenceEquals(type.Key, obj)) return false;
            }

            // mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib)) { return false; }

            // The manifest module for mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib.ManifestModule)) { return false; }

            return true;
        }

        private static void AddMemo(PicklerSerializationState state, object? obj)
        {
            if (obj != null)
            {
                System.Diagnostics.Debug.Assert(ShouldMemo(obj), "Tried to call AddMemo for an object that shouldn't be memoised");
                state.AddMemo(obj);
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
            else
            {
                throw new NotImplementedException($"Unhandled SignatureElement: {signature}");
            }
        }

        private void SerializeSignatureLocation(PicklerSerializationState state, SignatureLocation location, bool withModifiers)
        {
            SerializeSignatureElement(state, location.Element);

            if (withModifiers)
            {
                // Combine the count of required and optional parameters and write that out
                var reqmods = location.RequiredCustomModifiers;
                var optmods = location.OptionalCustomModifiers;

                if (reqmods.Count > 15) { throw new NotSupportedException("Pikala does not support more than 15 required modifiers"); }
                if (optmods.Count > 15) { throw new NotSupportedException("Pikala does not support more than 15 optional modifiers"); }

                var interleave = (reqmods.Count << 4) | optmods.Count;

                state.Writer.Write((byte)interleave);
                foreach (var reqmod in reqmods)
                {
                    SerializeType(state, reqmod, null, null);
                }
                foreach (var optmod in optmods)
                {
                    SerializeType(state, optmod, null, null);
                }
            }
        }

        private void SerializeSignature(PicklerSerializationState state, Signature signature)
        {
            state.Writer.Write(signature.Name);
            state.Writer.Write((byte)signature.CallingConvention);
            state.Writer.Write7BitEncodedInt(signature.GenericParameterCount);
            SerializeSignatureLocation(state, signature.ReturnType, true);

            bool withModifiers = false;
            foreach (var parameter in signature.Parameters)
            {
                if (parameter.RequiredCustomModifiers.Count > 0 || parameter.OptionalCustomModifiers.Count > 0)
                {
                    withModifiers = true;
                    break;
                }
            }
            state.Writer.Write7BitEncodedInt((signature.Parameters.Length << 1) | (withModifiers ? 1 : 0));

            foreach (var param in signature.Parameters)
            {
                SerializeSignatureLocation(state, param, withModifiers);
            }
        }

        private void SerializeParameter(PicklerSerializationState state, ParameterInfo parameter, bool withModifiers, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            SerializeType(state, parameter.ParameterType, genericTypeParameters, genericMethodParameters);

            // Combine the count of required and optional parameters and write that out
            var reqmods = parameter.GetRequiredCustomModifiers();
            var optmods = parameter.GetOptionalCustomModifiers();

            if (withModifiers)
            {
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
            else
            {
                if (reqmods.Length != 0)
                {
                    throw new Exception("Required custom modifiers was non-zero but withModifiers was false");
                }
                if (optmods.Length != 0)
                {
                    throw new Exception("Required custom modifiers was non-zero but withModifiers was false");
                }
            }
        }

        private void SerializeMethodBaseHeader(PicklerSerializationState state, Type[]? genericTypeParameters, MethodBase method)
        {
            var methodInfo = method as MethodInfo;
            if (methodInfo != null)
            {
                // Can't define constructors names or implementation flags so don't both writing them out.
                state.Writer.Write(method.Name);
                state.Writer.Write((int)method.MethodImplementationFlags);
            }

            state.Writer.Write((int)method.Attributes);
            state.Writer.Write((byte)method.CallingConvention);

            Type[]? genericMethodParameters = null;
            if (methodInfo != null)
            {
                genericMethodParameters = method.GetGenericArguments();
                state.Writer.Write7BitEncodedInt(genericMethodParameters.Length);
                foreach (var parameter in genericMethodParameters)
                {
                    state.Writer.Write(parameter.Name);
                }

                var returnParameter = methodInfo.ReturnParameter;
                SerializeParameter(state, returnParameter, true, genericTypeParameters, genericMethodParameters);
            }

            var methodParameters = method.GetParameters();
            bool withModifiers = false;
            foreach (var parameter in methodParameters)
            {
                if (parameter.GetRequiredCustomModifiers().Length > 0 || parameter.GetOptionalCustomModifiers().Length > 0)
                {
                    withModifiers = true;
                    break;
                }
            }
            state.Writer.Write7BitEncodedInt((methodParameters.Length << 1) | (withModifiers ? 1 : 0));

            foreach (var parameter in methodParameters)
            {
                SerializeParameter(state, parameter, withModifiers, genericTypeParameters, genericMethodParameters);
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
        }

        private void SerializeMethodBody(PicklerSerializationState state, Type[]? genericTypeParameters, Module methodModule, Type[]? genericMethodParameters, MethodBody methodBody)
        {
            state.Writer.Write(methodBody.InitLocals);

            state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
            foreach (var local in methodBody.LocalVariables)
            {
                SerializeType(state, local.LocalType, genericTypeParameters, genericMethodParameters);
            }

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

        private void SerializeModuleDef(PicklerSerializationState state, Module module)
        {
            state.Writer.Write((byte)ModuleOperation.ModuleDef);
            state.Writer.Write(module.Name);
            SerializeAssembly(state, module.Assembly);
            AddMemo(state, module);

            var fields = module.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var methods = module.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            state.Stages.PushStage2(state =>
            {
                WriteCustomAttributesTypes(state, module.CustomAttributes.ToArray());

                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var field in fields)
                {
                    WriteCustomAttributesTypes(state, field.CustomAttributes.ToArray());
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
                state.Writer.Write7BitEncodedInt(methods.Length);
                foreach (var method in methods)
                {
                    WriteCustomAttributesTypes(state, method.CustomAttributes.ToArray());
                    SerializeMethodBaseHeader(state, null, method);
                }

                state.Stages.PushStage3(state =>
                {
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
                });
            });
        }

        private void SerializeDef(PicklerSerializationState state, Type type, Type[]? genericParameters)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var events = type.GetEvents(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            state.Stages.PushStage2(state =>
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

                WriteCustomAttributesTypes(state, type.CustomAttributes.ToArray());

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

                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var field in fields)
                {
                    WriteCustomAttributesTypes(state, field.CustomAttributes.ToArray());
                    state.Writer.Write(field.Name);
                    state.Writer.Write((int)field.Attributes);
                    SerializeType(state, field.FieldType, genericParameters, null);
                    if (field.Attributes.HasFlag(FieldAttributes.Literal))
                    {
                        WriteConstant(state, field.GetRawConstantValue(), field.FieldType);
                    }
                }
                state.Writer.Write7BitEncodedInt(constructors.Length);
                foreach (var constructor in constructors)
                {
                    WriteCustomAttributesTypes(state, constructor.CustomAttributes.ToArray());
                    SerializeMethodBaseHeader(state, genericParameters, constructor);
                }
                state.Writer.Write7BitEncodedInt(methods.Length);
                foreach (var method in methods)
                {
                    WriteCustomAttributesTypes(state, method.CustomAttributes.ToArray());
                    SerializeMethodBaseHeader(state, genericParameters, method);
                }
                state.Writer.Write7BitEncodedInt(properties.Length);
                foreach (var property in properties)
                {
                    WriteCustomAttributesTypes(state, property.CustomAttributes.ToArray());
                    state.Writer.Write(property.Name);
                    state.Writer.Write((int)property.Attributes);
                    SerializeSignature(state, Signature.GetSignature(property));

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
                state.Writer.Write7BitEncodedInt(events.Length);
                foreach (var evt in events)
                {
                    WriteCustomAttributesTypes(state, evt.CustomAttributes.ToArray());
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

                state.Stages.PushStage3(state =>
                {
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

                    state.Stages.PushStage4(state =>
                    {
                        var staticFields =
                            type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(fi => fi.Name);
                        foreach (var field in staticFields)
                        {
                            if (!field.IsLiteral && !field.IsInitOnly)
                            {
                                // It's ok to just have names here because we're only looking at fields one exactly one type, no base type fields. So there will not be any name conflicts.
                                state.Writer.Write(field.Name);
                                var value = field.GetValue(null);

                                // This will be a no-op for most well known types but also sealed types which we will of written out for the static value
                                var typeInfo = GetCachedTypeInfo(field.FieldType);
                                MaybeWriteTypeInfo(state, typeInfo);

                                InvokeSerializationMethod(typeInfo, state, value, null, false);
                            }
                        }
                    });
                });
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
                else if (IsArrayType(type, out var elementType))
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SZArray);
                    WriteType(elementType);
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
                SerializeType(state, attribute.AttributeType, null, null);
                SerializeSignature(state, Signature.GetSignature(attribute.Constructor));

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

        private void WriteCustomAttributesTypes(PicklerSerializationState state, CustomAttributeData[] attributes)
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
                else if (IsArrayType(type, out var elementType))
                {
                    state.Writer.Write((byte)System.Reflection.Metadata.SerializationTypeCode.SZArray);
                    WriteType(elementType);
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
                }
                else if (type == typeof(string))
                {
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
                SerializeType(state, attribute.AttributeType, null, null);
                SerializeSignature(state, Signature.GetSignature(attribute.Constructor));

                state.Writer.Write7BitEncodedInt(attribute.ConstructorArguments.Count);
                foreach (var argument in attribute.ConstructorArguments)
                {
                    WriteType(argument.ArgumentType);
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
                        WriteValue(value.ArgumentType, value.Value);
                    }
                }

                foreach (var argument in attribute.NamedArguments)
                {
                    if (!argument.IsField)
                    {
                        var value = argument.TypedValue;
                        WriteType(value.ArgumentType);
                        WriteValue(value.ArgumentType, value.Value);
                    }
                }
            }
        }

        private void SerializeAssembly(PicklerSerializationState state, Assembly assembly, bool memo = true)
        {
            if (Object.ReferenceEquals(assembly, null))
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (memo)
            {
                if (ShouldMemo(assembly) && state.MaybeWriteMemo(assembly, (byte)AssemblyOperation.Memo))
                {
                    return;
                }
            }

            // This is an assembly, we need to emit an assembly name so it can be reloaded

            // Is this mscorlib? If so we write out a single token for it
            if (assembly == mscorlib)
            {
                state.Writer.Write((byte)AssemblyOperation.MscorlibAssembly);
            }
            // Is this assembly one we should save by value?
            else if (PickleByValue(assembly))
            {
                // Write out an assembly definition, we'll build a dynamic assembly for this on the other side
                state.Writer.Write((byte)AssemblyOperation.AssemblyDef);
                state.Writer.Write(assembly.FullName);

                state.Stages.PushStage2(state =>
                {
                    WriteCustomAttributesTypes(state, assembly.CustomAttributes.ToArray());

                    state.Stages.PushStage3(state =>
                    {
                        WriteCustomAttributes(state, assembly.CustomAttributes.ToArray());
                    });
                });
                AddMemo(state, assembly);
            }
            else
            {
                // Just write out an assembly refernce
                state.Writer.Write((byte)AssemblyOperation.AssemblyRef);
                state.Writer.Write(assembly.FullName);
                AddMemo(state, assembly);
            }
        }

        private void SerializeModule(PicklerSerializationState state, Module module, bool memo = true)
        {
            if (Object.ReferenceEquals(module, null))
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (memo)
            {
                if (ShouldMemo(module) && state.MaybeWriteMemo(module, (byte)ModuleOperation.Memo))
                {
                    return;
                }
            }

            // This is a module, we need to emit a reference to the assembly it's found in and it's name

            // Is this assembly one we should save by value?
            if (PickleByValue(module.Assembly))
            {
                SerializeModuleDef(state, module);
            }
            else
            {
                if (module == mscorlib.ManifestModule)
                {
                    state.Writer.Write((byte)ModuleOperation.MscorlibModule);
                    // No need to memoize or write the assembly out, just early return
                    return;
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

                SerializeAssembly(state, module.Assembly);
                AddMemo(state, module);
            }
        }

        private void SerializeType(PicklerSerializationState state, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters, bool memo = true)
        {
            if (Object.ReferenceEquals(type, null))
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (memo)
            {
                if (ShouldMemo(type) && state.MaybeWriteMemo(type, (byte)TypeOperation.Memo))
                {
                    return;
                }
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
                AddMemo(state, type);
            }

            // Arrays aren't simple generic types, we need to write out the rank and element type
            else if (IsArrayType(type, out var arrayElementType))
            {
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
                SerializeType(state, arrayElementType, genericTypeParameters, genericMethodParameters);
                AddMemo(state, type);
            }

            else if (type.IsByRef)
            {
                state.Writer.Write((byte)TypeOperation.ByRefType);
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for a byref type");
                SerializeType(state, elementType, genericTypeParameters, genericMethodParameters);
                AddMemo(state, type);
            }

            else if (type.IsPointer)
            {
                state.Writer.Write((byte)TypeOperation.PointerType);
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for a pointer type");
                SerializeType(state, elementType, genericTypeParameters, genericMethodParameters);
                AddMemo(state, type);
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
                AddMemo(state, type);
            }

            // Is this assembly one we should save by value?
            else if (PickleByValue(type.Assembly))
            {
                // This is an unreferanceable assembly so on the other side it will be a dynamic assembly and we need to rebuild types
                state.Writer.Write((byte)TypeOperation.TypeDef);

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
                else if (type.IsInterface)
                {
                    typeFlags |= TypeDef.Interface;
                }
                else
                {
                    typeFlags |= TypeDef.Class;
                }

                state.Writer.Write((byte)typeFlags);

                if (!type.IsEnum)
                {
                    // Enums never have generic parameters so we don't even write out a count for them
                    genericTypeParameters = type.GetGenericArguments();
                    state.Writer.Write7BitEncodedInt(genericTypeParameters.Length);
                    foreach (var parameter in genericTypeParameters)
                    {
                        state.Writer.Write(parameter.Name);
                        state.Writer.Write((byte)parameter.GenericParameterAttributes);
                    }
                }

                if (type.DeclaringType != null)
                {
                    SerializeType(state, type.DeclaringType, genericTypeParameters, genericMethodParameters);
                }
                else
                {
                    SerializeModule(state, type.Module);
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

                    AddMemo(state, type);

                    state.Stages.PushStage2(state =>
                    {
                        WriteCustomAttributesTypes(state, type.CustomAttributes.ToArray());

                        state.Stages.PushStage3(state =>
                    {
                        WriteCustomAttributes(state, type.CustomAttributes.ToArray());
                    });
                    });
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

                    AddMemo(state, type);

                    state.Stages.PushStage2(state =>
                    {
                        WriteCustomAttributesTypes(state, type.CustomAttributes.ToArray());

                        state.Stages.PushStage3(state =>
                        {
                            WriteCustomAttributes(state, type.CustomAttributes.ToArray());
                        });
                    });
                }
                else
                {
                    AddMemo(state, type);
                    SerializeDef(state, type, genericTypeParameters);
                }
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
                    SerializeModule(state, type.Module);
                }

                AddMemo(state, type);
            }
        }

        private void SerializeFieldInfo(PicklerSerializationState state, FieldInfo field, bool memo = true)
        {
            if (Object.ReferenceEquals(field, null))
            {
                throw new ArgumentNullException(nameof(field));
            }

            if (memo)
            {
                if (state.MaybeWriteMemo(field, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

            state.Writer.Write(field.Name);
            // Fields can be on either modules or types, if its on a module we write a null type and then the module
            if (field.ReflectedType == null)
            {
                state.Writer.Write(false);
                SerializeModule(state, field.Module);
            }
            else
            {
                state.Writer.Write(true);
                SerializeType(state, field.ReflectedType, null, null);
            }
            state.Stages.PopStages(state, 2);
            AddMemo(state, field);
        }

        private void SerializePropertyInfo(PicklerSerializationState state, PropertyInfo property, bool memo = true)
        {
            if (Object.ReferenceEquals(property, null))
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (memo)
            {
                if (state.MaybeWriteMemo(property, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

            SerializeSignature(state, Signature.GetSignature(property));
            SerializeType(state, property.ReflectedType, null, null);
            state.Stages.PopStages(state, 2);
            AddMemo(state, property);
        }

        private void SerializeEventInfo(PicklerSerializationState state, EventInfo evt, bool memo = true)
        {
            if (Object.ReferenceEquals(evt, null))
            {
                throw new ArgumentNullException(nameof(evt));
            }

            if (memo)
            {
                if (state.MaybeWriteMemo(evt, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

            state.Writer.Write(evt.Name);
            SerializeType(state, evt.ReflectedType, null, null);
            state.Stages.PopStages(state, 2);
            AddMemo(state, evt);
        }

        private void SerializeMethodInfo(PicklerSerializationState state, MethodInfo methodInfo, bool memo = true)
        {
            if (Object.ReferenceEquals(methodInfo, null))
            {
                throw new ArgumentNullException(nameof(methodInfo));
            }

            if (memo)
            {
                if (state.MaybeWriteMemo(methodInfo, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

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

            // Methods can be on either modules or types
            if (methodInfo.ReflectedType == null)
            {
                state.Writer.Write(false);
                SerializeModule(state, methodInfo.Module);
            }
            else
            {
                state.Writer.Write(true);
                SerializeType(state, methodInfo.ReflectedType, null, null);
            }
            state.Stages.PopStages(state, 2);
            AddMemo(state, methodInfo);
        }

        private void SerializeConstructorInfo(PicklerSerializationState state, ConstructorInfo constructor, bool memo = true)
        {
            if (Object.ReferenceEquals(constructor, null))
            {
                throw new ArgumentNullException(nameof(constructor));
            }

            if (memo)
            {
                if (state.MaybeWriteMemo(constructor, (byte)ObjectOperation.Memo))
                {
                    return;
                }

                state.Writer.Write((byte)ObjectOperation.Object);
            }

            SerializeSignature(state, Signature.GetSignature(constructor));
            SerializeType(state, constructor.ReflectedType, null, null);
            state.Stages.PopStages(state, 2);
            AddMemo(state, constructor);
        }

        private void SerializeMethodBase(PicklerSerializationState state, MethodBase methodBase)
        {
            if (Object.ReferenceEquals(methodBase, null))
            {
                throw new ArgumentNullException(nameof(methodBase));
            }

            if (state.MaybeWriteMemo(methodBase, (byte)ObjectOperation.Memo))
            {
                return;
            }

            state.Writer.Write((byte)ObjectOperation.Object);

            if (methodBase is MethodInfo methodInfo)
            {
                SerializeType(state, typeof(MethodInfo), null, null);
                SerializeMethodInfo(state, methodInfo, false);
            }
            else if (methodBase is ConstructorInfo constructorInfo)
            {
                SerializeType(state, typeof(ConstructorInfo), null, null);
                SerializeConstructorInfo(state, constructorInfo, false);
            }
            else
            {
                throw new Exception($"Unexpected type '{methodBase.GetType()}' for MethodBase");
            }
        }

        private void SerializeMemberInfo(PicklerSerializationState state, MemberInfo memberInfo)
        {
            if (Object.ReferenceEquals(memberInfo, null))
            {
                throw new ArgumentNullException(nameof(memberInfo));
            }

            if (state.MaybeWriteMemo(memberInfo, (byte)ObjectOperation.Memo))
            {
                return;
            }

            state.Writer.Write((byte)ObjectOperation.Object);

            if (memberInfo is MethodInfo methodInfo)
            {
                SerializeType(state, typeof(MethodInfo), null, null);
                SerializeMethodInfo(state, methodInfo, false);
            }
            else if (memberInfo is ConstructorInfo constructorInfo)
            {
                SerializeType(state, typeof(ConstructorInfo), null, null);
                SerializeConstructorInfo(state, constructorInfo, false);
            }
            else if (memberInfo is FieldInfo fieldInfo)
            {
                SerializeType(state, typeof(FieldInfo), null, null);
                SerializeFieldInfo(state, fieldInfo, false);
            }
            else if (memberInfo is PropertyInfo propertyInfo)
            {
                SerializeType(state, typeof(PropertyInfo), null, null);
                SerializePropertyInfo(state, propertyInfo, false);
            }
            else if (memberInfo is EventInfo eventInfo)
            {
                SerializeType(state, typeof(EventInfo), null, null);
                SerializeEventInfo(state, eventInfo, false);
            }
            else if (memberInfo is Type type)
            {
                SerializeType(state, typeof(Type), null, null);
                SerializeType(state, type, null, null, false);
            }
            else
            {
                throw new Exception($"Unexpected type '{memberInfo.GetType()}' for MemberInfo");
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
                    SerializeFieldInfo(state, fieldInfo);
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

        private void MaybeWriteTypeInfo(PicklerSerializationState state, SerialisedObjectTypeInfo info)
        {
            if (!state.SeenTypes.Contains(info.Type))
            {
                state.SeenTypes.Add(info.Type);
                WriteSerialisedObjectTypeInfo(state, info);
            }
        }

        // This can't change once a type is loaded, so it's safe to cache across multiple Serialize methods.
        // TODO: This need to be parallel safe
        private Dictionary<Type, MethodInfo> _serializationMethods = new Dictionary<Type, MethodInfo>();

        private MethodInfo GetSerializationMethod(SerialisedObjectTypeInfo type)
        {
            if (_serializationMethods.TryGetValue(type.Type, out var method))
            {
                return method;
            }

            return BuildSerializationMethod(type);
        }

        private MethodInfo BuildSerializationMethod(SerialisedObjectTypeInfo type)
        {
            // Serialization methods are either (Pickler, PicklerSerializationState, T, bool) for reference types.
            // Where the bool parameter is true to say that the null/memo & type has already been checked.
            // Or (Pickler, PicklerSerializationState, T, object?) for value types.
            // Where the object? parameter is non-null if the value type is boxed.
            Type[] dynamicParameters;
            if (type.Type.IsValueType)
            {
                dynamicParameters = new Type[] { typeof(Pickler), typeof(PicklerSerializationState), type.Type, typeof(object) };
            }
            else
            {
                dynamicParameters = new Type[] { typeof(Pickler), typeof(PicklerSerializationState), type.Type, typeof(bool) };
            }

            // All other types we build a dynamic method for it.
            var dynamicMethod = new DynamicMethod("Serialize_" + type.Type.Name, typeof(void), dynamicParameters, typeof(Pickler));
            _serializationMethods.Add(type.Type, dynamicMethod);

            var il = dynamicMethod.GetILGenerator();
            // Nearly every type needs access to the Writer property
            var binaryWriterProperty = typeof(PicklerSerializationState).GetProperty("Writer");
            System.Diagnostics.Debug.Assert(binaryWriterProperty != null, "Could not lookup Writer property");
            var binaryWriterPropertyGet = binaryWriterProperty.GetMethod;
            System.Diagnostics.Debug.Assert(binaryWriterPropertyGet != null, "Writer property had no get method");

            // All object based methods need the WriteObjectOperation and WriteObjectType method
            var writeObjectOperationMethod = typeof(Pickler).GetMethod("WriteObjectOperation", BindingFlags.NonPublic | BindingFlags.Static);
            System.Diagnostics.Debug.Assert(writeObjectOperationMethod != null, "Could not lookup WriteObjectOperation method");
            var writeObjectTypeMethod = typeof(Pickler).GetMethod("WriteObjectType", BindingFlags.NonPublic | BindingFlags.Instance);
            System.Diagnostics.Debug.Assert(writeObjectTypeMethod != null, "Could not lookup WriteObjectType method");

            // All methods need the memo methods (yes even value types because they could have been boxed)
            var addMemoMethod = typeof(Pickler).GetMethod("AddMemo", BindingFlags.NonPublic | BindingFlags.Static, new Type[] { typeof(PicklerSerializationState), typeof(object) });
            System.Diagnostics.Debug.Assert(addMemoMethod != null, "Could not lookup AddMemo method");
            var maybeWriteMemoMethod = typeof(Pickler).GetMethod("MaybeWriteMemo", BindingFlags.NonPublic | BindingFlags.Static, new Type[] { typeof(PicklerSerializationState), typeof(object) });
            System.Diagnostics.Debug.Assert(maybeWriteMemoMethod != null, "Could not lookup MaybeWriteMemo method");

            if (type.Error != null)
            {
                // This type isn't actually serialisable, if it's null we're ok but otherwise throw.

                var exceptionConstructor = typeof(Exception).GetConstructor(new Type[] { typeof(string) });
                System.Diagnostics.Debug.Assert(exceptionConstructor != null, "Could not lookup Exception constructor");

                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { typeof(byte) });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");

                var earlyReturn = il.DefineLabel();

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brtrue, prechecked);
                il.Emit(OpCodes.Ldarg_2);
                // All we care about is nullness, for which we'll write ObjectOperation.Null
                il.Emit(OpCodes.Brtrue, prechecked);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                il.Emit(OpCodes.Ldc_I4, (int)ObjectOperation.Null);
                il.Emit(OpCodes.Callvirt, writeMethod);
                il.Emit(OpCodes.Br, earlyReturn);
                il.MarkLabel(prechecked);

                // Throw the erorr
                il.Emit(OpCodes.Ldstr, type.Error);
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(DBNull))
            {
                // DBNull is easy, just do nothing
                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(UIntPtr))
            {
                // UIntPtr (and IntPtr) just cast to their 64 bit value and write that
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { typeof(ulong) });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");
                il.Emit(OpCodes.Ldarga_S, 2);
                var castMethod = typeof(UIntPtr).GetMethod("ToUInt64");
                System.Diagnostics.Debug.Assert(castMethod != null, "Could not lookup ToUInt64 method");
                il.Emit(OpCodes.Call, castMethod);
                il.Emit(OpCodes.Callvirt, writeMethod);

                // Maybe memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, addMemoMethod);

                il.Emit(OpCodes.Ret);
            }
            else if (type.Type == typeof(IntPtr))
            {
                // UIntPtr (and IntPtr) just cast to their 64 bit value and write that
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { typeof(long) });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");
                il.Emit(OpCodes.Ldarga_S, 2);
                var castMethod = typeof(IntPtr).GetMethod("ToInt64");
                System.Diagnostics.Debug.Assert(castMethod != null, "Could not lookup ToInt64 method");
                il.Emit(OpCodes.Call, castMethod);
                il.Emit(OpCodes.Callvirt, writeMethod);

                // Maybe memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, addMemoMethod);

                il.Emit(OpCodes.Ret);
            }
            else if (type.Type.IsPrimitive || type.Type == typeof(decimal))
            {
                // Lookup the write method for this type
                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { type.Type });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");

                // Primitive type like bool
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, writeMethod);

                // Maybe memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, addMemoMethod);

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

                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { enumType });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, writeMethod);

                // Maybe memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, addMemoMethod);

                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Type == typeof(string))
            {
                #region String
                var earlyReturn = il.DefineLabel();

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brtrue, prechecked);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, writeObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);
                il.MarkLabel(prechecked);

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { typeof(string) });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, writeMethod);

                // Memoize
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, addMemoMethod);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.IsNullable)
            {
                #region Nullable
                // Nullable<T> always writes the same way
                var innerTypeInfo = type.Element;
                System.Diagnostics.Debug.Assert(innerTypeInfo != null, $"{type.Type} was nullable but Element was null");
                var innerMethod = GetSerializationMethod(innerTypeInfo);

                var nullReturn = il.DefineLabel();

                var hasValueProperty = type.Type.GetProperty("HasValue");
                System.Diagnostics.Debug.Assert(hasValueProperty != null, "Could not lookup HasValue property");
                System.Diagnostics.Debug.Assert(hasValueProperty.GetMethod != null, "HasValue property had no get method");
                il.Emit(OpCodes.Ldarga_S, 2);
                il.Emit(OpCodes.Call, hasValueProperty.GetMethod);
                il.Emit(OpCodes.Brfalse, nullReturn);

                var writeMethod = typeof(BinaryWriter).GetMethod("Write", new Type[] { typeof(bool) });
                System.Diagnostics.Debug.Assert(writeMethod != null, "Could not lookup write method");

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, writeMethod);

                var valueProperty = type.Type.GetProperty("Value");
                System.Diagnostics.Debug.Assert(valueProperty != null, "Could not lookup Value property");
                System.Diagnostics.Debug.Assert(valueProperty.GetMethod != null, "Value property had no get method");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarga_S, 2);
                il.Emit(OpCodes.Call, valueProperty.GetMethod);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, innerMethod);

                // We can't directly memoize Nullable<T> it would always be boxed, and the inner method should handle this. So just return now.

                il.Emit(OpCodes.Ret);

                il.MarkLabel(nullReturn);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, writeMethod);
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
                var innerMethod = GetSerializationMethod(elementType);

                // Special case szarray (i.e. Rank 1, lower bound 0)
                var isSZ = type.Type.IsSZArray;

                var write7BitMethod = typeof(BinaryWriter).GetMethod("Write7BitEncodedInt", new Type[] { typeof(int) });
                System.Diagnostics.Debug.Assert(write7BitMethod != null, "Could not lookup Write7BitEncodedInt method");

                var earlyReturn = il.DefineLabel();

                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brtrue, prechecked);

                // If not pre-checked we _always_ need to do a memo/null check
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, writeObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);

                // But we only need to do a type check if this array could be variant.
                // e.g. an int[] location always holds an int[] runtime value, but an object[] location could hold a string[].
                // Unexpectedly a Type[] _must_ contain a Type[] because we don't allow other static type.
                if (!elementType.IsValueType && !elementType.IsSealed)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldtoken, type.Type);
                    il.Emit(OpCodes.Callvirt, writeObjectTypeMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);
                }

                il.MarkLabel(prechecked);

                // If we get here we know we are trying to write an array of exactly this type.

                LocalBuilder? szLengthLocal = null;
                var dimensions = 1;
                if (isSZ)
                {
                    var lengthProperty = type.Type.GetProperty("Length");
                    System.Diagnostics.Debug.Assert(lengthProperty != null, "Could not lookup Length property");
                    System.Diagnostics.Debug.Assert(lengthProperty.GetMethod != null, "Length property had no get method");
                    szLengthLocal = il.DeclareLocal(typeof(int));

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Callvirt, lengthProperty.GetMethod);
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, szLengthLocal);
                    il.Emit(OpCodes.Callvirt, write7BitMethod);
                }
                else
                {
                    var getLengthMethod = type.Type.GetMethod("GetLength");
                    System.Diagnostics.Debug.Assert(getLengthMethod != null, "Could not lookup GetLength method");

                    var getLowerBoundMethod = type.Type.GetMethod("GetLowerBound");
                    System.Diagnostics.Debug.Assert(getLowerBoundMethod != null, "Could not lookup GetLowerBound method");

                    // This might just be rank 1 but with non-normal bounds
                    dimensions = type.Type.GetArrayRank();
                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Callvirt, getLengthMethod);
                        il.Emit(OpCodes.Callvirt, write7BitMethod);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, binaryWriterPropertyGet);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Callvirt, getLowerBoundMethod);
                        il.Emit(OpCodes.Callvirt, write7BitMethod);
                    }
                }

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, addMemoMethod);

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

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldloc, indexLocal);
                    il.Emit(OpCodes.Ldelem, elementType.Type);
                    if (elementType.IsValueType)
                    {
                        il.Emit(OpCodes.Ldnull);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_0);
                    }
                    il.Emit(OpCodes.Call, innerMethod);

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

                    var getMethod = type.Type.GetMethod("Get");
                    System.Diagnostics.Debug.Assert(getMethod != null, "Could not lookup Get method");

                    // Copy values dimension by dimension
                    var variables = new (Label, Label, LocalBuilder, LocalBuilder)[dimensions];
                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var startOfLoop = il.DefineLabel();
                        var endOfLoop = il.DefineLabel();
                        var indexLocal = il.DeclareLocal(typeof(int));
                        var upperBoundLocal = il.DeclareLocal(typeof(int));

                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldc_I4, dimension);
                        il.Emit(OpCodes.Callvirt, getUpperBoundMethod);
                        il.Emit(OpCodes.Stloc, upperBoundLocal);

                        variables[dimension] = (startOfLoop, endOfLoop, indexLocal, upperBoundLocal);
                    }

                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var (startOfLoop, endOfLoop, indexLocal, upperBoundLocal) = variables[dimension];

                        // Set the index back to the lower bound for this dimension
                        il.Emit(OpCodes.Ldarg_2);
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

                    // Index into the array and serialize the value
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    for (int dimension = 0; dimension < dimensions; ++dimension)
                    {
                        var (_, _, indexLocal, _) = variables[dimension];
                        il.Emit(OpCodes.Ldloc, indexLocal);
                    }
                    il.Emit(OpCodes.Callvirt, getMethod);
                    if (elementType.IsValueType)
                    {
                        il.Emit(OpCodes.Ldnull);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_0);
                    }
                    il.Emit(OpCodes.Call, innerMethod);

                    for (int dimension = dimensions - 1; dimension >= 0; --dimension)
                    {
                        var (startOfLoop, endOfLoop, indexLocal, upperBoundLocal) = variables[dimension];

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
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.TupleArguments != null)
            {
                #region Tuple
                // This is either Tuple or ValueTuple
                // N.B This isn't for any ITuple as there might be user defined types that inherit from Tuple and it's not safe to pass them in here.

                var earlyReturn = il.DefineLabel();
                if (!type.Type.IsValueType)
                {
                    // We _might_ have to write out object headers here
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Brtrue, prechecked);

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, writeObjectOperationMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldtoken, type.Type);
                    il.Emit(OpCodes.Callvirt, writeObjectTypeMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    il.MarkLabel(prechecked);
                }

                var index = 0;
                foreach (var item in type.TupleArguments)
                {
                    // Lookup the ItemX (or Rest) property/field and serialize it
                    index++;
                    var itemName = index == 8 ? "Rest" : "Item" + index.ToString();
                    var innerMethod = GetSerializationMethod(item);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    if (type.Type.IsValueType)
                    {
                        var itemField = type.Type.GetField(itemName);
                        System.Diagnostics.Debug.Assert(itemField != null, $"Could not lookup {itemName} field");
                        il.Emit(OpCodes.Ldfld, itemField);
                    }
                    else
                    {
                        var itemProperty = type.Type.GetProperty(itemName);
                        System.Diagnostics.Debug.Assert(itemProperty != null, $"Could not lookup {itemName} property");
                        System.Diagnostics.Debug.Assert(itemProperty.GetMethod != null, "Item property had no get method");
                        il.Emit(OpCodes.Callvirt, itemProperty.GetMethod);
                    }

                    if (item.IsValueType)
                    {
                        il.Emit(OpCodes.Ldnull);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_0);
                    }
                    il.Emit(OpCodes.Call, innerMethod);

                    // If this is a reference to a tuple (i.e. Tuple, or boxed ValueTuple) then serialising the fields may serialise the tuple itself.
                    // That is:
                    //  if (MaybeWriteMemo(tuple)) return;
                    // MaybeWriteMemo can handle a null input
                    var skipMemo = il.DefineLabel();
                    if (type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Brfalse, skipMemo);
                    }
                    il.Emit(OpCodes.Ldarg_1);
                    if (type.Type.IsValueType)
                    {
                        il.Emit(OpCodes.Ldarg_3);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldarg_2);
                    }
                    il.Emit(OpCodes.Call, maybeWriteMemoMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);
                    il.MarkLabel(skipMemo);
                }

                // Memoize the result
                il.Emit(OpCodes.Ldarg_1);
                if (type.Type.IsValueType)
                {
                    il.Emit(OpCodes.Ldarg_3);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_2);
                }
                il.Emit(OpCodes.Call, addMemoMethod);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Mode == PickledTypeMode.IsDelegate)
            {
                #region Delegate
                // Delegates are always reference objects, so no worry about boxing here.
                var writeDelegateMethod = typeof(Pickler).GetMethod("WriteDelegate", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Diagnostics.Debug.Assert(writeDelegateMethod != null, "Could not lookup WriteDelegate method");

                var earlyReturn = il.DefineLabel();
                // We _might_ have to write out object headers here
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brtrue, prechecked);

                // We always need to do a null/memo check here
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, writeObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);

                il.MarkLabel(prechecked);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, writeDelegateMethod);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.IsAbstract)
            {
                #region Abstract
                // Abstract types must do dynamic dispatch
                var earlyReturn = il.DefineLabel();

                var exceptionConstructor = typeof(Exception).GetConstructor(new Type[] { typeof(string) });
                System.Diagnostics.Debug.Assert(exceptionConstructor != null, "Could not lookup Exception constructor");

                // If this say's it's prechecked that's a bug!
                var prechecked = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Brfalse, prechecked);
                il.Emit(OpCodes.Ldstr, "Abstract type was called as prechecked");
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);
                il.MarkLabel(prechecked);

                // We always need to do a null/memo check here
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, writeObjectOperationMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);
                // And a type check because this type is abstract
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                // Make a zero Nullable<RuntimeTypeHandle>
                var nullableLocal = il.DeclareLocal(typeof(RuntimeTypeHandle?));
                il.Emit(OpCodes.Ldloc, nullableLocal);
                il.Emit(OpCodes.Callvirt, writeObjectTypeMethod);
                il.Emit(OpCodes.Brtrue, earlyReturn);

                // If we get here something has gone very wrong
                il.Emit(OpCodes.Ldstr, "Tried to serialize an abstract type");
                il.Emit(OpCodes.Newobj, exceptionConstructor);
                il.Emit(OpCodes.Throw);

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
                #endregion
            }
            else if (type.Mode == PickledTypeMode.IsReduced)
            {
                #region IReducer
                // Use of an IReducer causes boxing anyway so just cast up to object.
                var writeReducerMethod = typeof(Pickler).GetMethod("WriteReducer", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Diagnostics.Debug.Assert(writeReducerMethod != null, "Could not lookup WriteReducer method");

                var earlyReturn = il.DefineLabel();
                if (!type.IsValueType)
                {
                    // We _might_ have to write out object headers here
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Brtrue, prechecked);

                    // We always need to do a null/memo check here
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, writeObjectOperationMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    // But we only need to do a type check if the type is not sealed
                    if (!type.IsSealed)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldtoken, type.Type);
                        il.Emit(OpCodes.Callvirt, writeObjectTypeMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);
                    }

                    il.MarkLabel(prechecked);
                }

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                if (type.IsValueType)
                {
                    il.Emit(OpCodes.Box);
                }
                il.Emit(OpCodes.Callvirt, writeReducerMethod);

                // Memoize the result
                il.Emit(OpCodes.Ldarg_1);
                if (type.IsValueType)
                {
                    il.Emit(OpCodes.Ldarg_3);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_2);
                }
                il.Emit(OpCodes.Call, addMemoMethod);

                il.MarkLabel(earlyReturn);
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
                if (!type.IsValueType)
                {
                    // We _might_ have to write out object headers here
                    var prechecked = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Brtrue, prechecked);

                    // We always need to do a null/memo check here
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, writeObjectOperationMethod);
                    il.Emit(OpCodes.Brtrue, earlyReturn);

                    // But we only need to do a type check if the type is not sealed
                    if (!type.IsSealed)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldtoken, type.Type);
                        il.Emit(OpCodes.Callvirt, writeObjectTypeMethod);
                        il.Emit(OpCodes.Brtrue, earlyReturn);
                    }

                    il.MarkLabel(prechecked);
                }

                il.Emit(OpCodes.Ldarg_1);
                if (type.IsValueType)
                {
                    il.Emit(OpCodes.Ldarg_3);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_2);
                }
                il.Emit(OpCodes.Call, addMemoMethod);

                foreach (var (fieldType, field) in type.SerialisedFields)
                {
                    var innerMethod = GetSerializationMethod(fieldType);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldfld, field);
                    if (fieldType.IsValueType)
                    {
                        il.Emit(OpCodes.Ldnull);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4_0);
                    }
                    il.Emit(OpCodes.Call, innerMethod);
                }

                il.MarkLabel(earlyReturn);
                il.Emit(OpCodes.Ret);
                #endregion
            }

            return dynamicMethod;
        }

        private void InvokeSerializationMethod(SerialisedObjectTypeInfo typeInfo, PicklerSerializationState state, object? obj, object? memo, bool prechecked)
        {
            var serializationMethod = GetSerializationMethod(typeInfo);
            try
            {
                if (typeInfo.IsValueType)
                {
                    serializationMethod.Invoke(null, new[] { this, state, obj, memo });
                }
                else
                {
                    // We're calling a method like Serialize_Tuple`2 here but we now know that 
                    // A) obj is not null, or memo'd
                    // B) Is exactly a Tuple`2
                    // So we pass true for the prechecked
                    serializationMethod.Invoke(null, new[] { this, state, obj, prechecked });
                }
            }
            catch (TargetInvocationException exc)
            {
                System.Diagnostics.Debug.Assert(exc.InnerException != null, "TargetInvocationException.InnerException was null");
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exc.InnerException);
            }
        }

        private static bool MaybeWriteMemo(PicklerSerializationState state, object obj)
        {
            System.Diagnostics.Debug.Assert(obj != null, "Should not call MaybeWriteMemo with a null object");
            var wroteMemo = state.MaybeWriteMemo(obj, null);
            if (!wroteMemo)
            {
                state.Writer.Write15BitEncodedLong(0);
            }
            return wroteMemo;
        }

        /// <summary>
        /// WriteObjectHeader deals with the common logic that all reference types need to deal with, that is.
        /// </summary>
        private static bool WriteObjectOperation(PicklerSerializationState state, [NotNullWhen(false)] object? obj)
        {
            if (Object.ReferenceEquals(obj, null))
            {
                state.Writer.Write((byte)ObjectOperation.Null);
                return true;
            }

            if (state.MaybeWriteMemo(obj, (byte)ObjectOperation.Memo))
            {
                return true;
            }

            state.Writer.Write((byte)ObjectOperation.Object);
            return false;
        }

        /// <summary>
        /// WriteObjectType gets the runtime type of obj, writes out the TypeInfo for it if needed, rechecks the memo state,
        /// and then checks if it's the expected type. If not it dynamic dispatchs to correct method.
        /// </summary>
        private bool WriteObjectType(PicklerSerializationState state, object obj, RuntimeTypeHandle? expectedType)
        {
            var runtimeType = obj.GetType();
            SerializeType(state, runtimeType, null, null);
            // Don't serialize static fields at this level
            state.Stages.PopStages(state, 3);
            // Get the type info for this type
            var typeInfo = GetCachedTypeInfo(runtimeType);
            MaybeWriteTypeInfo(state, typeInfo);
            // Now we can write out static fields (which might serialise this type again)
            state.Stages.PopStages(state, 4);
            // At this point we _may_ have written out the value as part of circular static fields, so write a maybememo
            if (state.MaybeWriteMemo(obj, null)) return true;
            state.Writer.Write15BitEncodedLong(0);

            // If runtimeType == expected then return that this expected type needs writing,
            // else dynamic dispatch to the correct type but tell it headers are already set
            if (expectedType.HasValue && runtimeType == expectedType.Value) return false;

            InvokeSerializationMethod(typeInfo, state, obj, obj, true);
            return true;
        }

        private void WriteReducer(PicklerSerializationState state, object obj)
        {
            // We know obj is not null by this point
            var runtimeType = obj.GetType();
            var typeInfo = GetCachedTypeInfo(runtimeType);

            System.Diagnostics.Debug.Assert(typeInfo.Reducer != null, "Called WriteReducer for a type without a reducer");

            // We've got a reducer for the type (or its generic variant)
            var (method, target, args) = typeInfo.Reducer.Reduce(obj);

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

                Serialize_Object(this, state, target, false);
            }
            else
            {
                throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was '{method}'.");
            }

            state.Writer.Write7BitEncodedInt(args.Length);
            foreach (var arg in args)
            {
                Serialize_Object(this, state, arg, false);
            }
        }

        private void WriteDelegate(PicklerSerializationState state, Delegate obj)
        {
            // Delegates are just a target and a method
            var invocationList = obj.GetInvocationList();
            state.Writer.Write7BitEncodedInt(invocationList.Length);

            // We need to memoise delegates correctly, if the invocation list has a single element we write out the target and method,
            // but if the invocation list has multiple elements we need to recurse them through Serialize so they can be memoised correctly
            if (invocationList.Length == 1)
            {
                Serialize_Object(this, state, invocationList[0].Target, false);
                // Serialise the sub-objects may recurse and serialise this delegate so early out if that's the case
                if (state.MaybeWriteMemo(obj, null)) return;
                state.Writer.Write15BitEncodedLong(0);

                Serialize_MethodInfo(this, state, invocationList[0].Method, false);
                if (state.MaybeWriteMemo(obj, null)) return;
                state.Writer.Write15BitEncodedLong(0);
            }
            else
            {
                foreach (var invocation in invocationList)
                {
                    Serialize_Delegate(this, state, invocation, false);
                    if (state.MaybeWriteMemo(obj, null)) return;
                    state.Writer.Write15BitEncodedLong(0);
                }
            }
            AddMemo(state, obj);
        }

        #region Built in serialization methods
        private static void Serialize_Object(Pickler self, PicklerSerializationState state, object? obj, bool prechecked)
        {
            // It's known that this IS a System.Object and it's not null or memo'd
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
                if (self.WriteObjectType(state, obj, typeof(object).TypeHandle))
                {
                    return;
                }
            }
            // Don't need to actually write anything for System.Object
        }

        private static void Serialize_Delegate(Pickler self, PicklerSerializationState state, Delegate? obj, bool prechecked)
        {
            System.Diagnostics.Debug.Assert(!prechecked, "Serialize_Delegate was called as prechecked");

            if (WriteObjectOperation(state, obj))
            {
                return;
            }
            if (self.WriteObjectType(state, obj, null))
            {
                return;
            }

            throw new Exception("Tried to serialize an abstract Delegate");
        }

        private static void Serialize_Type(Pickler self, PicklerSerializationState state, Type? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "Type was null but was prechecked");
            self.SerializeType(state, obj, null, null, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_Module(Pickler self, PicklerSerializationState state, Module? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "Module was null but was prechecked");
            self.SerializeModule(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_Assembly(Pickler self, PicklerSerializationState state, Assembly? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "Assembly was null but was prechecked");
            self.SerializeAssembly(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_MethodInfo(Pickler self, PicklerSerializationState state, MethodInfo? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "MethodInfo was null but was prechecked");
            self.SerializeMethodInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_DynamicMethod(Pickler self, PicklerSerializationState state, DynamicMethod? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "DynamicMethod was null but was prechecked");
            self.SerializeMethodInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_ConstructorInfo(Pickler self, PicklerSerializationState state, ConstructorInfo? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "ConstructorInfo was null but was prechecked");
            self.SerializeConstructorInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_FieldInfo(Pickler self, PicklerSerializationState state, FieldInfo? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "FieldInfo was null but was prechecked");
            self.SerializeFieldInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_PropertyInfo(Pickler self, PicklerSerializationState state, PropertyInfo? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "PropertyInfo was null but was prechecked");
            self.SerializePropertyInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_EventInfo(Pickler self, PicklerSerializationState state, EventInfo? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "EventInfo was null but was prechecked");
            self.SerializeEventInfo(state, obj, false);
            state.Stages.PopStages(state);
        }

        private static void Serialize_Pickler(Pickler self, PicklerSerializationState state, Pickler? obj, bool prechecked)
        {
            if (!prechecked)
            {
                if (WriteObjectOperation(state, obj))
                {
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(obj != null, "Pickler was null but was prechecked");

            // TODO: We should work out how to serialize Pickler itself, but for now skip it.
            //Serialize_Object(self, state, obj.AssemblyLoadContext, false);
            //Serialize_Object(self, state, obj._assemblyPickleMode, false);
            //Serialize_Object(self, state, obj._reducers.Values.ToArray(), false);
            AddMemo(state, obj);
        }
        #endregion

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

            Serialize_Object(this, state, rootObject, false);
            state.Stages.AssertEmpty();
        }
    }
}
