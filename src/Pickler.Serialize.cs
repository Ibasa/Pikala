using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    public struct SerializeInformation
    {
        private readonly object? _value;
        private Type? _type;

        public Type RuntimeType
        {
            get
            {
                if (_type == null)
                {
                    _type = _value?.GetType() ?? typeof(object);
                }
                return _type;
            }
        }
        public Type StaticType { get; }
        public bool ShouldMemo { get; }

        /// <summary>
        /// This is an optional type that defines a parent contextual type.
        /// For example if we're serialising a load of FieldInfos but we know their all for the same type then we still want 
        /// to hit all our memo machinery but can elide serializing out the type.
        /// </summary>
        public Type? ContextType { get; }

        public SerializeInformation(object? value, Type staticType, bool shouldMemo, Type? contextType)
        {
            _value = value;
            _type = null;
            StaticType = staticType;
            ShouldMemo = shouldMemo;
            ContextType = contextType;
        }
    }

    public sealed partial class Pickler
    {
        /// <summary>
        /// There are some objects that we shouldn't bother to memoise because it's cheaper to just write their tokens.
        /// </summary>
        private bool ShouldMemo(object? obj, Type staticType)
        {
            // If the static type is a value type we shouldn't memo because this is a value not a reference
            if (staticType.IsValueType) { return false; }

            // mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib)) { return false; }

            // The manifest module for mscorlib gets saved as two tokens, no worse than a memo and probably better
            if (Object.ReferenceEquals(obj, mscorlib.ManifestModule)) { return false; }

            return true;
        }

        private SerializeInformation MakeInfo(object? obj, Type staticType, bool? shouldMemo = null, Type? contextType = null)
        {
            if (obj == null) { return new SerializeInformation(obj, staticType, false, contextType); }

            if (!shouldMemo.HasValue)
            {
                shouldMemo = ShouldMemo(obj, staticType);
            }

            return new SerializeInformation(obj, staticType, shouldMemo.Value, contextType);
        }

        private bool PickleByValue(Assembly assembly)
        {
            // We never pickle mscorlib by value, don't even give the user a choice
            if (assembly == mscorlib) { return false; }

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
                Serialize(state, st.Type, MakeInfo(st.Type, typeof(Type), true));
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
                Serialize(state, scgt.GenericTypeDefinition, MakeInfo(scgt.GenericTypeDefinition, typeof(Type), true));
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
            state.Writer.Write7BitEncodedInt(constructorParameters.Length);
            foreach (var parameter in constructorParameters)
            {
                Serialize(state, parameter.ParameterType, MakeInfo(parameter.ParameterType, typeof(Type), true), genericTypeParameters);
            }
            foreach (var parameter in constructorParameters)
            {
                state.Writer.WriteNullableString(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);
            }

            var methodBody = constructor.GetMethodBody();
            System.Diagnostics.Debug.Assert(methodBody != null, "GetMethodBody returned null for a constructor");

            state.Writer.Write(methodBody.InitLocals);

            state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
            foreach (var local in methodBody.LocalVariables)
            {
                Serialize(state, local.LocalType, MakeInfo(local.LocalType, typeof(Type), true), genericTypeParameters);
            }

            var collectedTypes = CollectTypes(genericTypeParameters, constructor.Module, null, methodBody);
            state.Writer.Write7BitEncodedInt(collectedTypes.Count);
            foreach (var type in collectedTypes)
            {
                Serialize(state, type, MakeInfo(type, typeof(Type), true), genericTypeParameters);
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

            Serialize(state, method.ReturnType, MakeInfo(method.ReturnType, typeof(Type), true), genericTypeParameters, genericMethodParameters);

            var methodParameters = method.GetParameters();
            state.Writer.Write7BitEncodedInt(methodParameters.Length);
            foreach (var parameter in methodParameters)
            {
                Serialize(state, parameter.ParameterType, MakeInfo(parameter.ParameterType, typeof(Type), true), genericTypeParameters, genericMethodParameters);
            }
            foreach (var parameter in methodParameters)
            {
                // 22.33: 9. Name can be null or non-null
                state.Writer.WriteNullableString(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);
            }

            if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || method.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || method.Attributes.HasFlag(MethodAttributes.Abstract))
            {

            }
            else
            {
                var methodBody = method.GetMethodBody();
                System.Diagnostics.Debug.Assert(methodBody != null, "GetMethodBody returned null unexpectedly");

                state.Writer.Write(methodBody.InitLocals);

                state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
                foreach (var local in methodBody.LocalVariables)
                {
                    Serialize(state, local.LocalType, MakeInfo(local.LocalType, typeof(Type), true), genericTypeParameters, genericMethodParameters);
                }

                var collectedTypes = CollectTypes(genericTypeParameters, method.Module, genericMethodParameters, methodBody);
                state.Writer.Write7BitEncodedInt(collectedTypes.Count);
                foreach (var type in collectedTypes)
                {
                    Serialize(state, type, MakeInfo(type, typeof(Type), true), genericTypeParameters, genericMethodParameters);
                }
            }
        }

        private OpCode ReadOpCode(BinaryReader reader)
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

        private HashSet<Type> CollectTypes(Type[]? genericTypeParameters, Module methodModule, Type[]? genericMethodParameters, MethodBody methodBody)
        {
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
                            Serialize(state, typeInfo, MakeInfo(typeInfo, typeof(Type), true), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldToken = ilReader.ReadInt32();
                            var fieldInfo = methodModule.ResolveField(fieldToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, fieldInfo, MakeInfo(fieldInfo, typeof(FieldInfo), true), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodToken = ilReader.ReadInt32();
                            var methodInfo = methodModule.ResolveMethod(methodToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, methodInfo, MakeInfo(methodInfo, typeof(MethodInfo), true), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var memberToken = ilReader.ReadInt32();
                            var memberInfo = methodModule.ResolveMember(memberToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, memberInfo, MakeInfo(memberInfo, typeof(MemberInfo), true), genericTypeParameters, genericMethodParameters);
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
            state.Writer.Write((byte)PickleOperation.ModuleDef);
            state.Writer.Write(module.Name);
            Serialize(state, module.Assembly, MakeInfo(module.Assembly, typeof(Assembly)));

            var fields = module.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(fields.Length);
            foreach (var field in fields)
            {
                state.Writer.Write(field.Name);
                state.Writer.Write((int)field.Attributes);

                // We expect all module fields to be RVA fields
                System.Diagnostics.Debug.Assert(field.Attributes.HasFlag(FieldAttributes.HasFieldRVA));
                // with a StructLayoutAttribute
                System.Diagnostics.Debug.Assert(field.FieldType.StructLayoutAttribute != null);

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

            state.PushTrailer(() =>
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
            },
            () => { });
        }

        private void SerializeDef(PicklerSerializationState state, Type type, Type[]? genericParameters)
        {
            if (type.IsValueType)
            {
                // Value types don't ever have any base class
            }
            else if (type.BaseType == typeof(object))
            {
                state.Writer.Write((byte)PickleOperation.Null);
            }
            else
            {
                Serialize(state, type.BaseType, MakeInfo(type.BaseType, typeof(Type), true), genericParameters);
            }

            var interfaces = type.GetInterfaces();
            state.Writer.Write7BitEncodedInt(interfaces.Length);
            foreach (var interfaceType in interfaces)
            {
                Serialize(state, interfaceType, MakeInfo(interfaceType, typeof(Type), true), genericParameters);

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
                Serialize(state, field.FieldType, MakeInfo(field.FieldType, typeof(Type), true), genericParameters);
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
                Serialize(state, property.PropertyType, MakeInfo(property.PropertyType, typeof(Type), true), genericParameters);
                var indexParameters = property.GetIndexParameters();
                state.Writer.Write7BitEncodedInt(indexParameters.Length);
                foreach (var indexParameter in indexParameters)
                {
                    Serialize(state, indexParameter.ParameterType, MakeInfo(indexParameter.ParameterType, typeof(Type), true), genericParameters);
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
                Serialize(state, evt.EventHandlerType, MakeInfo(evt.EventHandlerType, typeof(Type), true), genericParameters);

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

            state.PushTrailer(() =>
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
            }, () =>
            {
                var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in staticFields)
                {
                    if (!field.IsInitOnly)
                    {
                        state.Writer.Write(field.Name);
                        var value = field.GetValue(null);
                        var fieldInfo = MakeInfo(value, typeof(object), ShouldMemo(value, field.FieldType));
                        Serialize(state, value, fieldInfo, genericParameters);
                    }
                }
            });
        }

        private void SerializeArray(PicklerSerializationState state, Array obj, Type objType)
        {
            // This is an array, write the type then loop over each item.
            // Theres a performance optimisation we could do here with value types,
            // we we fetch the handler only once.

            if (obj.Rank > byte.MaxValue)
            {
                // Who has 256 rank arrays!? The runtime specification says this can be at most 32.
                throw new NotImplementedException($"Pikala does not support arrays of rank higher than {byte.MaxValue}, got {obj.Rank}");
            }

            var elementType = objType.GetElementType();
            System.Diagnostics.Debug.Assert(elementType != null, "GetElementType returned null for an array type");
            Serialize(state, elementType, MakeInfo(elementType, typeof(Type), true));

            // Special case szarray (i.e. Rank 1, lower bound 0)
            if (objType.IsSZArray)
            {
                state.Writer.Write7BitEncodedInt(obj.Length);
            }
            else
            {
                // This might just be rank 1 but with non-normal bounds
                state.Writer.Write((byte)obj.Rank);
                for (int dimension = 0; dimension < obj.Rank; ++dimension)
                {
                    state.Writer.Write7BitEncodedInt(obj.GetLength(dimension));
                    state.Writer.Write7BitEncodedInt(obj.GetLowerBound(dimension));
                }
            }
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
                    Serialize(state, item, MakeInfo(item, elementType));
                }
            }
        }

        private void WriteCustomAttributeValue(PicklerSerializationState state, object? value, SerializeInformation info)
        {
            // argument might be a ReadOnlyCollection[CustomAttributeTypedArgument] but we should write that as just an array of values
            if (value is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> collection)
            {
                var result = new object?[collection.Count];
                for (int i = 0; i < result.Length; ++i)
                {
                    result[i] = collection[i].Value;
                }
                // No point memoising this array, we just created it!
                Serialize(state, result, new SerializeInformation(result, typeof(object?[]), false, null));
            }
            else
            {
                Serialize(state, value, info);
            }
        }

        private void WriteCustomAttributes(PicklerSerializationState state, CustomAttributeData[] attributes)
        {
            state.Writer.Write7BitEncodedInt(attributes.Length);
            foreach (var attribute in attributes)
            {
                Serialize(state, attribute.AttributeType, MakeInfo(attribute.AttributeType, typeof(Type), true));

                Serialize(state, attribute.Constructor, MakeInfo(attribute.Constructor, typeof(ConstructorInfo), true, attribute.AttributeType));
                state.Writer.Write7BitEncodedInt(attribute.ConstructorArguments.Count);
                foreach (var argument in attribute.ConstructorArguments)
                {
                    WriteCustomAttributeValue(state, argument.Value, MakeInfo(argument.Value, typeof(object)));
                }

                var fieldCount = attribute.NamedArguments.Count(argument => argument.IsField);
                var propertyCount = attribute.NamedArguments.Count - fieldCount;

                state.Writer.Write7BitEncodedInt(propertyCount);
                foreach (var argument in attribute.NamedArguments)
                {
                    if (!argument.IsField)
                    {
                        var property = (PropertyInfo)argument.MemberInfo;
                        Serialize(state, property, MakeInfo(property, typeof(PropertyInfo), true, attribute.AttributeType));
                        var value = argument.TypedValue.Value;
                        var info = MakeInfo(argument.TypedValue.Value, typeof(object), ShouldMemo(value, property.PropertyType));
                        WriteCustomAttributeValue(state, value, info);
                    }
                }

                state.Writer.Write7BitEncodedInt(fieldCount);
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.IsField)
                    {
                        var field = (FieldInfo)argument.MemberInfo;
                        Serialize(state, field, MakeInfo(field, typeof(FieldInfo), true, attribute.AttributeType));
                        var value = argument.TypedValue.Value;
                        var info = MakeInfo(argument.TypedValue.Value, typeof(object), ShouldMemo(value, field.FieldType));
                        WriteCustomAttributeValue(state, value, info);
                    }
                }
            }
        }

        private void SerializeAssembly(PicklerSerializationState state, Assembly assembly)
        {
            // This is an assembly, we need to emit an assembly name so it can be reloaded

            // Is this mscorlib? If so we write out a single token for it
            if (assembly == mscorlib)
            {
                state.Writer.Write((byte)PickleOperation.Mscorlib);
            }
            // Is this assembly one we should save by value?
            else if (PickleByValue(assembly))
            {
                state.RunWithTrailers(() =>
                {
                    // Write out an assembly definition, we'll build a dynamic assembly for this on the other side
                    state.Writer.Write((byte)PickleOperation.AssemblyDef);
                    state.Writer.Write(assembly.FullName);
                    state.PushTrailer(() =>
                    {
                        WriteCustomAttributes(state, assembly.CustomAttributes.ToArray());
                    }, () => { });
                });
            }
            else
            {
                // Just write out an assembly refernce
                state.Writer.Write((byte)PickleOperation.AssemblyRef);
                state.Writer.Write(assembly.FullName);
            }
        }

        private void SerializeModule(PicklerSerializationState state, Module module)
        {
            // This is a module, we need to emit a reference to the assembly it's found in and it's name

            // Is this assembly one we should save by value?
            if (PickleByValue(module.Assembly))
            {
                state.RunWithTrailers(() =>
                {
                    SerializeModuleDef(state, module);
                });
            }
            else
            {
                // We can just write a ref here, lets check if this is the ONLY module on the assembly (i.e. the ManifestModule)
                // because we can then write out a token instead of a name
                if (module == module.Assembly.ManifestModule)
                {
                    state.Writer.Write((byte)PickleOperation.ManifestModuleRef);
                }
                else
                {
                    state.Writer.Write((byte)PickleOperation.ModuleRef);
                    state.Writer.Write(module.Name);
                }
                Serialize(state, module.Assembly, MakeInfo(module.Assembly, typeof(Assembly)));
            }
        }

        private void SerializeType(PicklerSerializationState state, Type type, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            // This is a type, we need to emit a TypeRef or Def so it can be reconstructed

            // Constructed generic types are always handled the same way, we write out a GenericDef, the unconstructed generic type and then the generic arguments
            if (type.IsConstructedGenericType)
            {
                state.Writer.Write((byte)PickleOperation.GenericInstantiation);
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                Serialize(state, genericTypeDefinition, MakeInfo(genericTypeDefinition, typeof(Type), true));
                state.Writer.Write7BitEncodedInt(type.GenericTypeArguments.Length);
                foreach (var arg in type.GenericTypeArguments)
                {
                    Serialize(state, arg, MakeInfo(arg, typeof(Type), true));
                }
            }

            // Arrays aren't simple generic types, we need to write out the rank and element type
            else if (type.IsArray)
            {
                state.Writer.Write((byte)PickleOperation.ArrayType);
                if (type.IsSZArray)
                {
                    state.Writer.Write((byte)0);
                }
                else
                {
                    state.Writer.Write((byte)type.GetArrayRank());
                }
                var elementType = type.GetElementType();
                Serialize(state, elementType, MakeInfo(elementType, typeof(Type), true));
            }

            else if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod != null)
                {
                    if (genericMethodParameters == null)
                    {
                        state.Writer.Write((byte)PickleOperation.GenericParameter);
                        Serialize(state, type.DeclaringMethod, MakeInfo(type.DeclaringMethod, typeof(MemberInfo), true));
                    }
                    else
                    {
                        state.Writer.Write((byte)PickleOperation.MVar);
                    }
                }
                else if (type.DeclaringType != null)
                {
                    if (genericTypeParameters == null)
                    {
                        state.Writer.Write((byte)PickleOperation.GenericParameter);
                        Serialize(state, type.DeclaringType, MakeInfo(type.DeclaringType, typeof(MemberInfo), true));
                    }
                    else
                    {
                        state.Writer.Write((byte)PickleOperation.TVar);
                    }
                }
                else
                {
                    throw new Exception($"'{type}' is a generic parameter but is not bound to a type or method");
                }
                state.Writer.Write7BitEncodedInt(type.GenericParameterPosition);
            }

            // Is this assembly one we should save by value?
            else if (PickleByValue(type.Assembly))
            {
                state.RunWithTrailers(() =>
                {
                    // This is an unreferanceable assembly so on the other side it will be a dynamic assembly and we need to rebuild types
                    state.Writer.Write((byte)PickleOperation.TypeDef);

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

                    if (type.IsEnum)
                    {
                        state.Writer.Write((byte)TypeDef.Enum);
                    }
                    else if (type.IsAssignableTo(typeof(Delegate)))
                    {
                        state.Writer.Write((byte)TypeDef.Delegate);
                    }
                    else if (type.IsValueType)
                    {
                        state.Writer.Write((byte)TypeDef.Struct);
                    }
                    else
                    {
                        state.Writer.Write((byte)TypeDef.Class);
                    }
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

                    if (type.DeclaringType != null)
                    {
                        Serialize(state, type.DeclaringType, MakeInfo(type.DeclaringType, typeof(Type), true));
                    }
                    else
                    {
                        Serialize(state, type.Module, MakeInfo(type.Module, typeof(Module)));
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

                        WriteCustomAttributes(state, type.CustomAttributes.ToArray());
                    }
                    else if (type.IsAssignableTo(typeof(Delegate)))
                    {
                        // delegates are a name, optionally generic parameters, a return type and parameter types
                        var invoke = type.GetMethod("Invoke");
                        System.Diagnostics.Debug.Assert(invoke != null, "GetMethod(\"Invoke\") unexpectedly returned null for a delegate type");

                        Serialize(state, invoke.ReturnType, MakeInfo(invoke.ReturnType, typeof(Type), true));
                        var parameters = invoke.GetParameters();
                        state.Writer.Write7BitEncodedInt(parameters.Length);
                        foreach (var parameter in parameters)
                        {
                            state.Writer.WriteNullableString(parameter.Name);
                            Serialize(state, parameter.ParameterType, MakeInfo(parameter.ParameterType, typeof(Type), true), genericParameters);
                        }
                    }
                    else
                    {
                        SerializeDef(state, type, genericParameters);
                    }
                });
            }
            else
            {
                // Just write out a refernce to the type
                state.Writer.Write((byte)PickleOperation.TypeRef);

                if (type.DeclaringType != null)
                {
                    Serialize(state, type.DeclaringType, MakeInfo(type.DeclaringType, typeof(Type), true));
                    state.Writer.Write(type.Name);
                }
                else
                {
                    Serialize(state, type.Module, MakeInfo(type.Module, typeof(Module)));
                    if (string.IsNullOrEmpty(type.Namespace))
                    {
                        state.Writer.Write(type.Name);
                    }
                    else
                    {
                        state.Writer.Write(type.Namespace + "." + type.Name);
                    }
                }
            }
        }

        private void SerializeFieldInfo(PicklerSerializationState state, SerializeInformation info, FieldInfo field)
        {
            state.RunWithTrailers(() =>
            {
                System.Diagnostics.Debug.Assert(info.ContextType == null || info.ContextType == field.ReflectedType);

                if (info.ContextType == null)
                {
                    Serialize(state, field.ReflectedType, MakeInfo(field.ReflectedType, typeof(Type), true));
                }

                state.Writer.Write(field.Name);
            });
        }

        private void SerializePropertyInfo(PicklerSerializationState state, SerializeInformation info, PropertyInfo property)
        {
            state.RunWithTrailers(() =>
            {
                System.Diagnostics.Debug.Assert(info.ContextType == null || info.ContextType == property.ReflectedType);

                if (info.ContextType == null)
                {
                    Serialize(state, property.ReflectedType, MakeInfo(property.ReflectedType, typeof(Type), true));
                }

                SerializeSignature(state, Signature.GetSignature(property));
            });
        }

        private void SerializeEventInfo(PicklerSerializationState state, SerializeInformation info, EventInfo evt)
        {
            state.RunWithTrailers(() =>
            {
                System.Diagnostics.Debug.Assert(info.ContextType == null || info.ContextType == evt.ReflectedType);

                if (info.ContextType == null)
                {
                    Serialize(state, evt.ReflectedType, MakeInfo(evt.ReflectedType, typeof(Type), true));
                }

                state.Writer.Write(evt.Name);
            });
        }

        private void SerializeMethodInfo(PicklerSerializationState state, SerializeInformation info, MethodInfo method)
        {
            state.RunWithTrailers(() =>
            {
                System.Diagnostics.Debug.Assert(info.ContextType == null || info.ContextType == method.ReflectedType);

                if (info.ContextType == null)
                {
                    Serialize(state, method.ReflectedType, MakeInfo(method.ReflectedType, typeof(Type), true));
                }

                if (method.IsConstructedGenericMethod)
                {
                    var genericArguments = method.GetGenericArguments();
                    SerializeSignature(state, Signature.GetSignature(method.GetGenericMethodDefinition()));
                    state.Writer.Write7BitEncodedInt(genericArguments.Length);
                    foreach (var generic in genericArguments)
                    {
                        Serialize(state, generic, MakeInfo(generic, typeof(Type), true));
                    }
                }
                else
                {
                    SerializeSignature(state, Signature.GetSignature(method));
                    state.Writer.Write7BitEncodedInt(0);
                }
            });
        }

        private void SerializeConstructorInfo(PicklerSerializationState state, SerializeInformation info, ConstructorInfo constructor)
        {
            state.RunWithTrailers(() =>
            {
                System.Diagnostics.Debug.Assert(info.ContextType == null || info.ContextType == constructor.ReflectedType);

                if (info.ContextType == null)
                {
                    Serialize(state, constructor.ReflectedType, MakeInfo(constructor.ReflectedType, typeof(Type), true));
                }

                SerializeSignature(state, Signature.GetSignature(constructor));
            });
        }

        private void SerializeMulticastDelegate(PicklerSerializationState state, MulticastDelegate multicastDelegate, Type runtimeType)
        {
            // Delegates are just a target and a method
            var invocationList = multicastDelegate.GetInvocationList();
            Serialize(state, runtimeType, MakeInfo(runtimeType, typeof(Type), true));
            state.Writer.Write7BitEncodedInt(invocationList.Length);
            foreach (var invocation in invocationList)
            {
                Serialize(state, invocation.Target, MakeInfo(invocation.Target, typeof(object), true));
                Serialize(state, invocation.Method, MakeInfo(invocation.Method, typeof(MethodInfo), true));
            }
        }

        private void SerializeTuple(PicklerSerializationState state, System.Runtime.CompilerServices.ITuple tuple, Type staticType, Type[]? genericArguments)
        {
            // No need to write out the static types if we know them
            if (!IsTupleType(staticType))
            {
                if (genericArguments == null)
                {
                    // This must be an empty value tuple just write out a null params and return
                    state.Writer.Write((byte)PickleOperation.Null);
                    return;
                }

                if (tuple.Length > byte.MaxValue)
                {
                    throw new NotImplementedException($"Pikala does not support tuples of length higher than {byte.MaxValue}, got {tuple.Length}");
                }

                // Write out the static types
                Serialize(state, genericArguments, MakeInfo(genericArguments, typeof(Type[]), true));
            }

            // Write out the values
            for (int i = 0; i < tuple.Length; ++i)
            {
                System.Diagnostics.Debug.Assert(genericArguments != null, "genericArguments was null for a non-empty tuple");

                var item = tuple[i];
                Serialize(state, item, MakeInfo(item, genericArguments[i]));
            }
        }

        private void SerializeReducer(PicklerSerializationState state, object obj, IReducer reducer, Type runtimeType)
        {
            // We've got a reducer for the type (or its generic variant)
            var (method, target, args) = reducer.Reduce(runtimeType, obj);

            Serialize(state, method, MakeInfo(method, typeof(MethodBase), true));

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

                Serialize(state, target, MakeInfo(target, typeof(object), true));
            }
            else
            {
                throw new Exception($"Invalid reduction for type '{runtimeType}'. MethodBase was '{method}'.");
            }

            state.Writer.Write7BitEncodedInt(args.Length);
            foreach (var arg in args)
            {
                Serialize(state, arg, MakeInfo(arg, typeof(object), true));
            }
        }

        private void SerializeISerializable(PicklerSerializationState state, System.Runtime.Serialization.ISerializable iserializable, SerializeInformation info)
        {
            // ISerializable objects call into GetObjectData and will reconstruct with the (SerializationInfo, StreamingContext) constructor

            var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
            var serializationInfo = new System.Runtime.Serialization.SerializationInfo(info.RuntimeType, new System.Runtime.Serialization.FormatterConverter());
            iserializable.GetObjectData(serializationInfo, context);

            Serialize(state, info.RuntimeType, MakeInfo(info.RuntimeType, typeof(Type), true));
            state.Writer.Write7BitEncodedInt(serializationInfo.MemberCount);
            foreach (var member in serializationInfo)
            {
                state.Writer.Write(member.Name);
                Serialize(state, member.Value, MakeInfo(member.Value, typeof(object), true));
            }
        }

        private void SerializeObject(PicklerSerializationState state, object obj, SerializeInformation info, Tuple<ValueTuple<string, Type>[], FieldInfo[]> fields)
        {
            // Must be an object, try and dump all it's fields
            Serialize(state, info.RuntimeType, MakeInfo(info.RuntimeType, typeof(Type), true));

            var (namesAndTypes, fieldInfos) = fields;

            // If we've seen this type before we don't need to write out it's fields again
            if (!state.SeenType(info.RuntimeType))
            {
                Serialize(state, namesAndTypes, MakeInfo(namesAndTypes, typeof(ValueTuple<string, Type>[]), true));
            }

            foreach (var field in fieldInfos)
            {
                var value = field.GetValue(obj);
                Serialize(state, value, MakeInfo(value, field.FieldType));
            }
        }

        private OperationCacheEntry GetOperation(Type runtimeType)
        {
            OperationCacheEntry GetOperation(Type runtimeType)
            {
                // Rest of the operations will need the type of obj
                var typeCode = Type.GetTypeCode(runtimeType);

                if (runtimeType.IsEnum)
                {
                    return new OperationCacheEntry(typeCode, PickleOperation.Enum);
                }

                switch (typeCode)
                {
                    case TypeCode.Boolean: return new OperationCacheEntry(typeCode, PickleOperation.Boolean);
                    case TypeCode.Char: return new OperationCacheEntry(typeCode, PickleOperation.Char);
                    case TypeCode.SByte: return new OperationCacheEntry(typeCode, PickleOperation.SByte);
                    case TypeCode.Int16: return new OperationCacheEntry(typeCode, PickleOperation.Int16);
                    case TypeCode.Int32: return new OperationCacheEntry(typeCode, PickleOperation.Int32);
                    case TypeCode.Int64: return new OperationCacheEntry(typeCode, PickleOperation.Int64);
                    case TypeCode.Byte: return new OperationCacheEntry(typeCode, PickleOperation.Byte);
                    case TypeCode.UInt16: return new OperationCacheEntry(typeCode, PickleOperation.UInt16);
                    case TypeCode.UInt32: return new OperationCacheEntry(typeCode, PickleOperation.UInt32);
                    case TypeCode.UInt64: return new OperationCacheEntry(typeCode, PickleOperation.UInt64);
                    case TypeCode.Single: return new OperationCacheEntry(typeCode, PickleOperation.Single);
                    case TypeCode.Double: return new OperationCacheEntry(typeCode, PickleOperation.Double);
                    case TypeCode.Decimal: return new OperationCacheEntry(typeCode, PickleOperation.Decimal);
                    case TypeCode.DBNull: return new OperationCacheEntry(typeCode, PickleOperation.DBNull);
                    case TypeCode.String: return new OperationCacheEntry(typeCode, PickleOperation.String);
                    // Let DateTime just be handled by ISerializable
                    case TypeCode.DateTime:
                    case TypeCode.Object:
                        {
                            // Most pointers we'll reject but we special case IntPtr and UIntPtr as they're often
                            // used for native sized numbers
                            if (runtimeType.IsPointer || runtimeType == typeof(Pointer))
                            {
                                throw new Exception($"Pointer types are not serializable: '{runtimeType}'");
                            }

                            if (runtimeType.IsArray)
                            {
                                if (runtimeType.IsSZArray)
                                {
                                    return new OperationCacheEntry(typeCode, PickleOperation.SZArray);
                                }
                                else
                                {
                                    return new OperationCacheEntry(typeCode, PickleOperation.Array);
                                }
                            }

                            // This check needs to come before IsValueType, because these
                            // are also value types.
                            if (runtimeType == typeof(IntPtr))
                            {
                                return new OperationCacheEntry(typeCode, PickleOperation.IntPtr);
                            }
                            if (runtimeType == typeof(UIntPtr))
                            {
                                return new OperationCacheEntry(typeCode, PickleOperation.UIntPtr);
                            }

                            // Reflection
                            if (runtimeType.IsAssignableTo(typeof(Assembly)))
                            {
                                // We only support serialising the actual runtime assembly type (either a real runtime assembly, or an assemblybuilder)
                                if (runtimeType.IsAssignableTo(runtimeAssemblyType) || runtimeType == runtimeAssemblyBuilderType)
                                {
                                    return new OperationCacheEntry(typeCode, OperationGroup.Assembly);
                                }
                                else
                                {
                                    throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from Assembly.");
                                }
                            }
                            if (runtimeType.IsAssignableTo(typeof(Module)))
                            {
                                if (runtimeType.IsAssignableTo(runtimeModuleType) || runtimeType == runtimeModuleBuilderType)
                                {
                                    return new OperationCacheEntry(typeCode, OperationGroup.Module);
                                }
                                else
                                {
                                    throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from Module.");
                                }
                            }
                            if (runtimeType.IsAssignableTo(typeof(MemberInfo)))
                            {
                                if (runtimeType.IsAssignableTo(typeof(Type)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimeTypeType))
                                    {
                                        return new OperationCacheEntry(typeCode, OperationGroup.Type);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from Type.");
                                    }
                                }
                                else if (runtimeType.IsAssignableTo(typeof(FieldInfo)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimeFieldInfoType))
                                    {
                                        return new OperationCacheEntry(typeCode, PickleOperation.FieldRef);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from FieldInfo.");
                                    }
                                }
                                else if (runtimeType.IsAssignableTo(typeof(PropertyInfo)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimePropertyInfoType))
                                    {
                                        return new OperationCacheEntry(typeCode, PickleOperation.PropertyRef);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from PropertyInfo.");
                                    }
                                }
                                else if (runtimeType.IsAssignableTo(typeof(EventInfo)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimeEventInfoType))
                                    {
                                        return new OperationCacheEntry(typeCode, PickleOperation.EventRef);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from EventInfo.");
                                    }
                                }
                                else if (runtimeType.IsAssignableTo(typeof(MethodInfo)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimeMethodInfoType))
                                    {
                                        return new OperationCacheEntry(typeCode, PickleOperation.MethodRef);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from MethodInfo.");
                                    }
                                }
                                else if (runtimeType.IsAssignableTo(typeof(ConstructorInfo)))
                                {
                                    if (runtimeType.IsAssignableTo(runtimeConstructorInfoType))
                                    {
                                        return new OperationCacheEntry(typeCode, PickleOperation.ConstructorRef);
                                    }
                                    else
                                    {
                                        throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from ConstructorInfo.");
                                    }
                                }
                                else
                                {
                                    throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from MemberInfo.");
                                }
                            }
                            // End of reflection handlers

                            if (runtimeType.IsAssignableTo(typeof(MulticastDelegate)))
                            {
                                return new OperationCacheEntry(typeCode, PickleOperation.Delegate);
                            }

                            // Tuples!
                            if (IsTupleType(runtimeType))
                            {
                                if (runtimeType.IsValueType)
                                {
                                    return new OperationCacheEntry(typeCode, true, runtimeType.IsGenericType ? runtimeType.GetGenericArguments() : null);
                                }
                                else
                                {
                                    return new OperationCacheEntry(typeCode, false, runtimeType.GetGenericArguments());
                                }
                            }

                            if (_reducers.TryGetValue(runtimeType, out var reducer) || (runtimeType.IsGenericType && _reducers.TryGetValue(runtimeType.GetGenericTypeDefinition(), out reducer)))
                            {
                                return new OperationCacheEntry(typeCode, reducer);
                            }

                            if (runtimeType.IsAssignableTo(typeof(System.Runtime.Serialization.ISerializable)))
                            {
                                return new OperationCacheEntry(typeCode, PickleOperation.ISerializable);
                            }

                            if (runtimeType.IsAssignableTo(typeof(MarshalByRefObject)))
                            {
                                throw new Exception($"Type '{runtimeType}' is not automaticly serializable as it inherits from MarshalByRefObject.");
                            }

                            return new OperationCacheEntry(typeCode, GetSerializedFields(runtimeType));
                        }
                }

                throw new Exception($"Unhandled TypeCode '{typeCode}' for type '{runtimeType}'");
            }

            if (!_operationCache.TryGetValue(runtimeType, out var operationEntry))
            {
                operationEntry = GetOperation(runtimeType);
                _operationCache.Add(runtimeType, operationEntry);
            }

            return operationEntry;
        }

        private void Serialize(PicklerSerializationState state, object? obj, SerializeInformation info, Type[]? genericTypeParameters = null, Type[]? genericMethodParameters = null)
        {
            if (Object.ReferenceEquals(obj, null))
            {
                state.Writer.Write((byte)PickleOperation.Null);
                return;
            }
            else if (info.ShouldMemo && state.DoMemo(obj))
            {
                return;
            }


            var operationEntry = GetOperation(info.RuntimeType);
            switch (operationEntry.Group)
            {
                case OperationGroup.FullyKnown:
                    System.Diagnostics.Debug.Assert(operationEntry.Operation.HasValue);
                    var operation = operationEntry.Operation.Value;
                    // This is exactly the same method we use when deserialising, if we can infer the operation from the static type we
                    // don't write out operation tokens (and some other info like type refs)
                    var inferedOperationToken = InferOperationFromStaticType(null, info.StaticType);
                    if (inferedOperationToken.HasValue)
                    {
                        System.Diagnostics.Debug.Assert(inferedOperationToken.Value == operation, "Infered operation from static type didn't match intended operation");
                        // If we've infered the operation we can't be memoising this value because we're skipping writing out the op token that could tell us to memoise
                        System.Diagnostics.Debug.Assert(!info.ShouldMemo, "Infered an operation for a memoizable type");
                    }
                    else
                    {
                        // Set the high bit for operations that shouldn't memo
                        var opByte = (int)operation | (info.ShouldMemo ? 0 : 0x80);
                        state.Writer.Write((byte)opByte);
                    }

                    switch (operation)
                    {
                        case PickleOperation.Enum:
                            System.Diagnostics.Debug.Assert(info.RuntimeType.IsEnum, "Trying to enum serialise a type that is not an enum");

                            // StaticType will be object/ValueType or Nullable or the enum type (Anything else is a bug)
                            // If this is the enum type (or nullable<enumType>) we can skip writing out the type token
                            // iff the enum type is statically final
                            bool needTypeToken;
                            if (info.StaticType == typeof(object) || info.StaticType == typeof(ValueType))
                            {
                                needTypeToken = true;
                            }
                            else if (info.StaticType == info.RuntimeType)
                            {
                                needTypeToken = !IsStaticallyFinal(null, info.StaticType);
                            }
                            else
                            {
                                System.Diagnostics.Debug.Assert(info.StaticType.Name == "Nullable`1", "Expected static type for enum to be Nullable<T>", "But was {0}", info.StaticType);
                                var genericArguments = info.StaticType.GetGenericArguments();
                                System.Diagnostics.Debug.Assert(genericArguments.Length == 1, "Expected Nullable<T> to have one generic argument");
                                var genericArgument = genericArguments[0];
                                System.Diagnostics.Debug.Assert(genericArgument == info.RuntimeType, "Expected T of Nullable<T> to match enum type");
                                needTypeToken = !IsStaticallyFinal(null, info.RuntimeType);
                            }

                            if (needTypeToken)
                            {
                                Serialize(state, info.RuntimeType, MakeInfo(info.RuntimeType, typeof(Type), true));
                            }
                            // typeCode for an enum will be something like Int32
                            WriteEnumerationValue(state.Writer, operationEntry.TypeCode, obj);
                            return;

                        case PickleOperation.Boolean:
                            state.Writer.Write((bool)obj);
                            return;
                        case PickleOperation.Char:
                            state.Writer.Write((char)obj);
                            return;
                        case PickleOperation.SByte:
                            state.Writer.Write((sbyte)obj);
                            return;
                        case PickleOperation.Int16:
                            state.Writer.Write((short)obj);
                            return;
                        case PickleOperation.Int32:
                            state.Writer.Write((int)obj);
                            return;
                        case PickleOperation.Int64:
                            state.Writer.Write((long)obj);
                            return;
                        case PickleOperation.Byte:
                            state.Writer.Write((byte)obj);
                            return;
                        case PickleOperation.UInt16:
                            state.Writer.Write((ushort)obj);
                            return;
                        case PickleOperation.UInt32:
                            state.Writer.Write((uint)obj);
                            return;
                        case PickleOperation.UInt64:
                            state.Writer.Write((ulong)obj);
                            return;
                        case PickleOperation.Single:
                            state.Writer.Write((float)obj);
                            return;
                        case PickleOperation.Double:
                            state.Writer.Write((double)obj);
                            return;
                        case PickleOperation.Decimal:
                            state.Writer.Write((decimal)obj);
                            return;
                        case PickleOperation.DBNull:
                            return;
                        case PickleOperation.String:
                            state.Writer.Write((string)obj);
                            return;
                        case PickleOperation.IntPtr:
                            state.Writer.Write((long)(IntPtr)obj);
                            return;
                        case PickleOperation.UIntPtr:
                            state.Writer.Write((ulong)(UIntPtr)obj);
                            return;
                        case PickleOperation.Array:
                        case PickleOperation.SZArray:
                            SerializeArray(state, (Array)obj, info.RuntimeType);
                            return;
                        case PickleOperation.FieldRef:
                            SerializeFieldInfo(state, info, (FieldInfo)obj);
                            return;
                        case PickleOperation.PropertyRef:
                            SerializePropertyInfo(state, info, (PropertyInfo)obj);
                            return;
                        case PickleOperation.EventRef:
                            SerializeEventInfo(state, info, (EventInfo)obj);
                            return;
                        case PickleOperation.MethodRef:
                            SerializeMethodInfo(state, info, (MethodInfo)obj);
                            return;
                        case PickleOperation.ConstructorRef:
                            SerializeConstructorInfo(state, info, (ConstructorInfo)obj);
                            return;
                        case PickleOperation.Delegate:
                            SerializeMulticastDelegate(state, (MulticastDelegate)obj, info.RuntimeType);
                            return;
                        case PickleOperation.Tuple:
                        case PickleOperation.ValueTuple:
                            SerializeTuple(state, (System.Runtime.CompilerServices.ITuple)obj, info.StaticType, operationEntry.GenericArguments);
                            return;
                        case PickleOperation.ISerializable:
                            SerializeISerializable(state, (System.Runtime.Serialization.ISerializable)obj, info);
                            return;
                        case PickleOperation.Reducer:
                            System.Diagnostics.Debug.Assert(operationEntry.Reducer != null);
                            SerializeReducer(state, obj, operationEntry.Reducer, info.RuntimeType);
                            return;
                        case PickleOperation.Object:
                            System.Diagnostics.Debug.Assert(operationEntry.Fields != null);
                            SerializeObject(state, obj, info, operationEntry.Fields);
                            return;

                        default:
                            throw new Exception($"Unexpected operation {operationEntry.Operation} for a fully known operation");
                    }

                case OperationGroup.Assembly:
                    SerializeAssembly(state, (Assembly)obj);
                    return;
                case OperationGroup.Module:
                    SerializeModule(state, (Module)obj);
                    return;
                case OperationGroup.Type:
                    SerializeType(state, (Type)obj, genericTypeParameters, genericMethodParameters);
                    return;

            }

            throw new Exception($"Unhandled OperationGroup '{operationEntry.Group}' for type '{info.RuntimeType}'");
        }

        public void Serialize(Stream stream, object? rootObject)
        {
            var state = new PicklerSerializationState(stream);

            // Always start the pickler stream with a header for sanity checking inputs
            state.Writer.Write(_header);
            state.Writer.Write(_version);

            Serialize(state, rootObject, MakeInfo(rootObject, typeof(object)));
            state.CheckTrailers();
        }
    }
}
