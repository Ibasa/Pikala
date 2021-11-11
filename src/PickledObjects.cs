using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Ibasa.Pikala
{
    abstract class PickledTypeInfo : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return Type; } }

        public abstract Type Type { get; }

        public abstract PickledConstructorInfo GetConstructor(string signature);

        public abstract PickledMethodInfo GetMethod(string signature);

        public abstract PickledFieldInfo GetField(string name);

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, Type);
        }
    }

    sealed class PickledTypeInfoRef : PickledTypeInfo
    {
        public override Type Type { get; }

        public PickledTypeInfoRef(Type type)
        {
            Type = type;
        }

        public override PickledConstructorInfo GetConstructor(string signature)
        {
            var constructors = Type.GetConstructors(BindingsAll);
            foreach (var constructor in constructors)
            {
                if (Method.GetSignature(constructor) == signature)
                {
                    return new PickledConstructorInfoRef(constructor);
                }
            }

            throw new Exception($"Could not load constructor '{signature}' from type '{Type.Name}'");
        }

        public override PickledMethodInfo GetMethod(string signature)
        {
            var methods = Type.GetMethods(BindingsAll);
            foreach (var method in methods)
            {
                if (Method.GetSignature(method) == signature)
                {
                    return new PickledMethodInfoRef(method);
                }
            }

            throw new Exception($"Could not load method '{signature}' from type '{Type.Name}'");
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

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }

        public override PickledConstructorInfo GetConstructor(string signature)
        {
            throw new NotImplementedException();
        }

        public override PickledMethodInfo GetMethod(string signature)
        {
            throw new NotImplementedException();
        }

        public override PickledFieldInfo GetField(string name)
        {
            throw new NotImplementedException();
        }

        public override Type Type { get; }
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

        public override PickledConstructorInfo GetConstructor(string signature)
        {
            throw new NotImplementedException();
        }

        public override PickledMethodInfo GetMethod(string signature)
        {
            throw new NotImplementedException();
        }

        public override PickledFieldInfo GetField(string name)
        {
            throw new NotImplementedException();
        }

        public override Type Type
        {
            get
            {
                if (DeclaringType.IsCreated)
                {
                    return DeclaringType.Type.GenericTypeArguments[Position];
                }

                return DeclaringType.GenericParameters[Position];
            }
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
            return _type;
        }

        public bool IsCreated { get { return _type != null; } }

        public TypeDef TypeDef { get; private set; }
        public override Type Type
        {
            get
            {
                return _type ?? TypeBuilder;
            }
        }
        public TypeBuilder TypeBuilder { get; }
        public GenericTypeParameterBuilder[] GenericParameters { get; set; }
        public PickledFieldInfoDef[] Fields { get; set; }
        public PropertyBuilder[] Properties { get; set; }
        public PickledMethodInfoDef[] Methods { get; set; }
        public PickledConstructorInfoDef[] Constructors { get; set; }

        public override string ToString()
        {
            return TypeBuilder.Name;
        }

        public override PickledConstructorInfo GetConstructor(string signature)
        {
            foreach (var constructor in Constructors)
            {
                if (constructor.GetSignature() == signature)
                {
                    return constructor;
                }
            }

            throw new Exception($"Could not load constructor '{signature}' from type '{TypeBuilder.Name}'");
        }

        public override PickledMethodInfo GetMethod(string signature)
        {
            foreach (var method in Methods)
            {
                if (method.GetSignature() == signature)
                {
                    return method;
                }
            }

            throw new Exception($"Could not load method '{signature}' from type '{TypeBuilder.Name}'");
        }

        public override PickledFieldInfo GetField(string name)
        {
            foreach (var field in Fields)
            {
                if (field.FieldBuilder.Name == name)
                {
                    return field;
                }
            }

            throw new Exception($"Could not load field '{name}' from type '{TypeBuilder.Name}'");
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            return new PickledGenericParameterDef(this, position);
        }
    }

    abstract class PickledMethodInfo : PickledMethodBase
    {
        public override MethodBase MethodBase { get { return MethodInfo; } }

        public abstract MethodInfo MethodInfo { get; }

        public virtual object Invoke(object? target, params object[] parameters)
        {
            return MethodInfo.Invoke(target, parameters);
        }

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
                    if (Method.GetSignature(method) == signature)
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

        public override string GetSignature()
        {
            var signature = new StringBuilder();

            signature.Append(MethodBuilder.Name);

            if (GenericParameters != null)
            {
                signature.Append("<");
                bool first = true;
                foreach (var param in GenericParameters)
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;
                    signature.Append(param.Name);
                }
                signature.Append(">");
            }

            {
                signature.Append("(");
                if (ParameterTypes != null)
                {
                    bool first = true;
                    foreach (var parameterType in ParameterTypes)
                    {
                        if (!first)
                        {
                            signature.Append(", ");
                        }
                        first = false;

                        if (parameterType.IsGenericParameter)
                        {
                            if (parameterType.DeclaringMethod != null)
                            {
                                signature.Append("!!");
                            }
                            else if (parameterType.DeclaringType != null)
                            {
                                signature.Append("!");
                            }
                            else
                            {
                                throw new Exception("Generic paramater had neither a DeclaringMethod or a DeclaringType!");
                            }
                            signature.Append(parameterType.GenericParameterPosition);
                        }
                        else if (parameterType.FullName != null)
                        {
                            signature.Append(parameterType.FullName);
                        }
                        else
                        {
                            signature.Append(parameterType.Namespace + parameterType.Name);
                        }
                    }
                }
                signature.Append(")");
            }

            return signature.ToString();
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

        (Type, bool) ResolveType()
        {
            var genericArguments = new Type[GenericArguments.Length];
            bool isComplete = true;
            for (int i = 0; i < genericArguments.Length; ++i)
            {
                var argument = GenericArguments[i];
                if (argument is PickledTypeInfoRef)
                {
                    genericArguments[i] = argument.Type;
                }
                else if (argument is PickledTypeInfoDef)
                {
                    var constructingType = (PickledTypeInfoDef)argument;
                    isComplete &= constructingType.IsCreated;
                    genericArguments[i] = constructingType.Type;
                }
                else if (argument is PickledGenericType)
                {
                    var constructingType = (PickledGenericType)argument;
                    var (type, complete) = constructingType.ResolveType();
                    isComplete &= complete;
                    genericArguments[i] = type;
                }
                else if (argument is PickledGenericParameterRef)
                {
                    var constructingType = (PickledGenericParameterRef)argument;
                    genericArguments[i] = constructingType.Type;
                }
                else if (argument is PickledGenericParameterDef)
                {
                    var constructingType = (PickledGenericParameterDef)argument;
                    isComplete &= constructingType.DeclaringType.IsCreated;
                    genericArguments[i] = constructingType.Type;
                }
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

        public override Type Type => ResolveType().Item1;

        public PickledTypeInfo[] GenericArguments { get; set; }

        public override PickledConstructorInfo GetConstructor(string signature)
        {
            var (type, isComplete) = ResolveType();

            if (isComplete)
            {
                var infoRef = new PickledTypeInfoRef(type);
                return infoRef.GetConstructor(signature);
            }
            else
            {
                var constructorInfo = GenericType.GetConstructor(signature);
                return new PickledConstructorInfoRef(TypeBuilder.GetConstructor(type, constructorInfo.ConstructorInfo));
            }
        }

        public override PickledMethodInfo GetMethod(string signature)
        {
            var (type, isComplete) = ResolveType();

            if (isComplete)
            {
                var infoRef = new PickledTypeInfoRef(type);
                return infoRef.GetMethod(signature);
            }
            else
            {
                var methodInfo = GenericType.GetMethod(signature);
                return new PickledMethodInfoRef(TypeBuilder.GetMethod(type, methodInfo.MethodInfo));
            }
        }

        public override PickledFieldInfo GetField(string name)
        {
            var (type, isComplete) = ResolveType();

            if (isComplete)
            {
                var infoRef = new PickledTypeInfoRef(type);
                return infoRef.GetField(name);
            }
            else
            {
                var fieldInfo = GenericType.GetField(name);
                return new PickledFieldInfoRef(TypeBuilder.GetField(type, fieldInfo.FieldInfo));
            }
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
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

        public override string ToString()
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

        public abstract PickledTypeInfo GetGenericArgument(int position);
        public abstract void Emit(ILGenerator ilGenerator, OpCode opCode);
    }

    abstract class PickledMethodBase : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return MethodBase; } }

        public abstract MethodBase MethodBase { get; }

        public virtual string GetSignature()
        {
            var signature = new StringBuilder();

            // We want the open method handle
            MethodBase methodBase = MethodBase;
            if (methodBase.DeclaringType != null)
            {
                if (methodBase.DeclaringType.IsConstructedGenericType)
                {
                    var genericType = methodBase.DeclaringType.GetGenericTypeDefinition();
                    methodBase = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, genericType.TypeHandle);
                }
            }

            signature.Append(methodBase.Name);

            if (methodBase.IsGenericMethod)
            {
                signature.Append("<");
                bool first = true;
                foreach (var param in methodBase.GetGenericArguments())
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;
                    signature.Append(param.Name);
                }
                signature.Append(">");
            }

            {
                signature.Append("(");
                bool first = true;
                foreach (var param in methodBase.GetParameters())
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;

                    var parameterType = param.ParameterType;
                    if (parameterType.IsGenericParameter)
                    {
                        if (parameterType.DeclaringMethod != null)
                        {
                            signature.Append("!!");
                        }
                        else if (parameterType.DeclaringType != null)
                        {
                            signature.Append("!");
                        }
                        else
                        {
                            throw new Exception("Generic paramater had neither a DeclaringMethod or a DeclaringType!");
                        }
                        signature.Append(parameterType.GenericParameterPosition);
                    }
                    else if (parameterType.FullName != null)
                    {
                        signature.Append(parameterType.FullName);
                    }
                    else
                    {
                        signature.Append(parameterType.Namespace + parameterType.Name);
                    }
                }
                signature.Append(")");
            }

            return signature.ToString();
        }
    }

    abstract class PickledConstructorInfo : PickledMethodBase
    {
        public override MethodBase MethodBase { get { return ConstructorInfo; } }

        public abstract ConstructorInfo ConstructorInfo { get; }

        public virtual object Invoke(params object[] parameters)
        {
            return ConstructorInfo.Invoke(parameters);
        }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, ConstructorInfo);
        }
        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotSupportedException("Constructors do not have generic arguments");
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

                return ConstructingType.Type.GetConstructor(ParameterTypes ?? Type.EmptyTypes);
            }
        }

        public override string GetSignature()
        {
            var signature = new StringBuilder();

            signature.Append(ConstructorBuilder.Name);

            {
                signature.Append("(");
                if (ParameterTypes != null)
                {
                    bool first = true;
                    foreach (var parameterType in ParameterTypes)
                    {
                        if (!first)
                        {
                            signature.Append(", ");
                        }
                        first = false;

                        if (parameterType.IsGenericParameter)
                        {
                            if (parameterType.DeclaringType != null)
                            {
                                signature.Append("!");
                            }
                            else
                            {
                                throw new Exception("Generic paramater had no DeclaringType!");
                            }
                            signature.Append(parameterType.GenericParameterPosition);
                        }
                        else if (parameterType.FullName != null)
                        {
                            signature.Append(parameterType.FullName);
                        }
                        else
                        {
                            signature.Append(parameterType.Namespace + parameterType.Name);
                        }
                    }
                }
                signature.Append(")");
            }

            return signature.ToString();
        }

        public PickledTypeInfoDef ConstructingType { get; }
        public ConstructorBuilder ConstructorBuilder { get; }
        public ParameterBuilder[]? Parameters { get; }
        public Type[]? ParameterTypes { get; }
        public PickledTypeInfo[]? Locals { get; }
    }

    abstract class PickledFieldInfo : PickledMemberInfo
    {
        public override MemberInfo MemberInfo { get { return FieldInfo; } }

        public abstract FieldInfo FieldInfo { get; }

        public override void Emit(ILGenerator ilGenerator, OpCode opCode)
        {
            ilGenerator.Emit(opCode, FieldInfo);
        }

        public override PickledTypeInfo GetGenericArgument(int position)
        {
            throw new NotImplementedException();
        }
    }

    sealed class PickledFieldInfoRef : PickledFieldInfo
    {
        public override FieldInfo FieldInfo { get; }

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

        public override FieldInfo FieldInfo
        {
            get
            {
                if (!DeclaringType.IsCreated)
                {
                    return FieldBuilder;
                }

                return DeclaringType.Type.GetField(FieldBuilder.Name);
            }
        }
    }
}
