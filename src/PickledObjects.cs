using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    [Flags]
    public enum PickledTypeFlags
    {
        IsAbstract = 1,
        IsSealed = 2,
        IsValueType = 4,
        HasElementType = 8,
    }

    public enum PickledTypeMode
    {
        IsBuiltin = -1,
        IsEnum = 0,
        IsDelegate = 1,
        IsReduced = 2,
        IsAutoSerialisedObject = 4,

        IsReflectionObject = 256,
    }

    struct GenericTypeContext
    {
        public readonly Type[]? GenericTypeParameters;
        public readonly Type[]? GenericMethodParameters;

        public GenericTypeContext(Type[]? genericTypeParameters, Type[]? genericMethodParameters)
        {
            GenericTypeParameters = genericTypeParameters;
            GenericMethodParameters = genericMethodParameters;
        }

        public GenericTypeContext(Type[]? genericTypeParameters)
        {
            GenericTypeParameters = genericTypeParameters;
            GenericMethodParameters = null;
        }
    }

    sealed class SerialisedObjectTypeInfo
    {
        public readonly Type Type;
        public SerialisedObjectTypeInfo(Type type)
        {
            Type = type;
        }

        public PickledTypeMode Mode;
        public PickledTypeFlags Flags;
        // Null if this wasn't serailised using object format, or if the fields have changed.
        public (SerialisedObjectTypeInfo, FieldInfo)[]? SerialisedFields;
        // Non null if there was an error building Fields (we should use a DU really) or this type couldn't be serialised
        public string? Error;
        // Only non-null if an enum
        public TypeCode? TypeCode;
        // Either an array OR Nullable<T>
        public SerialisedObjectTypeInfo? Element;
        // Only non-null if reduced
        public IReducer? Reducer;
        // Only non-null if tuple
        public SerialisedObjectTypeInfo[]? TupleArguments;
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

        public enum Resolved
        {
            IsComplete,
            IsNotComplete,
            IsAssumedComplete,
        }

        public override MemberInfo MemberInfo { get { return Type; } }

        public abstract (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeFullyDefined);

        public (Type, bool) Resolve()
        {
            if (_type == null)
            {
                var assumptions = new HashSet<PickledTypeInfoDef>();
                var (type, isComplete) = Resolve(assumptions);
                if (isComplete == Resolved.IsNotComplete)
                {
                    return (type, false);
                }
                _type = type;
            }
            return (_type, true);
        }

        private Type? _type;

        public Type Type
        {
            get
            {
                var (type, _) = Resolve();
                return type;
            }
        }

        /// <summary>
        /// Like type but throws if not complete
        /// </summary>
        public Type CompleteType
        {
            get
            {
                var (type, isComplete) = Resolve();
                if (isComplete != true)
                {
                    throw new Exception($"Expected {this} to be a complete type, not a constructing one");
                }
                return type;
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

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            return (Type, Resolved.IsComplete);
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

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            var (elementType, isComplete) = ElementType.Resolve(assumeIsComplete);
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
    }

    sealed class PickledGenericParameterRef : PickledTypeInfo
    {
        public PickledGenericParameterRef(Type parameter)
        {
            Type = parameter;
        }

        private new readonly Type Type;

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            return (Type, Resolved.IsComplete);
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

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            var (resolvedType, isComplete) = DeclaringType.Resolve(assumeIsComplete);
            if (isComplete == Resolved.IsComplete)
            {
                var args = resolvedType.GetGenericArguments();
                return (args[Position], isComplete);
            }

            return (DeclaringType.GenericParameters![Position], isComplete);
        }
    }

    sealed class PickledTypeInfoDef : PickledTypeInfo
    {
        public PickledTypeInfoDef(TypeDef typeDef, TypeBuilder typeBuilder, PickledTypeInfoDef? declaringType)
        {
            TypeDef = typeDef;
            TypeBuilder = typeBuilder;
            _declaringType = declaringType;
        }

        PickledTypeInfoDef? _declaringType;

        public bool FullyDefined
        {
            private get; set;
        }

        public TypeDef TypeDef { get; private set; }

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            if (!FullyDefined)
            {
                return (TypeBuilder, Resolved.IsNotComplete);
            }

            if (!assumeIsComplete.Add(this))
            {
                // We're assumed to be complete, so just return that
                return (TypeBuilder, Resolved.IsAssumedComplete);
            }

            if (_declaringType != null)
            {
                var (_, isComplete) = _declaringType.Resolve(assumeIsComplete);
                if (isComplete == Resolved.IsNotComplete)
                {
                    assumeIsComplete.Remove(this);
                    return (TypeBuilder, Resolved.IsNotComplete);
                }
            }

            // Need to see if any base types are not resolved yet
            if (BaseTypes != null)
            {
                foreach (var baseType in BaseTypes)
                {
                    var (_, isComplete) = baseType.Resolve(assumeIsComplete);
                    if (isComplete == Resolved.IsNotComplete)
                    {
                        assumeIsComplete.Remove(this);
                        return (TypeBuilder, Resolved.IsNotComplete);
                    }
                }
            }

            assumeIsComplete.Remove(this);

            // Got here everything else must of fully resolved, try and create this
            var type = TypeBuilder.CreateType();
            if (type == null)
            {
                throw new Exception($"CreateType for {TypeBuilder.Name} unexpectedly returned null");
            }

            return (type, Resolved.IsComplete);
        }

        public PickledTypeInfo[]? BaseTypes { get; set; }

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
                var (resolvedType, isComplete) = DeclaringType.Resolve();
                if (isComplete != true)
                {
                    return MethodBuilder;
                }

                var signature = GetSignature();
                var methods = resolvedType.GetMethods(BindingsAll);
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
                var (type, isComplete) = Type.Resolve();

                if (isComplete == true)
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

        public override (Type, Resolved) Resolve(HashSet<PickledTypeInfoDef> assumeIsComplete)
        {
            var genericArguments = new Type[GenericArguments.Length];
            Resolved allArgumentsComplete = Resolved.IsComplete;
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                var (argument, isArgumentComplete) = GenericArguments[i].Resolve(assumeIsComplete);

                if (isArgumentComplete == Resolved.IsNotComplete)
                {
                    allArgumentsComplete = Resolved.IsNotComplete;
                }
                else if (isArgumentComplete == Resolved.IsAssumedComplete && allArgumentsComplete == Resolved.IsComplete)
                {
                    allArgumentsComplete = Resolved.IsAssumedComplete;
                }

                genericArguments[i] = argument;
            }

            var (resolvedType, isTypeDefinitionComplete) = GenericType.Resolve(assumeIsComplete);

            Resolved isComplete = allArgumentsComplete;
            if (isTypeDefinitionComplete == Resolved.IsNotComplete)
            {
                isComplete = Resolved.IsNotComplete;
            }
            else if (isTypeDefinitionComplete == Resolved.IsAssumedComplete && isComplete == Resolved.IsComplete)
            {
                isComplete = Resolved.IsAssumedComplete;
            }

            return (resolvedType.MakeGenericType(genericArguments), isComplete);
        }

        public override PickledConstructorInfo GetConstructor(Signature signature)
        {
            var (type, isComplete) = Resolve();

            if (isComplete == true)
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

            if (isComplete == true)
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

            if (isComplete == true)
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

            if (isComplete == true)
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

    abstract class PickledModule : PickledObject
    {
        public override object Get()
        {
            return Module;
        }

        public abstract Module Module { get; }
    }

    sealed class PickledModuleRef : PickledModule
    {
        private readonly Module _module;
        public override Module Module => _module;

        public PickledModuleRef(Module module)
        {
            _module = module;
        }
    }

    sealed class PickledModuleDef : PickledModule
    {
        private readonly ModuleBuilder _moduleBuilder;
        public override Module Module => _moduleBuilder;
        public ModuleBuilder ModuleBuilder => _moduleBuilder;

        public PickledModuleDef(ModuleBuilder moduleBuilder)
        {
            _moduleBuilder = moduleBuilder;
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

        public abstract Type[] GetParameters();
    }

    sealed class PickledConstructorInfoRef : PickledConstructorInfo
    {
        public override ConstructorInfo ConstructorInfo { get; }

        public PickledConstructorInfoRef(ConstructorInfo constructorInfo)
        {
            ConstructorInfo = constructorInfo;
        }

        public override Type[] GetParameters()
        {
            var rawParameters = ConstructorInfo.GetParameters();
            var refParameters = new Type[rawParameters.Length];
            for (int i = 0; i < rawParameters.Length; ++i)
            {
                refParameters[i] = rawParameters[i].ParameterType;
            }
            return refParameters;
        }
    }

    sealed class PickledConstructorInfoDef : PickledConstructorInfo
    {
        public PickledConstructorInfoDef(PickledTypeInfoDef constructingType, ConstructorBuilder constructorBuilder, ParameterBuilder[]? parameters, Type[]? parameterTypes)
        {
            ConstructingType = constructingType;
            ConstructorBuilder = constructorBuilder;
            Parameters = parameters;
            ParameterTypes = parameterTypes;
        }

        public override ConstructorInfo ConstructorInfo
        {
            get
            {
                var (resolvedType, isComplete) = ConstructingType.Resolve();
                if (isComplete != true)
                {
                    return ConstructorBuilder;
                }

                var signature = GetSignature();
                var constructors = resolvedType.GetConstructors(BindingsAll);
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

        public override Type[] GetParameters()
        {
            if (ParameterTypes == null) return new Type[0];
            return (Type[])ParameterTypes.Clone();
        }

        public Signature GetSignature()
        {
            return new Signature(ConstructorBuilder.Name, 0, SignatureElement.FromType(ConstructingType.Type), SignatureElement.FromTypes(ParameterTypes));
        }

        public PickledTypeInfoDef ConstructingType { get; }
        public ConstructorBuilder ConstructorBuilder { get; }
        public ParameterBuilder[]? Parameters { get; }
        public Type[]? ParameterTypes { get; }
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
                var (resolvedType, isComplete) = DeclaringType.Resolve();
                if (isComplete != true)
                {
                    return PropertyBuilder;
                }

                var signature = GetSignature();
                var properties = resolvedType.GetProperties(BindingsAll);
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
                var (resolvedType, isComplete) = DeclaringType.Resolve();
                if (isComplete != true)
                {
                    throw new Exception("EventBuilder can't create an EventInfo until the type is created");
                }

                var result = resolvedType.GetEvent(Name, BindingsAll);
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
                var (resolvedType, isComplete) = DeclaringType.Resolve();
                if (isComplete != true)
                {
                    return FieldBuilder;
                }

                var result = resolvedType.GetField(FieldBuilder.Name, BindingsAll);
                if (result == null)
                {
                    throw new Exception($"GetField for {DeclaringType.Type.Name} unexpectedly returned null");
                }
                return result;
            }
        }
    }
}
