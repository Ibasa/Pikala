using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    public sealed partial class Pickler
    {
        private bool PickleByValue(Assembly assembly)
        {
            return
                // We never pickle mscorlib by value, even if _pickleByValuePredicate returns true for it
                assembly != mscorlib && (
                assembly.IsDynamic ||
                assembly.Location == "" ||
                _pickleByValuePredicate(assembly));
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
                Serialize(state, parameter.ParameterType, typeof(Type), genericTypeParameters, null);
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
                Serialize(state, local.LocalType, typeof(Type), genericTypeParameters, null);
            }

            var collectedTypes = CollectTypes(genericTypeParameters, constructor.Module, null, methodBody);
            state.Writer.Write7BitEncodedInt(collectedTypes.Count);
            foreach (var type in collectedTypes)
            {
                Serialize(state, type, typeof(Type), genericTypeParameters, null);
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

            Serialize(state, method.ReturnType, typeof(Type), genericTypeParameters, genericMethodParameters);

            var methodParameters = method.GetParameters();
            state.Writer.Write7BitEncodedInt(methodParameters.Length);
            foreach (var parameter in methodParameters)
            {
                Serialize(state, parameter.ParameterType, typeof(Type), genericTypeParameters, genericMethodParameters);
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
                    Serialize(state, local.LocalType, typeof(Type), genericTypeParameters, genericMethodParameters);
                }

                var collectedTypes = CollectTypes(genericTypeParameters, method.Module, genericMethodParameters, methodBody);
                state.Writer.Write7BitEncodedInt(collectedTypes.Count);
                foreach (var type in collectedTypes)
                {
                    Serialize(state, type, typeof(Type), genericTypeParameters, genericMethodParameters);
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
                            Serialize(state, typeInfo, typeof(Type), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fieldToken = ilReader.ReadInt32();
                            var fieldInfo = methodModule.ResolveField(fieldToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, fieldInfo, typeof(FieldInfo), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var methodToken = ilReader.ReadInt32();
                            var methodInfo = methodModule.ResolveMethod(methodToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, methodInfo, typeof(MethodInfo), genericTypeParameters, genericMethodParameters);
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var memberToken = ilReader.ReadInt32();
                            var memberInfo = methodModule.ResolveMember(memberToken, genericTypeParameters, genericMethodParameters);
                            Serialize(state, memberInfo, typeof(MemberInfo), genericTypeParameters, genericMethodParameters);
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

        private void SerializeType(PicklerSerializationState state, Type type, Type[]? genericParameters)
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
                Serialize(state, type.BaseType, typeof(Type), genericParameters, null);
            }

            var interfaces = type.GetInterfaces();
            state.Writer.Write7BitEncodedInt(interfaces.Length);
            foreach (var interfaceType in interfaces)
            {
                Serialize(state, interfaceType, typeof(Type), genericParameters, null);

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
                Serialize(state, field.FieldType, typeof(Type), genericParameters, null);
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
                Serialize(state, property.PropertyType, typeof(Type), genericParameters, null);
                var indexParameters = property.GetIndexParameters();
                state.Writer.Write7BitEncodedInt(indexParameters.Length);
                foreach (var indexParameter in indexParameters)
                {
                    Serialize(state, indexParameter.ParameterType, typeof(Type), genericParameters, null);
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
                        Serialize(state, field.GetValue(null), field.FieldType, genericParameters);
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
                throw new Exception($"Can not serialize array of rank {obj.Rank}");
            }

            state.Writer.Write((byte)PickleOperation.Array);
            var elementType = objType.GetElementType();
            Serialize(state, elementType, typeof(Type));

            // Special case rank 1 with lower bound 0 
            if (obj.Rank == 1 && obj.GetLowerBound(0) == 0)
            {
                // Rank 1 really but we use rank 0 as a special marker 
                // for a "normal" array, that is one dimension and a lower 
                // bound of zero.
                state.Writer.Write((byte)0);
                state.Writer.Write7BitEncodedInt(obj.Length);
                foreach (var item in obj)
                {
                    Serialize(state, item, elementType);
                }
            }
            else
            {
                if (obj.Rank > byte.MaxValue)
                {
                    // Who has 256 rank arrays!?
                    throw new NotImplementedException($"Pikala does not support arrays of rank higher than {byte.MaxValue}");
                }

                // This might just be rank 1 but with non-normal bounds
                state.Writer.Write((byte)obj.Rank);
                for (int dimension = 0; dimension < obj.Rank; ++dimension)
                {
                    state.Writer.Write7BitEncodedInt(obj.GetLength(dimension));
                    state.Writer.Write7BitEncodedInt(obj.GetLowerBound(dimension));
                }
                foreach (var item in obj)
                {
                    Serialize(state, item, elementType);
                }
            }
        }

        private void WriteCustomAttributeValue(PicklerSerializationState state, object value, Type staticType)
        {
            // argument might be a ReadOnlyCollection[CustomAttributeTypedArgument] but we should write that as just an array of values
            if (value is System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument> collection)
            {
                var result = new object[collection.Count];
                for (int i = 0; i < result.Length; ++i)
                {
                    result[i] = collection[i].Value;
                }
                value = result;
            }
            Serialize(state, value, staticType);
        }

        private void WriteCustomAttributes(PicklerSerializationState state, CustomAttributeData[] attributes)
        {
            state.Writer.Write7BitEncodedInt(attributes.Length);
            foreach (var attribute in attributes)
            {
                Serialize(state, attribute.Constructor, typeof(ConstructorInfo));
                state.Writer.Write7BitEncodedInt(attribute.ConstructorArguments.Count);
                foreach (var argument in attribute.ConstructorArguments)
                {
                    WriteCustomAttributeValue(state, argument.Value, typeof(object));
                }

                var fieldCount = attribute.NamedArguments.Count(argument => argument.IsField);
                var propertyCount = attribute.NamedArguments.Count - fieldCount;

                state.Writer.Write7BitEncodedInt(propertyCount);
                foreach (var argument in attribute.NamedArguments)
                {
                    if (!argument.IsField)
                    {
                        var property = (PropertyInfo)argument.MemberInfo;
                        Serialize(state, property, typeof(PropertyInfo));
                        WriteCustomAttributeValue(state, argument.TypedValue.Value, property.PropertyType);
                    }
                }

                state.Writer.Write7BitEncodedInt(fieldCount);
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.IsField)
                    {
                        var field = (FieldInfo)argument.MemberInfo;
                        Serialize(state, field, typeof(FieldInfo));
                        WriteCustomAttributeValue(state, argument.TypedValue.Value, field.FieldType);
                    }
                }
            }
        }

        private void SerializeObject(PicklerSerializationState state, object obj, Type objType, Type staticType, Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            // If we call this we know obj is not memoised or null or an enum 
            // or any of the types explictly in System.TypeCode

            IReducer reducer;

            if (objType.IsArray)
            {
                SerializeArray(state, (Array)obj, objType);
            }

            // This check needs to come before IsValueType, because these 
            // are also value types.
            else if (objType == typeof(IntPtr))
            {
                if (staticType != objType)
                {
                    state.Writer.Write((byte)PickleOperation.IntPtr);
                }
                state.Writer.Write((long)(IntPtr)obj);
            }
            else if (objType == typeof(UIntPtr))
            {
                if (staticType != objType)
                {
                    state.Writer.Write((byte)PickleOperation.UIntPtr);
                }
                state.Writer.Write((ulong)(UIntPtr)obj);
            }

            // Reflection
            else if (objType.IsAssignableTo(typeof(Assembly)))
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

            else if (objType.IsAssignableTo(typeof(Module)))
            {
                // This is a module, we need to emit a reference to the assembly it's found in and it's name
                var module = (Module)obj;

                // Is this assembly one we should save by value?
                if (PickleByValue(module.Assembly))
                {
                    state.Writer.Write((byte)PickleOperation.ModuleDef);
                    state.Writer.Write(module.Name);
                    Serialize(state, module.Assembly, typeof(Assembly));

                    WriteCustomAttributes(state, module.CustomAttributes.ToArray());

                    var fields = module.GetFields();
                    state.Writer.Write7BitEncodedInt(fields.Length);
                    foreach (var field in fields)
                    {
                        state.Writer.Write(field.Name);
                        state.Writer.Write((int)field.Attributes);
                        Serialize(state, field.FieldType, typeof(Type), null, null);
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
                            Serialize(state, field.GetValue(null), field.FieldType, null);
                        }
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
                    Serialize(state, module.Assembly, typeof(Assembly));
                }
            }

            else if (objType.IsAssignableTo(typeof(Type)))
            {
                // This is a type, we need to emit a TypeRef or Def so it can be reconstructed
                var type = (Type)obj;

                // Constructed generic types are always handled the same way, we write out a GenericDef, the unconstructed generic type and then the generic arguments
                if (type.IsConstructedGenericType)
                {
                    state.Writer.Write((byte)PickleOperation.GenericInstantiation);
                    Serialize(state, type.GetGenericTypeDefinition(), typeof(Type));
                    state.Writer.Write7BitEncodedInt(type.GenericTypeArguments.Length);
                    foreach (var arg in type.GenericTypeArguments)
                    {
                        Serialize(state, arg, typeof(Type));
                    }
                }

                else if (type.IsGenericParameter)
                {
                    if (type.DeclaringMethod != null)
                    {
                        if (genericMethodParameters == null)
                        {
                            state.Writer.Write((byte)PickleOperation.GenericParameter);
                            Serialize(state, type.DeclaringMethod, typeof(MethodInfo));
                        }
                        else
                        {
                            state.Writer.Write((byte)PickleOperation.MVar);
                        }
                    }
                    else if (type.DeclaringType != null && genericMethodParameters == null)
                    {
                        if (genericMethodParameters == null)
                        {
                            state.Writer.Write((byte)PickleOperation.GenericParameter);
                            Serialize(state, type.DeclaringType, typeof(Type));
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
                            Serialize(state, type.DeclaringType, typeof(Type));
                        }
                        else
                        {
                            Serialize(state, type.Module, typeof(Module));
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
                            Serialize(state, invoke.ReturnType, typeof(Type));
                            var parameters = invoke.GetParameters();
                            state.Writer.Write7BitEncodedInt(parameters.Length);
                            foreach (var parameter in parameters)
                            {
                                state.Writer.Write(parameter.Name);
                                Serialize(state, parameter.ParameterType, typeof(Type), genericParameters);
                            }
                        }
                        else
                        {
                            SerializeType(state, type, genericParameters);
                        }
                    });
                }
                else
                {
                    // Just write out a refernce to the type
                    state.Writer.Write((byte)PickleOperation.TypeRef);

                    if (type.DeclaringType != null)
                    {
                        Serialize(state, type.DeclaringType, typeof(Type));
                        state.Writer.Write(type.Name);
                    }
                    else
                    {
                        Serialize(state, type.Module, typeof(Module));
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

            else if (objType.IsAssignableTo(typeof(FieldInfo)))
            {
                var field = (FieldInfo)obj;

                state.Writer.Write((byte)PickleOperation.FieldRef);
                Serialize(state, field.ReflectedType, typeof(Type));
                state.Writer.Write(field.Name);
            }

            else if (objType.IsAssignableTo(typeof(PropertyInfo)))
            {
                var property = (PropertyInfo)obj;

                state.Writer.Write((byte)PickleOperation.PropertyRef);
                Serialize(state, property.ReflectedType, typeof(Type));
                state.Writer.Write(property.Name);
            }

            else if (objType.IsAssignableTo(typeof(MethodInfo)))
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
                        Serialize(state, generic, typeof(Type));
                    }
                }
                else
                {
                    state.Writer.Write(Method.GetSignature(method));
                    state.Writer.Write7BitEncodedInt(0);
                }
                Serialize(state, method.ReflectedType, typeof(Type));
            }

            else if (objType.IsAssignableTo(typeof(ConstructorInfo)))
            {
                var method = (ConstructorInfo)obj;

                state.Writer.Write((byte)PickleOperation.ConstructorRef);
                state.Writer.Write(Method.GetSignature(method));
                Serialize(state, method.ReflectedType, typeof(Type));
            }

            // End of reflection handlers

            else if (objType.IsAssignableTo(typeof(MulticastDelegate)))
            {
                // Delegates are just a target and a method
                var dele = (MulticastDelegate)obj;
                var invocationList = dele.GetInvocationList();

                state.Writer.Write((byte)PickleOperation.Delegate);
                Serialize(state, objType, typeof(Type));
                state.Writer.Write7BitEncodedInt(invocationList.Length);
                foreach (var invocation in invocationList)
                {
                    Serialize(state, invocation.Target, typeof(object));
                    Serialize(state, invocation.Method, typeof(MethodInfo));
                }
            }

            else if (_reducers.TryGetValue(objType, out reducer) || (objType.IsGenericType && _reducers.TryGetValue(objType.GetGenericTypeDefinition(), out reducer)))
            {
                // We've got a reducer for the type (or its generic variant)
                var (method, target, args) = reducer.Reduce(objType, obj);

                state.Writer.Write((byte)PickleOperation.Reducer);
                Serialize(state, method, typeof(MethodBase));

                // Assert properties of the reduction
                if (method is ConstructorInfo constructorInfo)
                {
                    if (target != null)
                    {
                        throw new Exception($"Invalid reduction for type '{objType}'. MethodBase was a ConstructorInfo but Target was not null.");
                    }

                    if (constructorInfo.DeclaringType != objType)
                    {
                        throw new Exception($"Invalid reduction for type '{objType}'. MethodBase was a ConstructorInfo for '{constructorInfo.DeclaringType}'.");
                    }

                    // We don't write target for ConstructorInfo, it must be null.
                    Serialize(state, args, typeof(object[]));
                }
                else if (method is MethodInfo methodInfo)
                {
                    if (methodInfo.ReturnType != objType)
                    {
                        throw new Exception($"Invalid reduction for type '{objType}'. MethodBase was a MethodInfo that returns '{methodInfo.ReturnType}'.");
                    }

                    Serialize(state, target, typeof(object));
                    Serialize(state, args, typeof(object[]));
                }
                else
                {
                    throw new Exception($"Invalid reduction for type '{objType}'. MethodBase was '{method}'.");
                }
            }

            else if (objType.IsAssignableTo(typeof(System.Runtime.Serialization.ISerializable)))
            {
                // ISerializable objects call into GetObjectData and will reconstruct with the (SerializationInfo, StreamingContext) constructor

                var iserializable = (System.Runtime.Serialization.ISerializable)obj;

                var context = new System.Runtime.Serialization.StreamingContext(System.Runtime.Serialization.StreamingContextStates.All, this);
                var info = new System.Runtime.Serialization.SerializationInfo(objType, new System.Runtime.Serialization.FormatterConverter());
                iserializable.GetObjectData(info, context);

                state.Writer.Write((byte)PickleOperation.ISerializable);
                if (!staticType.IsValueType || staticType != objType)
                {
                    Serialize(state, objType, typeof(Type));
                }
                state.Writer.Write7BitEncodedInt(info.MemberCount);
                foreach (var member in info)
                {
                    state.Writer.Write(member.Name);
                    Serialize(state, member.Value, typeof(object));
                }
            }

            else if (objType.IsAssignableTo(typeof(MarshalByRefObject)))
            {
                throw new Exception($"Type '{objType}' is not automaticly serializable as it inherits from MarshalByRefObject.");
            }

            else
            {
                // Must be an object, try and dump all it's fields
                state.Writer.Write((byte)PickleOperation.Object);
                if (!staticType.IsValueType || staticType != objType)
                {
                    Serialize(state, objType, typeof(Type));
                }
                var fields = GetSerializedFields(objType);
                state.Writer.Write7BitEncodedInt(fields.Length);
                foreach (var field in fields)
                {
                    state.Writer.Write(field.Name);
                    Serialize(state, field.GetValue(obj), field.FieldType);
                }
            }
        }

        /// <summary>
        /// There are some objects that we shouldn't bother to memoise because it's cheaper to just write their tokens.
        /// </summary>
        private bool ShouldMemo(object obj, Type staticType)
        {
            // If the static type is a value type we shouldn't memo because this is a value not a reference
            if (staticType.IsValueType) { return false; }

            // mscorlib gets saved as a single token
            if (Object.ReferenceEquals(obj, mscorlib)) { return false; }

            return true;
        }


        private void Serialize(PicklerSerializationState state, object? obj, Type staticType, Type[]? genericTypeParameters = null, Type[]? genericMethodParameters = null)
        {
            if (Object.ReferenceEquals(obj, null))
            {
                state.Writer.Write((byte)PickleOperation.Null);
            }
            else
            {
                // Rest of the operations will need the type of obj
                var objType = obj.GetType();
                var typeCode = Type.GetTypeCode(objType);

                // Most pointers we'll reject but we special case IntPtr and UIntPtr as they're often 
                // used for native sized numbers
                if (objType.IsPointer || objType == typeof(Pointer))
                {
                    throw new Exception($"Pointer types are not serializable: '{objType}'");
                }

                if (ShouldMemo(obj, staticType) && state.DoMemo(obj))
                {
                    return;
                }

                if (objType.IsEnum)
                {
                    // typeCode for an enum will be something like Int32
                    if (staticType != objType)
                    {
                        state.Writer.Write((byte)PickleOperation.Enum);
                        Serialize(state, objType, typeof(Type));
                    }
                    WriteEnumerationValue(state.Writer, typeCode, obj);
                    return;
                }

                switch (typeCode)
                {
                    case TypeCode.Boolean:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Boolean);
                        }
                        state.Writer.Write((bool)obj);
                        return;
                    case TypeCode.Char:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Char);
                        }
                        state.Writer.Write((char)obj);
                        return;
                    case TypeCode.SByte:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.SByte);
                        }
                        state.Writer.Write((sbyte)obj);
                        return;
                    case TypeCode.Int16:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Int16);
                        }
                        state.Writer.Write((short)obj);
                        return;
                    case TypeCode.Int32:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Int32);
                        }
                        state.Writer.Write((int)obj);
                        return;
                    case TypeCode.Int64:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Int64);
                        }
                        state.Writer.Write((long)obj);
                        return;
                    case TypeCode.Byte:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Byte);
                        }
                        state.Writer.Write((byte)obj);
                        return;
                    case TypeCode.UInt16:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt16);
                        }
                        state.Writer.Write((ushort)obj);
                        return;
                    case TypeCode.UInt32:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt32);
                        }
                        state.Writer.Write((uint)obj);
                        return;
                    case TypeCode.UInt64:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.UInt64);
                        }
                        state.Writer.Write((ulong)obj);
                        return;
                    case TypeCode.Single:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Single);
                        }
                        state.Writer.Write((float)obj);
                        return;
                    case TypeCode.Double:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Double);
                        }
                        state.Writer.Write((double)obj);
                        return;
                    case TypeCode.Decimal:
                        if (staticType != objType)
                        {
                            state.Writer.Write((byte)PickleOperation.Decimal);
                        }
                        state.Writer.Write((decimal)obj);
                        return;
                    case TypeCode.DBNull:
                        if (staticType != objType)
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
                        SerializeObject(state, obj, objType, staticType, genericTypeParameters, genericMethodParameters);
                        return;
                }

                throw new Exception($"Unhandled TypeCode '{typeCode}' for type '{objType}'");
            }
        }

        public void Serialize(Stream stream, object? rootObject)
        {
            var state = new PicklerSerializationState(stream);

            // Always start the pickler stream with a header for sanity checking inputs
            state.Writer.Write(_header);
            state.Writer.Write(_version);

            Serialize(state, rootObject, typeof(object));
            state.CheckTrailers();
        }
    }
}
