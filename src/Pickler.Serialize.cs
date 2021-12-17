using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    public struct SerializeInformation
    {
        public Type RuntimeType { get; }
        public Type StaticType { get; }
        public bool ShouldMemo { get; }

        public SerializeInformation(Type? runtimeType, Type staticType, bool shouldMemo)
        {
            RuntimeType = runtimeType ?? typeof(object);
            StaticType = staticType;
            ShouldMemo = shouldMemo;
        }

        public bool NeedsOperationToken { get { return RuntimeType != StaticType; } }
    }

    public sealed partial class Pickler
    {
        /// <summary>
        /// There are some objects that we shouldn't bother to memoise because it's cheaper to just write their tokens.
        /// </summary>
        private bool ShouldMemo(object obj, Type staticType)
        {
            // If the static type is a value type we shouldn't memo because this is a value not a reference
            if (staticType.IsValueType) { return false; }

            // mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib)) { return false; }

            // The manifest module for mscorlib gets saved as two tokens, no worse than a memo and probably better
            if (Object.ReferenceEquals(obj, mscorlib.ManifestModule)) { return false; }

            return true;
        }

        private SerializeInformation MakeInfo(object? obj, Type staticType, bool? shouldMemo = null)
        {
            if (obj == null) { return new SerializeInformation(typeof(object), staticType, false); }

            if (!shouldMemo.HasValue)
            {
                shouldMemo = ShouldMemo(obj, staticType);
            }

            return new SerializeInformation(obj.GetType(), staticType, shouldMemo.Value);
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

        private void SerializeConstructorHeader(PicklerSerializationState state, Type[]? genericTypeParameters, ConstructorInfo constructor)
        {
            state.Writer.Write((int)constructor.Attributes);
            state.Writer.Write((int)constructor.CallingConvention);

            var constructorParameters = constructor.GetParameters();
            state.Writer.Write7BitEncodedInt(constructorParameters.Length);
            foreach (var parameter in constructorParameters)
            {
                SerializeType(state, parameter.ParameterType, genericTypeParameters);
            }
            foreach (var parameter in constructorParameters)
            {
                state.Writer.WriteNullableString(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);
            }

            WriteCustomAttributes(state, constructor.CustomAttributes.ToArray());

            var methodBody = constructor.GetMethodBody();

            state.Writer.Write(methodBody.InitLocals);

            state.Writer.Write7BitEncodedInt(methodBody.LocalVariables.Count);
            foreach (var local in methodBody.LocalVariables)
            {
                SerializeType(state, local.LocalType, genericTypeParameters);
            }

            var collectedTypes = CollectTypes(genericTypeParameters, constructor.Module, null, methodBody);
            state.Writer.Write7BitEncodedInt(collectedTypes.Count);
            foreach (var type in collectedTypes)
            {
                SerializeType(state, type, genericTypeParameters);
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

            SerializeType(state, method.ReturnType, genericTypeParameters, genericMethodParameters);

            var methodParameters = method.GetParameters();
            state.Writer.Write7BitEncodedInt(methodParameters.Length);
            foreach (var parameter in methodParameters)
            {
                SerializeType(state, parameter.ParameterType, genericTypeParameters, genericMethodParameters);
            }
            foreach (var parameter in methodParameters)
            {
                state.Writer.Write(parameter.Name);
                state.Writer.Write((int)parameter.Attributes);
            }

            WriteCustomAttributes(state, method.CustomAttributes.ToArray());

            if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) || method.Attributes.HasFlag(MethodAttributes.UnmanagedExport) || method.Attributes.HasFlag(MethodAttributes.Abstract))
            {

            }
            else
            {
                var methodBody = method.GetMethodBody();

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
                            types.Add(fieldInfo.DeclaringType);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodToken = ilReader.ReadInt32();
                            var methodInfo = methodModule.ResolveMethod(methodToken, genericTypeParameters, genericMethodParameters);
                            types.Add(methodInfo.DeclaringType);
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var memberToken = ilReader.ReadInt32();
                            var memberInfo = methodModule.ResolveMember(memberToken, genericTypeParameters, genericMethodParameters);
                            types.Add(memberInfo.DeclaringType);
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
                            SerializeType(state, typeInfo, genericTypeParameters, genericMethodParameters);
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

            WriteCustomAttributes(state, module.CustomAttributes.ToArray());

            var fields = module.GetFields();
            state.Writer.Write7BitEncodedInt(fields.Length);
            foreach (var field in fields)
            {
                state.Writer.Write(field.Name);
                state.Writer.Write((int)field.Attributes);
                SerializeType(state, field.FieldType);
            }

            var methods = module.GetMethods();
            state.Writer.Write7BitEncodedInt(methods.Length);
            foreach (var method in methods)
            {
                SerializeMethodHeader(state, null, method);
            }

            state.PushTrailer(() =>
            {
                foreach (var method in methods)
                {
                    SerializeMethodBody(state, null, method.Module, method.GetGenericArguments(), method.GetMethodBody());
                }
            }, () =>
            {
                foreach (var field in fields)
                {
                    state.Writer.Write(field.Name);
                    var value = field.GetValue(null);
                    var fieldInfo = MakeInfo(value, typeof(object), ShouldMemo(value, field.FieldType));
                    Serialize(state, value, fieldInfo);
                }
            });
        }

        private void SerializeTypeDef(PicklerSerializationState state, Type type, Type[]? genericParameters)
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
                SerializeType(state, type.BaseType, genericParameters);
            }

            var interfaces = type.GetInterfaces();
            state.Writer.Write7BitEncodedInt(interfaces.Length);
            foreach (var interfaceType in interfaces)
            {
                SerializeType(state, interfaceType, genericParameters);

                var interfaceMap = type.GetInterfaceMap(interfaceType);
                var mappedMethods = new List<(string, string)>();
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
                        var interfaceMethodSignature = Method.GetSignature(interfaceMap.InterfaceMethods[i]);
                        var targetMethodSignature = Method.GetSignature(targetMethod);
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
                    state.Writer.Write(interfaceMethod);
                    state.Writer.Write(targetMethod);
                }
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            state.Writer.Write7BitEncodedInt(fields.Length);
            foreach (var field in fields)
            {
                state.Writer.Write(field.Name);
                state.Writer.Write((int)field.Attributes);
                SerializeType(state, field.FieldType, genericParameters);
                WriteCustomAttributes(state, field.CustomAttributes.ToArray());
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
                SerializeType(state, property.PropertyType, genericParameters);
                var indexParameters = property.GetIndexParameters();
                state.Writer.Write7BitEncodedInt(indexParameters.Length);
                foreach (var indexParameter in indexParameters)
                {
                    SerializeType(state, indexParameter.ParameterType, genericParameters);
                }
                WriteCustomAttributes(state, property.CustomAttributes.ToArray());

                if (property.GetMethod == null)
                {
                    state.Writer.WriteNullableString(null);
                }
                else
                {
                    state.Writer.WriteNullableString(Method.GetSignature(property.GetMethod));
                }

                if (property.SetMethod == null)
                {
                    state.Writer.WriteNullableString(null);
                }
                else
                {
                    state.Writer.WriteNullableString(Method.GetSignature(property.SetMethod));
                }
            }

            // Custom attributes might be self referencing so make sure all ctors and things are setup first
            WriteCustomAttributes(state, type.CustomAttributes.ToArray());

            state.PushTrailer(() =>
            {
                foreach (var constructor in constructors)
                {
                    SerializeMethodBody(state, genericParameters, constructor.Module, null, constructor.GetMethodBody());
                }
                foreach (var method in methods)
                {
                    var methodBody = method.GetMethodBody();
                    if (methodBody != null)
                    {
                        SerializeMethodBody(state, genericParameters, method.Module, method.GetGenericArguments(), methodBody);
                    }
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

            // Special case szarray (i.e. Rank 1, lower bound 0)
            if (objType.IsSZArray)
            {
                state.Writer.Write((byte)PickleOperation.SZArray);
            }
            else
            {
                state.Writer.Write((byte)PickleOperation.Array);
            }

            var elementType = objType.GetElementType();
            SerializeType(state, elementType);

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
                unsafe
                {
                    var arrayHandle = System.Runtime.InteropServices.GCHandle.Alloc(obj, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        var pin = arrayHandle.AddrOfPinnedObject();
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

                        while (byteCount > 0)
                        {
                            // Write 4k at a time
                            var bufferSize = 4096L;
                            var length = (int)(byteCount < bufferSize ? byteCount : bufferSize);

                            var span = new ReadOnlySpan<byte>(pin.ToPointer(), length);
                            state.Writer.Write(span);

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
                foreach (var item in obj)
                {
                    // TODO If we know all items are the same type we can save calling MakeInfo on each one
                    Serialize(state, item, MakeInfo(item, elementType));
                }
            }
        }

        private void WriteCustomAttributeValue(PicklerSerializationState state, object value, SerializeInformation info)
        {
            // argument might be a ReadOnlyCollection[CustomAttributeTypedArgument] but we should write that as just an array of values
            if (value is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> collection)
            {
                var result = new object[collection.Count];
                for (int i = 0; i < result.Length; ++i)
                {
                    result[i] = collection[i].Value;
                }
                // No point memoising this array, we just created it!
                Serialize(state, result, new SerializeInformation(typeof(object[]), typeof(object[]), false));
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
                Serialize(state, attribute.Constructor, MakeInfo(attribute.Constructor, typeof(ConstructorInfo)));
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
                        Serialize(state, property, MakeInfo(property, typeof(PropertyInfo)));
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
                        Serialize(state, field, MakeInfo(field, typeof(FieldInfo)));
                        var value = argument.TypedValue.Value;
                        var info = MakeInfo(argument.TypedValue.Value, typeof(object), ShouldMemo(value, field.FieldType));
                        WriteCustomAttributeValue(state, value, info);
                    }
                }
            }
        }

        private void SerializeObject(PicklerSerializationState state, object obj, SerializeInformation info, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            // If we call this we know obj is not memoised or null or an enum 
            // or any of the types explictly in System.TypeCode

            IReducer reducer;

            if (info.RuntimeType.IsArray)
            {
                SerializeArray(state, (Array)obj, info.RuntimeType);
            }

            // This check needs to come before IsValueType, because these 
            // are also value types.
            else if (info.RuntimeType == typeof(IntPtr))
            {
                if (info.NeedsOperationToken)
                {
                    state.Writer.Write((byte)PickleOperation.IntPtr);
                }
                state.Writer.Write((long)(IntPtr)obj);
            }
            else if (info.RuntimeType == typeof(UIntPtr))
            {
                if (info.NeedsOperationToken)
                {
                    state.Writer.Write((byte)PickleOperation.UIntPtr);
                }
                state.Writer.Write((ulong)(UIntPtr)obj);
            }

            // Reflection
            else if (info.RuntimeType.IsAssignableTo(typeof(Assembly)))
            {
                // This is an assembly, we need to emit an assembly name so it can be reloaded
                var assembly = (Assembly)obj;

                // Is this mscorlib? If so we write out a single token for it
                if (assembly == mscorlib)
                {
                    state.Writer.Write((byte)PickleOperation.Mscorlib);
                }
                // Is this assembly one we should save by value?
                else if (PickleByValue(assembly))
                {
                    // Write out an assembly definition, we'll build a dynamic assembly for this on the other side
                    state.Writer.Write((byte)PickleOperation.AssemblyDef);
                    state.Writer.Write(assembly.FullName);
                    WriteCustomAttributes(state, assembly.CustomAttributes.ToArray());
                }
                else
                {
                    // Just write out an assembly refernce
                    state.Writer.Write((byte)PickleOperation.AssemblyRef);
                    state.Writer.Write(assembly.FullName);
                }
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(Module)))
            {
                // This is a module, we need to emit a reference to the assembly it's found in and it's name
                var module = (Module)obj;

                // Is this assembly one we should save by value?
                if (PickleByValue(module.Assembly))
                {
                    SerializeModuleDef(state, module);
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

            else if (info.RuntimeType.IsAssignableTo(typeof(Type)))
            {
                // This is a type, we need to emit a TypeRef or Def so it can be reconstructed
                var type = (Type)obj;

                // Constructed generic types are always handled the same way, we write out a GenericDef, the unconstructed generic type and then the generic arguments
                if (type.IsConstructedGenericType)
                {
                    state.Writer.Write((byte)PickleOperation.GenericInstantiation);
                    SerializeType(state, type.GetGenericTypeDefinition());
                    state.Writer.Write7BitEncodedInt(type.GenericTypeArguments.Length);
                    foreach (var arg in type.GenericTypeArguments)
                    {
                        SerializeType(state, arg);
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
                    SerializeType(state, type.GetElementType());
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
                            SerializeType(state, type.DeclaringType);
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
                                WriteEnumerationValue(state.Writer, typeCode, values.GetValue(i));
                            }

                            var customAttributes = type.CustomAttributes.ToArray();
                            WriteCustomAttributes(state, customAttributes);
                        }
                        else if (type.IsAssignableTo(typeof(Delegate)))
                        {
                            // delegates are a name, optionally generic parameters, a return type and parameter types
                            var invoke = type.GetMethod("Invoke");
                            SerializeType(state, invoke.ReturnType);
                            var parameters = invoke.GetParameters();
                            state.Writer.Write7BitEncodedInt(parameters.Length);
                            foreach (var parameter in parameters)
                            {
                                state.Writer.Write(parameter.Name);
                                SerializeType(state, parameter.ParameterType, genericParameters);
                            }
                        }
                        else
                        {
                            SerializeTypeDef(state, type, genericParameters);
                        }
                    });
                }
                else
                {
                    // Just write out a refernce to the type
                    state.Writer.Write((byte)PickleOperation.TypeRef);

                    if (type.DeclaringType != null)
                    {
                        SerializeType(state, type.DeclaringType);
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

            else if (info.RuntimeType.IsAssignableTo(typeof(FieldInfo)))
            {
                var field = (FieldInfo)obj;

                state.Writer.Write((byte)PickleOperation.FieldRef);
                SerializeType(state, field.ReflectedType);
                state.Writer.Write(field.Name);
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(PropertyInfo)))
            {
                var property = (PropertyInfo)obj;

                state.Writer.Write((byte)PickleOperation.PropertyRef);
                SerializeType(state, property.ReflectedType);
                state.Writer.Write(property.Name);
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(MethodInfo)))
            {
                var method = (MethodInfo)obj;

                state.Writer.Write((byte)PickleOperation.MethodRef);
                if (method.IsConstructedGenericMethod)
                {
                    var genericArguments = method.GetGenericArguments();
                    state.Writer.Write(Method.GetSignature(method.GetGenericMethodDefinition()));
                    state.Writer.Write7BitEncodedInt(genericArguments.Length);
                    foreach (var generic in genericArguments)
                    {
                        SerializeType(state, generic);
                    }
                }
                else
                {
                    state.Writer.Write(Method.GetSignature(method));
                    state.Writer.Write7BitEncodedInt(0);
                }
                SerializeType(state, method.ReflectedType);
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(ConstructorInfo)))
            {
                var method = (ConstructorInfo)obj;

                state.Writer.Write((byte)PickleOperation.ConstructorRef);
                state.Writer.Write(Method.GetSignature(method));
                SerializeType(state, method.ReflectedType);
            }

            // End of reflection handlers

            else if (info.RuntimeType.IsAssignableTo(typeof(MulticastDelegate)))
            {
                // Delegates are just a target and a method
                var dele = (MulticastDelegate)obj;
                var invocationList = dele.GetInvocationList();

                state.Writer.Write((byte)PickleOperation.Delegate);
                SerializeType(state, info.RuntimeType);
                state.Writer.Write7BitEncodedInt(invocationList.Length);
                foreach (var invocation in invocationList)
                {
                    Serialize(state, invocation.Target, MakeInfo(invocation.Target, typeof(object), true));
                    Serialize(state, invocation.Method, MakeInfo(invocation.Method, typeof(MethodInfo), true));
                }
            }

            // Tuples!

            else if (info.RuntimeType.Assembly == mscorlib && (info.RuntimeType.FullName.StartsWith("System.Tuple") || info.RuntimeType.FullName.StartsWith("System.ValueTuple")))
            {
                var tuple = obj as System.Runtime.CompilerServices.ITuple;

                if (info.RuntimeType.FullName.StartsWith("System.Tuple"))
                {
                    state.Writer.Write((byte)PickleOperation.Tuple);
                }
                else
                {
                    state.Writer.Write((byte)PickleOperation.ValueTuple);
                }

                if (tuple.Length > byte.MaxValue)
                {
                    throw new NotImplementedException($"Pikala does not support tuples of length higher than {byte.MaxValue}, got {tuple.Length}");
                }

                state.Writer.Write((byte)tuple.Length);

                // Write out the static types
                var genericArguments = info.RuntimeType.GetGenericArguments();
                for (int i = 0; i < tuple.Length; ++i)
                {
                    SerializeType(state, genericArguments[i], genericTypeParameters, genericMethodParameters);
                }

                // Write out the values
                for (int i = 0; i < tuple.Length; ++i)
                {
                    var item = tuple[i];
                    var itemInfo = new SerializeInformation(item?.GetType(), genericArguments[i], !genericArguments[i].IsValueType);
                    Serialize(state, item, itemInfo, genericTypeParameters, genericMethodParameters);
                }
            }

            else if (_reducers.TryGetValue(info.RuntimeType, out reducer) || (info.RuntimeType.IsGenericType && _reducers.TryGetValue(info.RuntimeType.GetGenericTypeDefinition(), out reducer)))
            {
                // We've got a reducer for the type (or its generic variant)
                var (method, target, args) = reducer.Reduce(info.RuntimeType, obj);

                state.Writer.Write((byte)PickleOperation.Reducer);
                Serialize(state, method, MakeInfo(method, typeof(MethodBase), true));

                // Assert properties of the reduction
                if (method is ConstructorInfo constructorInfo)
                {
                    if (target != null)
                    {
                        throw new Exception($"Invalid reduction for type '{info.RuntimeType}'. MethodBase was a ConstructorInfo but Target was not null.");
                    }

                    if (constructorInfo.DeclaringType != info.RuntimeType)
                    {
                        throw new Exception($"Invalid reduction for type '{info.RuntimeType}'. MethodBase was a ConstructorInfo for '{constructorInfo.DeclaringType}'.");
                    }

                    // We don't write target for ConstructorInfo, it must be null.
                }
                else if (method is MethodInfo methodInfo)
                {
                    if (methodInfo.ReturnType != info.RuntimeType)
                    {
                        throw new Exception($"Invalid reduction for type '{info.RuntimeType}'. MethodBase was a MethodInfo that returns '{methodInfo.ReturnType}'.");
                    }

                    Serialize(state, target, MakeInfo(target, typeof(object), true));
                }
                else
                {
                    throw new Exception($"Invalid reduction for type '{info.RuntimeType}'. MethodBase was '{method}'.");
                }

                state.Writer.Write7BitEncodedInt(args.Length);
                foreach (var arg in args)
                {
                    Serialize(state, arg, MakeInfo(arg, typeof(object), true), genericTypeParameters, genericMethodParameters);
                }
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(System.Runtime.Serialization.ISerializable)))
            {
                // ISerializable objects call into GetObjectData and will reconstruct with the (SerializationInfo, StreamingContext) constructor

                var iserializable = (System.Runtime.Serialization.ISerializable)obj;

                var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
                var serializationInfo = new System.Runtime.Serialization.SerializationInfo(info.RuntimeType, new System.Runtime.Serialization.FormatterConverter());
                iserializable.GetObjectData(serializationInfo, context);

                state.Writer.Write((byte)PickleOperation.ISerializable);
                if (!info.StaticType.IsValueType || info.StaticType != info.RuntimeType)
                {
                    SerializeType(state, info.RuntimeType);
                }
                state.Writer.Write7BitEncodedInt(serializationInfo.MemberCount);
                foreach (var member in serializationInfo)
                {
                    state.Writer.Write(member.Name);
                    Serialize(state, member.Value, MakeInfo(member.Value, typeof(object), true));
                }
            }

            else if (info.RuntimeType.IsAssignableTo(typeof(MarshalByRefObject)))
            {
                throw new Exception($"Type '{info.RuntimeType}' is not automaticly serializable as it inherits from MarshalByRefObject.");
            }

            else
            {
                // Must be an object, try and dump all it's fields
                state.Writer.Write((byte)PickleOperation.Object);
                if (!info.StaticType.IsValueType || info.StaticType != info.RuntimeType)
                {
                    SerializeType(state, info.RuntimeType);
                }
                var fields = GetSerializedFields(info.RuntimeType);
                // Sort the fields by name so we serialise in deterministic order
                Array.Sort(fields, (x, y) => x.Name.CompareTo(y.Name));

                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var field in fields)
                {
                    state.Writer.Write(field.Name);
                    // While it looks like we statically know the type here (it's the field type), it's not safe to pass it
                    // through as the static type. At derserialisation time we could be running a new program where the field has
                    // changed type, that change will fail to deserialise but it needs to fail safely(ish). Imagine changing FieldType
                    // from an Int32 to Int32[], we're going to try and read the 4 Int32 bytes as the length of the array and then start
                    // churning through the rest of the data stream trying to fill it.
                    var value = field.GetValue(obj);
                    var fieldInfo = MakeInfo(value, typeof(object), ShouldMemo(value, field.FieldType));
                    Serialize(state, value, fieldInfo);
                }
            }
        }

        private void SerializeType(PicklerSerializationState state, Type type, Type[]? genericTypeParameters = null, Type[]? genericMethodParameters = null)
        {
            Serialize(state, type, new SerializeInformation(type?.GetType(), typeof(Type), true), genericTypeParameters, genericMethodParameters);
        }

        private void Serialize(PicklerSerializationState state, object? obj, SerializeInformation info, Type[]? genericTypeParameters = null, Type[]? genericMethodParameters = null)
        {
            if (Object.ReferenceEquals(obj, null))
            {
                state.Writer.Write((byte)PickleOperation.Null);
            }
            else
            {
                // Rest of the operations will need the type of obj
                var typeCode = Type.GetTypeCode(info.RuntimeType);

                // Most pointers we'll reject but we special case IntPtr and UIntPtr as they're often 
                // used for native sized numbers
                if (info.RuntimeType.IsPointer || info.RuntimeType == typeof(Pointer))
                {
                    throw new Exception($"Pointer types are not serializable: '{info.RuntimeType}'");
                }

                if (info.ShouldMemo && state.DoMemo(obj))
                {
                    return;
                }

                if (info.RuntimeType.IsEnum)
                {
                    // typeCode for an enum will be something like Int32
                    if (info.NeedsOperationToken)
                    {
                        state.Writer.Write((byte)PickleOperation.Enum);
                        SerializeType(state, info.RuntimeType);
                    }
                    WriteEnumerationValue(state.Writer, typeCode, obj);
                    return;
                }

                switch (typeCode)
                {
                    case TypeCode.Boolean:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Boolean);
                        }
                        state.Writer.Write((bool)obj);
                        return;
                    case TypeCode.Char:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Char);
                        }
                        state.Writer.Write((char)obj);
                        return;
                    case TypeCode.SByte:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.SByte);
                        }
                        state.Writer.Write((sbyte)obj);
                        return;
                    case TypeCode.Int16:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Int16);
                        }
                        state.Writer.Write((short)obj);
                        return;
                    case TypeCode.Int32:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Int32);
                        }
                        state.Writer.Write((int)obj);
                        return;
                    case TypeCode.Int64:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Int64);
                        }
                        state.Writer.Write((long)obj);
                        return;
                    case TypeCode.Byte:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Byte);
                        }
                        state.Writer.Write((byte)obj);
                        return;
                    case TypeCode.UInt16:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt16);
                        }
                        state.Writer.Write((ushort)obj);
                        return;
                    case TypeCode.UInt32:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt32);
                        }
                        state.Writer.Write((uint)obj);
                        return;
                    case TypeCode.UInt64:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt64);
                        }
                        state.Writer.Write((ulong)obj);
                        return;
                    case TypeCode.Single:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Single);
                        }
                        state.Writer.Write((float)obj);
                        return;
                    case TypeCode.Double:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Double);
                        }
                        state.Writer.Write((double)obj);
                        return;
                    case TypeCode.Decimal:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.Decimal);
                        }
                        state.Writer.Write((decimal)obj);
                        return;
                    case TypeCode.DBNull:
                        if (info.NeedsOperationToken)
                        {
                            state.Writer.Write((byte)PickleOperation.DBNull);
                        }
                        return;
                    case TypeCode.String:
                        {
                            state.Writer.Write((byte)PickleOperation.String);
                            state.Writer.Write((string)obj);
                        }
                        return;
                    // Let DateTime just be handled by ISerializable 
                    case TypeCode.DateTime:
                    case TypeCode.Object:
                        SerializeObject(state, obj, info, genericTypeParameters, genericMethodParameters);
                        return;
                }

                throw new Exception($"Unhandled TypeCode '{typeCode}' for type '{info.RuntimeType}'");
            }
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
