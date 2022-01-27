﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    [Flags]
    public enum PickledTypeFlags : byte
    {
        IsAbstract = 1,
        IsSealed = 2,
        IsValueType = 4,
        IsEnum = 8,
        IsDelegate = 16,
    }

    sealed class SerialisedObjectTypeInfo
    {
        public PickledTypeFlags Flags;
        // Null if this wasn't serailised using object format, or if the fields have changed.
        public (Type, FieldInfo)[]? SerialisedFields;
        // Non null if there was an error building Fields (we should use a DU really)
        public string? Error;
        public PickleOperation? Operation;
        public TypeCode? TypeCode;
    }

    abstract class PickledTypeInfo : PickledMemberInfo
    {
        public static PickledTypeInfo FromType(Type type)
        {
            if (type.IsConstructedGenericType)
            {
                var genericArguments = type.GetGenericArguments();
                var pickledArguments = new PickledTypeInfo[genericArguments.Length];
                for (int i = 0; i < genericArguments.Length; ++i)
                {
                    pickledArguments[i] = FromType(genericArguments[i]);
                }

                return new PickledGenericType(
                    FromType(type.GetGenericTypeDefinition()),
                    pickledArguments);
            }
            else if (type.IsGenericParameter)
            {
                return new PickledGenericParameterRef(type);
            }
            else
            {
                return new PickledTypeInfoRef(type);
            }
        }

        public override MemberInfo MemberInfo { get { return Type; } }

        public abstract (Type, bool) Resolve();

        private Type? _type;
        public Type Type
        {
            get
            {
                if (_type == null)
                {
                    var (tp, complete) = Resolve();
                    if (!complete)
                    {
                        return tp;
                    }
                    _type = tp;
                }
                return _type;
            }
        }

        /// <summary>
        /// Like type but throws if not complete
        /// </summary>
        public Type CompleteType
        {
            get
            {

                if (_type == null)
                {
                    var (tp, complete) = Resolve();
                    if (!complete)
                    {
                        throw new Exception($"Expected {this} to be a complete type, not a constructing one");
                    }
                    _type = tp;
                }
                return _type;
            }
        }

        public abstract PickledConstructorInfo GetConstructor(Signature signature);

        public abstract PickledMethodInfo GetMethod(Signature signature);

        public abstract IEnumerable<PickledFieldInfo> GetFields();

        public abstract PickledFieldInfo GetField(string name);

        public abstract PickledPropertyInfo GetProperty(Signature signature);

        public abstract PickledEventInfo GetEvent(string name);

        public abstract PickledTypeInfo GetGenericArgument(int position);

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, Type);
        }

        public abstract PickledTypeInfo Reify(PickledTypeInfo[] genericArguments);
    }

    sealed class PickledTypeInfoRef : PickledTypeInfo
    {
        public PickledTypeInfoRef(Type type)
        {
            System.Diagnostics.Debug.Assert(!type.IsArray, "Tried to create a TypeRef for an array type, this should of been an ArrayType");
            System.Diagnostics.Debug.Assert(!type.IsConstructedGenericType, "Tried to create a TypeRef for a constructed generic type, this should of been a GenericType");
            System.Diagnostics.Debug.Assert(!type.IsGenericParameter, "Tried to create a TypeRef for a generic parameter, this should of been a PickledGenericParameterRef");

            Type = type;
        }

        private new readonly Type Type;

        public override (Type, bool) Resolve()
        {
            return (Type, true);
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            var constructors = Type.GetConstructors(BindingsAll);
            foreach (var constructor in constructors)
            {
                if (Signature.GetSignature(constructor).Equals(signature))
                {
                    return new PickledConstructorInfoRef(constructor);
                }
            }

            throw new Exception($"Could not load constructor '{signature}' from type '{Type.Name}'");
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            var methods = Type.GetMethods(BindingsAll);
            foreach (var method in methods)
            {
                if (Signature.GetSignature(method).Equals(signature))
                {
                    return new PickledMethodInfoRef(method);
                }
            }

            throw new Exception($"Could not load method '{signature}' from type '{Type.Name}'");
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            foreach (var field in Type.GetFields(BindingsAll))
            {
                yield return new PickledFieldInfoRef(field);
            }
        }

        public override PickledFieldInfo GetField(string name)
        {
            var result = Type.GetField(name, BindingsAll);
            if (result == null)
            {
                throw new Exception($"Could not load field '{name}' from type '{Type.Name}'");
            }
            return new PickledFieldInfoRef(result);
        }

        public override PickledEventInfo GetEvent(string name)
        {
            var result = Type.GetEvent(name, BindingsAll);
            if (result == null)
            {
                throw new Exception($"Could not load event '{name}' from type '{Type.Name}'");
            }
            return new PickledEventInfoRef(result);
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            var properties = Type.GetProperties(BindingsAll);
            foreach (var property in properties)
            {
                if (Signature.GetSignature(property).Equals(signature))
                {
                    return new PickledPropertyInfoRef(property);
                }
            }

            throw new Exception($"Could not load property '{signature}' from type '{Type.Name}'");
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            return new PickledGenericParameterRef(Type.GetGenericArguments()[position]);
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            return this;
        }
    }

    sealed class PickledArrayType : PickledTypeInfo
    {
        public PickledArrayType(PickledTypeInfo type, int rank)
        {
            Rank = rank;
            ElementType = type;
        }

        private readonly int Rank;
        private readonly PickledTypeInfo ElementType;

        public override (Type, bool) Resolve()
        {
            var (elementType, isComplete) = ElementType.Resolve();
            Type arrayType;
            if (Rank == 0)
            {
                arrayType = elementType.MakeArrayType();
            }
            else
            {
                arrayType = elementType.MakeArrayType(Rank);
            }
            return (arrayType, isComplete);
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            var constructors = Type.GetConstructors(BindingsAll);
            foreach (var constructor in constructors)
            {
                if (Signature.GetSignature(constructor).Equals(signature))
                {
                    return new PickledConstructorInfoRef(constructor);
                }
            }

            throw new Exception($"Could not load constructor '{signature}' from type '{Type.Name}'");
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            var methods = Type.GetMethods(BindingsAll);
            foreach (var method in methods)
            {
                if (Signature.GetSignature(method).Equals(signature))
                {
                    return new PickledMethodInfoRef(method);
                }
            }

            throw new Exception($"Could not load method '{signature}' from type '{Type.Name}'");
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            foreach (var field in Type.GetFields(BindingsAll))
            {
                yield return new PickledFieldInfoRef(field);
            }
        }

        public override PickledFieldInfo GetField(string name)
        {
            var result = Type.GetField(name, BindingsAll);
            if (result == null)
            {
                throw new Exception($"Could not load field '{name}' from type '{Type.Name}'");
            }
            return new PickledFieldInfoRef(result);
        }

        public override PickledEventInfo GetEvent(string name)
        {
            var result = Type.GetEvent(name, BindingsAll);
            if (result == null)
            {
                throw new Exception($"Could not load event '{name}' from type '{Type.Name}'");
            }
            return new PickledEventInfoRef(result);
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            var properties = Type.GetProperties(BindingsAll);
            foreach (var property in properties)
            {
                if (Signature.GetSignature(property).Equals(signature))
                {
                    return new PickledPropertyInfoRef(property);
                }
            }

            throw new Exception($"Could not load property '{signature}' from type '{Type.Name}'");
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            return new PickledGenericParameterRef(Type.GetGenericArguments()[position]);
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            var elementType = ElementType.Reify(genericArguments);
            return new PickledArrayType(elementType, Rank);
        }
    }

    sealed class PickledGenericParameterRef : PickledTypeInfo
    {
        public PickledGenericParameterRef(Type parameter)
        {
            Type = parameter;
        }

        private new readonly Type Type;

        public override (Type, bool) Resolve()
        {
            return (Type, true);
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override PickledFieldInfo GetField(string name)
        {
            throw new NotImplementedException();
        }

        public override PickledEventInfo GetEvent(string name)
        {
            throw new NotImplementedException();
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            throw new NotImplementedException();
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            return genericArguments[Type.GenericParameterPosition];
        }
    }

    sealed class PickledGenericParameterDef : PickledTypeInfo
    {
        public PickledGenericParameterDef(PickledTypeInfoDef declaringType, int position)
        {
            DeclaringType = declaringType;
            Position = position;
        }

        public PickledTypeInfoDef DeclaringType { get; }
        public int Position { get; }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override PickledFieldInfo GetField(string name)
        {
            throw new NotImplementedException();
        }

        public override PickledEventInfo GetEvent(string name)
        {
            throw new NotImplementedException();
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            throw new NotImplementedException();
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            return genericArguments[Position];
        }

        public override (Type, bool) Resolve()
        {
            if (DeclaringType.IsCreated)
            {
                var args = DeclaringType.Type.GetGenericArguments();
                return (args[Position], true);
            }

            return (DeclaringType.GenericParameters![Position], false);
        }
    }

    sealed class PickledTypeInfoDef : PickledTypeInfo
    {
        public PickledTypeInfoDef(TypeDef typeDef, TypeBuilder typeBuilder)
        {
            TypeDef = typeDef;
            TypeBuilder = typeBuilder;
        }

        Type? _type = null;
        public bool FullyDefined { get; set; }

        public Type CreateType()
        {
            _type = TypeBuilder.CreateType();
            if (_type == null)
            {
                throw new Exception($"CreateType for {TypeBuilder.Name} unexpectedly returned null");
            }
            return _type;
        }

        public bool IsCreated { get { return _type != null; } }

        public TypeDef TypeDef { get; private set; }

        public override (Type, bool) Resolve()
        {
            if (_type == null) return (TypeBuilder, false);
            return (_type, true);
        }

        public TypeBuilder TypeBuilder { get; }
        public GenericTypeParameterBuilder[]? GenericParameters { get; set; }
        public PickledFieldInfoDef[]? Fields { get; set; }
        public PickledPropertyInfoDef[]? Properties { get; set; }
        public PickledMethodInfoDef[]? Methods { get; set; }
        public PickledConstructorInfoDef[]? Constructors { get; set; }
        public PickledEventInfoDef[]? Events { get; set; }

        public override string ToString()
        {
            return TypeBuilder.Name;
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            if (Constructors != null)
            {
                foreach (var constructor in Constructors)
                {
                    if (constructor.GetSignature().Equals(signature))
                    {
                        return constructor;
                    }
                }
            }

            throw new Exception($"Could not load constructor '{signature}' from type '{TypeBuilder.Name}'");
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            if (Methods != null)
            {
                foreach (var method in Methods)
                {
                    if (method.GetSignature().Equals(signature))
                    {
                        return method;
                    }
                }
            }

            throw new Exception($"Could not load method '{signature}' from type '{TypeBuilder.Name}'");
        }

        public override PickledFieldInfo GetField(string name)
        {
            if (Fields != null)
            {
                foreach (var field in Fields)
                {
                    if (field.FieldBuilder.Name == name)
                    {
                        return field;
                    }
                }
            }

            throw new Exception($"Could not load field '{name}' from type '{TypeBuilder.Name}'");
        }
        public override PickledEventInfo GetEvent(string name)
        {
            if (Events != null)
            {
                foreach (var evt in Events)
                {
                    if (evt.Name == name)
                    {
                        return evt;
                    }
                }
            }

            throw new Exception($"Could not load event '{name}' from type '{TypeBuilder.Name}'");
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            if (Properties != null)
            {
                foreach (var property in Properties)
                {
                    if (property.GetSignature().Equals(signature))
                    {
                        return property;
                    }
                }
            }

            throw new Exception($"Could not load property '{signature}' from type '{TypeBuilder.Name}'");
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            System.Diagnostics.Debug.Assert(GenericParameters != null, "GenericParameters is null");
            System.Diagnostics.Debug.Assert(position >= 0, "Can't get generic argument for negative position");
            System.Diagnostics.Debug.Assert(position < GenericParameters.Length, "Generic argument position out of bounds");

            return new PickledGenericParameterDef(this, position);
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            if (Fields != null)
            {
                foreach (var field in Fields)
                {
                    yield return field;
                }
            }
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            return this;
        }
    }

    abstract class PickledMethodInfo : PickledMethodBase
    {
        public override MethodBase MethodBase { get { return MethodInfo; } }

        public abstract MethodInfo MethodInfo { get; }

        public override object? Invoke(object? target, params object?[] args)
        {
            return MethodInfo.Invoke(target, args);
        }

        public abstract PickledTypeInfo GetGenericArgument(int position);

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, MethodInfo);
        }
    }

    sealed class PickledMethodInfoRef : PickledMethodInfo
    {
        public override MethodInfo MethodInfo { get; }

        public PickledMethodInfoRef(MethodInfo constructorInfo)
        {
            MethodInfo = constructorInfo;
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            return new PickledGenericParameterRef(MethodInfo.GetGenericArguments()[position]);
        }
    }

    sealed class PickledMethodInfoDef : PickledMethodInfo
    {
        public PickledMethodInfoDef(PickledTypeInfoDef declaringType, MethodBuilder methodBuilder)
        {
            DeclaringType = declaringType;
            MethodBuilder = methodBuilder;
        }

        public PickledTypeInfoDef DeclaringType { get; }

        public override MethodInfo MethodInfo
        {
            get
            {
                if (!DeclaringType.IsCreated)
                {
                    return MethodBuilder;
                }

                var signature = GetSignature();
                var methods = DeclaringType.Type.GetMethods(BindingsAll);
                foreach (var method in methods)
                {
                    if (Signature.GetSignature(method) == signature)
                    {
                        return method;
                    }
                }

                throw new Exception($"Could not load method '{signature}' from type '{DeclaringType.Type.Name}'");
            }
        }


        public MethodBuilder MethodBuilder { get; }
        public GenericTypeParameterBuilder[]? GenericParameters { get; set; }
        public ParameterBuilder[]? Parameters { get; set; }
        public Type[]? ParameterTypes { get; set; }
        public PickledTypeInfo[]? Locals { get; set; }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }

        public Signature GetSignature()
        {
            return new Signature(MethodBuilder.Name, GenericParameters?.Length ?? 0, SignatureElement.FromType(MethodBuilder.ReturnType), SignatureElement.FromTypes(ParameterTypes));
        }
    }

    sealed class PickledGenericField : PickledFieldInfo
    {
        public override string Name { get; }

        public override FieldInfo FieldInfo
        {
            get
            {
                var (type, complete) = Type.Resolve();

                if (complete)
                {
                    var result = type.GetField(Name, BindingsAll);
                    if (result == null)
                    {
                        throw new Exception($"Could not load field '{Name}' from type '{type.Name}'");
                    }
                    return result;
                }
                else
                {

                    var openField = Type.GenericType.GetField(Name);
                    return TypeBuilder.GetField(type, openField.FieldInfo);
                }
            }
        }

        public override bool IsInstance => !FieldInfo.Attributes.HasFlag(FieldAttributes.Static);

        public override bool IsLiteral => FieldInfo.IsLiteral;

        public override bool IsNotSerialized => FieldInfo.IsNotSerialized;

        readonly PickledGenericType Type;

        public PickledGenericField(PickledGenericType type, string name)
        {
            Type = type;
            Name = name;
        }
    }

    sealed class PickledGenericType : PickledTypeInfo
    {
        public PickledGenericType(PickledTypeInfo genericType, PickledTypeInfo[] genericArguments)
        {
            GenericType = genericType;
            GenericArguments = genericArguments;
        }

        public PickledTypeInfo GenericType { get; }
        public PickledTypeInfo[] GenericArguments { get; }

        public override (Type, bool) Resolve()
        {
            var genericArguments = new Type[GenericArguments.Length];
            bool isComplete = true;
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                var (argument, isCreated) = GenericArguments[i].Resolve();
                isComplete &= isCreated;
                genericArguments[i] = argument;
            }

            if (GenericType is PickledTypeInfoRef)
            {
                return (GenericType.Type.MakeGenericType(genericArguments), isComplete);
            }
            else
            {
                var constructingType = (PickledTypeInfoDef)GenericType;
                var typeInfo = constructingType.Type;
                return (typeInfo.MakeGenericType(genericArguments), isComplete & constructingType.IsCreated);
            }
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            var (type, isComplete) = Resolve();

            if (isComplete)
            {
                var constructors = type.GetConstructors(BindingsAll);
                foreach (var constructor in constructors)
                {
                    if (Signature.GetSignature(constructor).Equals(signature))
                    {
                        return new PickledConstructorInfoRef(constructor);
                    }
                }

                throw new Exception($"Could not load constructor '{signature}' from type '{type.Name}'");
            }
            else
            {
                var constructorInfo = GenericType.GetConstructor(signature);
                return new PickledConstructorInfoRef(TypeBuilder.GetConstructor(type, constructorInfo.ConstructorInfo));
            }
        }

        public override PickledMethodInfo GetMethod(Signature signature)
        {
            var (type, isComplete) = Resolve();

            if (isComplete)
            {
                var methods = type.GetMethods(BindingsAll);
                foreach (var method in methods)
                {
                    if (Signature.GetSignature(method).Equals(signature))
                    {
                        return new PickledMethodInfoRef(method);
                    }
                }

                throw new Exception($"Could not load method '{signature}' from type '{type.Name}'");
            }
            else
            {
                var methodInfo = GenericType.GetMethod(signature);
                return new PickledMethodInfoRef(TypeBuilder.GetMethod(type, methodInfo.MethodInfo));
            }
        }

        public override IEnumerable<PickledFieldInfo> GetFields()
        {
            foreach (var field in GenericType.GetFields())
            {
                yield return new PickledGenericField(this, field.Name);
            }
        }

        public override PickledFieldInfo GetField(string name)
        {
            return new PickledGenericField(this, name);
        }

        public override PickledEventInfo GetEvent(string name)
        {
            var (type, isComplete) = Resolve();

            if (isComplete)
            {
                var result = type.GetEvent(name, BindingsAll);
                if (result == null)
                {
                    throw new Exception($"Could not load event '{name}' from type '{type.Name}'");
                }
                return new PickledEventInfoRef(result);
            }
            else
            {
                var eventInfo = GenericType.GetEvent(name);
                return new PickledEventInfoRef(eventInfo.EventInfo);
            }
        }

        public override PickledPropertyInfo GetProperty(Signature signature)
        {
            var (type, isComplete) = Resolve();

            if (isComplete)
            {
                var properties = type.GetProperties(BindingsAll);
                foreach (var property in properties)
                {
                    if (Signature.GetSignature(property).Equals(signature))
                    {
                        return new PickledPropertyInfoRef(property);
                    }
                }

                throw new Exception($"Could not load property '{signature}' from type '{type.Name}'");
            }
            else
            {
                var propertyInfo = GenericType.GetProperty(signature);
                return new PickledPropertyInfoRef(propertyInfo.PropertyInfo);
            }
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            return GenericArguments[position];
        }

        public override PickledTypeInfo Reify(PickledTypeInfo[] genericArguments)
        {
            return this;
        }
    }


    sealed class ConstructingGenericMethod : PickledMethodInfo
    {
        public ConstructingGenericMethod(PickledMethodInfo genericMethod, PickledTypeInfo[] genericArguments)
        {
            GenericMethod = genericMethod;
            GenericArguments = genericArguments;
        }

        public PickledMethodInfo GenericMethod { get; }
        public PickledTypeInfo[] GenericArguments { get; set; }

        public override MethodInfo MethodInfo
        {
            get
            {
                var genericArguments = new Type[GenericArguments.Length];
                for (int i = 0; i < genericArguments.Length; ++i)
                {
                    genericArguments[i] = GenericArguments[i].Type;
                }
                return GenericMethod.MethodInfo.MakeGenericMethod(genericArguments);
            }
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }
    }

    abstract class PickledObject
    {
        protected static readonly BindingFlags BindingsAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public abstract object Get();

        public override string? ToString()
        {
            return Get().ToString();
        }
    }

    abstract class PickledMemberInfo : PickledObject
    {
        public override object Get()
        {
            return MemberInfo;
        }

        public abstract MemberInfo MemberInfo { get; }

        public abstract void Emit(ILGenerator ilGenerator, OpCode opCode);
    }

    abstract class PickledMethodBase : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return MethodBase; } }

        public abstract MethodBase MethodBase { get; }

        public abstract object? Invoke(object? target, params object?[] args);
    }

    abstract class PickledConstructorInfo : PickledMethodBase
    {
        public override MethodBase MethodBase { get { return ConstructorInfo; } }

        public abstract ConstructorInfo ConstructorInfo { get; }

        public override object Invoke(object? target, params object?[] args)
        {
            return ConstructorInfo.Invoke(args);
        }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, ConstructorInfo);
        }
    }

    sealed class PickledConstructorInfoRef : PickledConstructorInfo
    {
        public override ConstructorInfo ConstructorInfo { get; }

        public PickledConstructorInfoRef(ConstructorInfo constructorInfo)
        {
            ConstructorInfo = constructorInfo;
        }
    }

    sealed class PickledConstructorInfoDef : PickledConstructorInfo
    {
        public PickledConstructorInfoDef(PickledTypeInfoDef constructingType, ConstructorBuilder constructorBuilder, ParameterBuilder[]? parameters, Type[]? parameterTypes, PickledTypeInfo[]? locals)
        {
            ConstructingType = constructingType;
            ConstructorBuilder = constructorBuilder;
            Parameters = parameters;
            ParameterTypes = parameterTypes;
            Locals = locals;
        }

        public override ConstructorInfo ConstructorInfo
        {
            get
            {
                if (!ConstructingType.IsCreated)
                {
                    return ConstructorBuilder;
                }

                var signature = GetSignature();
                var constructors = ConstructingType.Type.GetConstructors(BindingsAll);
                foreach (var constructor in constructors)
                {
                    if (Signature.GetSignature(constructor) == signature)
                    {
                        return constructor;
                    }
                }

                throw new Exception($"Could not load constructor '{signature}' from type '{ConstructingType.Type.Name}'");
            }
        }

        public Signature GetSignature()
        {
            return new Signature(ConstructorBuilder.Name, 0, SignatureElement.FromType(ConstructingType.Type), SignatureElement.FromTypes(ParameterTypes));
        }

        public PickledTypeInfoDef ConstructingType { get; }
        public ConstructorBuilder ConstructorBuilder { get; }
        public ParameterBuilder[]? Parameters { get; }
        public Type[]? ParameterTypes { get; }
        public PickledTypeInfo[]? Locals { get; }
    }

    abstract class PickledPropertyInfo : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return PropertyInfo; } }

        public abstract PropertyInfo PropertyInfo { get; }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            throw new Exception("Can't emit property info to IL stream");
        }
    }

    sealed class PickledPropertyInfoRef : PickledPropertyInfo
    {
        public override PropertyInfo PropertyInfo { get; }

        public PickledPropertyInfoRef(PropertyInfo propertyInfo)
        {
            PropertyInfo = propertyInfo;
        }
    }

    sealed class PickledPropertyInfoDef : PickledPropertyInfo
    {
        public PickledPropertyInfoDef(PickledTypeInfoDef declaringType, PropertyBuilder propertyBuilder, Type[] indexParameters)
        {
            DeclaringType = declaringType;
            PropertyBuilder = propertyBuilder;
            IndexParameters = indexParameters;
        }

        public PickledTypeInfoDef DeclaringType { get; }
        public PropertyBuilder PropertyBuilder { get; }
        public Type[] IndexParameters { get; }

        public override PropertyInfo PropertyInfo
        {
            get
            {
                if (!DeclaringType.IsCreated)
                {
                    return PropertyBuilder;
                }

                var signature = GetSignature();
                var properties = DeclaringType.Type.GetProperties(BindingsAll);
                foreach (var property in properties)
                {
                    if (Signature.GetSignature(property) == signature)
                    {
                        return property;
                    }
                }

                throw new Exception($"Could not load property '{signature}' from type '{DeclaringType.Type.Name}'");
            }
        }

        public Signature GetSignature()
        {
            return new Signature(PropertyBuilder.Name, 0, SignatureElement.FromType(PropertyBuilder.PropertyType), SignatureElement.FromTypes(IndexParameters));
        }
    }

    abstract class PickledEventInfo : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return EventInfo; } }

        public abstract EventInfo EventInfo { get; }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            throw new Exception("Can't emit event info to IL stream");
        }
    }

    sealed class PickledEventInfoRef : PickledEventInfo
    {
        public override EventInfo EventInfo { get; }

        public PickledEventInfoRef(EventInfo eventInfo)
        {
            EventInfo = eventInfo;
        }
    }

    sealed class PickledEventInfoDef : PickledEventInfo
    {
        public PickledEventInfoDef(PickledTypeInfoDef declaringType, EventBuilder eventBuilder, string name)
        {
            DeclaringType = declaringType;
            EventBuilder = eventBuilder;
            Name = name;
        }

        public PickledTypeInfoDef DeclaringType { get; }
        public EventBuilder EventBuilder { get; }
        public string Name { get; }

        public override EventInfo EventInfo
        {
            get
            {
                if (!DeclaringType.IsCreated)
                {
                    throw new Exception("EventBuilder can't create an EventInfo until the type is created");
                }

                var result = DeclaringType.Type.GetEvent(Name, BindingsAll);
                if (result == null)
                {
                    throw new Exception($"GetField for {DeclaringType.Type.Name} unexpectedly returned null");
                }
                return result;
            }
        }
    }

    abstract class PickledFieldInfo : PickledMemberInfo
    {
        public abstract string Name { get; }

        public abstract bool IsInstance { get; }

        public abstract bool IsLiteral { get; }

        public abstract bool IsNotSerialized { get; }

        public override MemberInfo MemberInfo { get { return FieldInfo; } }

        public abstract FieldInfo FieldInfo { get; }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, FieldInfo);
        }
    }

    sealed class PickledFieldInfoRef : PickledFieldInfo
    {
        public override string Name { get { return FieldInfo.Name; } }

        public override FieldInfo FieldInfo { get; }

        public override bool IsInstance => !FieldInfo.Attributes.HasFlag(FieldAttributes.Static);

        public override bool IsLiteral => FieldInfo.IsLiteral;

        public override bool IsNotSerialized => FieldInfo.IsNotSerialized;

        public PickledFieldInfoRef(FieldInfo fieldInfo)
        {
            FieldInfo = fieldInfo;
        }
    }

    sealed class PickledFieldInfoDef : PickledFieldInfo
    {
        public PickledFieldInfoDef(PickledTypeInfoDef declaringType, FieldBuilder fieldBuilder)
        {
            DeclaringType = declaringType;
            FieldBuilder = fieldBuilder;
        }

        public PickledTypeInfoDef DeclaringType { get; }

        public FieldBuilder FieldBuilder { get; }

        public override string Name { get { return FieldBuilder.Name; } }

        public override bool IsInstance => !FieldInfo.Attributes.HasFlag(FieldAttributes.Static);

        public override bool IsLiteral => FieldBuilder.IsLiteral;

        public override bool IsNotSerialized => FieldBuilder.IsNotSerialized;

        public override FieldInfo FieldInfo
        {
            get
            {
                if (!DeclaringType.IsCreated)
                {
                    return FieldBuilder;
                }

                var result = DeclaringType.Type.GetField(FieldBuilder.Name, BindingsAll);
                if (result == null)
                {
                    throw new Exception($"GetField for {DeclaringType.Type.Name} unexpectedly returned null");
                }
                return result;
            }
        }
    }
}
