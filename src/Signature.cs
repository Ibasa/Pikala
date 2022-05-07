using System;
using System.Reflection;
using System.Text;

namespace Ibasa.Pikala
{

    enum SignatureElementOperation : byte
    {
        Type = 0,
        TVar = 1,
        MVar = 2,
        Generic = 3,
        Array = 4,
        ByRef = 5,
        Pointer = 6,
        Modreq = 7,
        Modopt = 8,
    }

    abstract class SignatureElement : IEquatable<SignatureElement>
    {
        public abstract bool Equals(SignatureElement? other);

        public abstract (Type, Type[], Type[]) Reify(GenericTypeContext typeContext);

        public static SignatureElement FromType(Type type)
        {
            if (type.IsGenericParameter)
            {
                return new SignatureGenericParameter(type.IsGenericTypeParameter, type.GenericParameterPosition);
            }
            else if (type.IsConstructedGenericType)
            {
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                var genericArguments = FromTypes(type.GetGenericArguments());
                return new SignatureConstructedGenericType(genericTypeDefinition, genericArguments);
            }
            else if (type.IsPointer)
            {
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null);
                return new SignaturePointer(FromType(elementType));
            }
            else if (type.IsArray)
            {
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null);
                return new SignatureArray(type.GetArrayRank(), FromType(elementType));
            }
            else if (type.IsByRef)
            {
                var elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null);
                return new SignatureByRef(FromType(elementType));
            }
            return new SignatureType(type);
        }

        public static SignatureElement[] FromTypes(Type[]? types)
        {
            if (types == null) { return new SignatureElement[0]; }
            var result = new SignatureElement[types.Length];
            for (int i = 0; i < types.Length; ++i)
            {
                result[i] = FromType(types[i]);
            }
            return result;
        }

        public static SignatureElement FromParameter(ParameterInfo parameter)
        {
            var innerType = FromType(parameter.ParameterType);
            var reqs = parameter.GetRequiredCustomModifiers();
            var opts = parameter.GetOptionalCustomModifiers();

            var result = innerType;
            foreach (var req in reqs)
            {
                result = new SignatureReq(result, req);
            }
            foreach (var opt in opts)
            {
                result = new SignatureOpt(result, opt);
            }
            return result;
        }

        public static SignatureElement[] FromParameters(ParameterInfo[] parameters)
        {
            var result = new SignatureElement[parameters.Length];
            for (int i = 0; i < parameters.Length; ++i)
            {
                result[i] = FromParameter(parameters[i]);
            }
            return result;
        }
    }

    sealed class SignatureGenericParameter : SignatureElement
    {
        public bool IsGenericTypeParameter { get; private set; }
        public int GenericParameterPosition { get; private set; }

        public SignatureGenericParameter(bool isGenericTypeParameter, int genericParameterPosition)
        {
            IsGenericTypeParameter = isGenericTypeParameter;
            GenericParameterPosition = genericParameterPosition;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureGenericParameter sgp)
            {
                return IsGenericTypeParameter == sgp.IsGenericTypeParameter && GenericParameterPosition == sgp.GenericParameterPosition;
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            Type result;
            if (IsGenericTypeParameter)
            {
                result = typeContext.GenericTypeParameters![GenericParameterPosition];
            }
            else
            {
                result = typeContext.GenericMethodParameters![GenericParameterPosition];
            }
            return (result, Type.EmptyTypes, Type.EmptyTypes);
        }

        public override string ToString()
        {
            return $"{(IsGenericTypeParameter ? "!" : "!!")}{GenericParameterPosition}";
        }
    }

    sealed class SignaturePointer : SignatureElement
    {
        public SignatureElement ElementType { get; private set; }

        public SignaturePointer(SignatureElement elementType)
        {
            ElementType = elementType;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignaturePointer sp)
            {
                return ElementType.Equals(sp.ElementType);
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var (inner, reqs, opts) = ElementType.Reify(typeContext);
            return (inner.MakePointerType(), reqs, opts);
        }

        public override string ToString()
        {
            return $"{ElementType}*";
        }
    }

    sealed class SignatureByRef : SignatureElement
    {
        public SignatureElement ElementType { get; private set; }

        public SignatureByRef(SignatureElement elementType)
        {
            ElementType = elementType;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureByRef sbr)
            {
                return ElementType.Equals(sbr.ElementType);
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var (inner, reqs, opts) = ElementType.Reify(typeContext);
            return (inner.MakeByRefType(), reqs, opts);
        }

        public override string ToString()
        {
            return $"{ElementType}&";
        }
    }

    sealed class SignatureArray : SignatureElement
    {
        public int Rank { get; private set; }
        public SignatureElement ElementType { get; private set; }

        public SignatureArray(int rank, SignatureElement elementType)
        {
            ElementType = elementType;
            Rank = rank;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureArray sa)
            {
                return Rank == sa.Rank && ElementType.Equals(sa.ElementType);
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var (inner, reqs, opts) = ElementType.Reify(typeContext);
            if (Rank == 1)
            {
                return (inner.MakeArrayType(), reqs, opts);
            }
            return (inner.MakeArrayType(Rank), reqs, opts);
        }

        public override string ToString()
        {
            return $"{ElementType}[{new string(',', Rank - 1)}]";
        }
    }

    sealed class SignatureType : SignatureElement
    {
        public Type Type { get; private set; }

        public SignatureType(Type type)
        {
            System.Diagnostics.Debug.Assert(!type.IsGenericParameter);
            System.Diagnostics.Debug.Assert(!type.IsConstructedGenericType);
            System.Diagnostics.Debug.Assert(!type.HasElementType);
            System.Diagnostics.Debug.Assert(type.FullName != null);
            Type = type;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureType st)
            {
                // Check they have the same module
                if (Type.Module != st.Type.Module) return false;
                // Check the have the same name
                // This is so that we match up TypeBuilders to their created types
                return Type.FullName == st.Type.FullName;
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            return (Type, Type.EmptyTypes, Type.EmptyTypes);
        }

        public override string ToString()
        {
            return Type.FullName!;
        }
    }

    sealed class SignatureReq : SignatureElement
    {
        public SignatureElement ElementType { get; private set; }
        public Type RequiredModifier { get; private set; }

        public SignatureReq(SignatureElement elementType, Type req)
        {
            ElementType = elementType;
            RequiredModifier = req;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureReq sr)
            {
                return ElementType.Equals(sr.ElementType) && RequiredModifier == sr.RequiredModifier;
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var (inner, reqs, opts) = ElementType.Reify(typeContext);
            var newReqs = new Type[reqs.Length + 1];
            reqs.CopyTo(newReqs, 0);
            newReqs[newReqs.Length - 1] = RequiredModifier;
            return (inner, newReqs, opts);
        }

        public override string ToString()
        {
            return $"{ElementType} modreq {RequiredModifier}";
        }
    }

    sealed class SignatureOpt : SignatureElement
    {
        public SignatureElement ElementType { get; private set; }
        public Type OptionalModifier { get; private set; }

        public SignatureOpt(SignatureElement elementType, Type opt)
        {
            ElementType = elementType;
            OptionalModifier = opt;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureOpt so)
            {
                return ElementType.Equals(so.ElementType) && OptionalModifier == so.OptionalModifier;
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var (inner, reqs, opts) = ElementType.Reify(typeContext);
            var newOpts = new Type[opts.Length + 1];
            opts.CopyTo(newOpts, 0);
            newOpts[newOpts.Length - 1] = OptionalModifier;
            return (inner, reqs, newOpts);
        }

        public override string ToString()
        {
            return $"{ElementType} modopt {OptionalModifier}";
        }
    }

    sealed class SignatureConstructedGenericType : SignatureElement
    {
        public Type GenericTypeDefinition { get; private set; }
        public SignatureElement[] GenericArguments { get; private set; }

        public SignatureConstructedGenericType(Type genericTypeDefinition, SignatureElement[] genericArguments)
        {
            System.Diagnostics.Debug.Assert(genericTypeDefinition.IsGenericTypeDefinition);
            System.Diagnostics.Debug.Assert(genericTypeDefinition.FullName != null);
            GenericTypeDefinition = genericTypeDefinition;
            GenericArguments = genericArguments;
        }

        public override bool Equals(SignatureElement? other)
        {
            if (other is SignatureConstructedGenericType scgt)
            {
                if (GenericTypeDefinition.Module != scgt.GenericTypeDefinition.Module)
                {
                    return false;
                }
                if (GenericTypeDefinition.FullName != scgt.GenericTypeDefinition.FullName)
                {
                    return false;
                }
                if (GenericArguments.Length != scgt.GenericArguments.Length)
                {
                    return false;
                }
                for (int i = 0; i < GenericArguments.Length; ++i)
                {
                    if (!GenericArguments[i].Equals(scgt.GenericArguments[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        public override (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var genericArguments = new Type[GenericArguments.Length];
            for (int i = 0; i < GenericArguments.Length; ++i)
            {
                var (genericArgument, reqs, opts) = GenericArguments[i].Reify(typeContext);
                if (reqs.Length != 0)
                {
                    throw new InvalidOperationException("Unexpected required modifer on generic argument");
                }
                if (opts.Length != 0)
                {
                    throw new InvalidOperationException("Unexpected optional modifer on generic argument");
                }
                genericArguments[i] = genericArgument;
            }

            return (GenericTypeDefinition.MakeGenericType(genericArguments), Type.EmptyTypes, Type.EmptyTypes);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(GenericTypeDefinition.FullName);
            builder.Append('[');
            var first = true;
            foreach (var genericArgument in GenericArguments)
            {
                if (!first)
                {
                    builder.Append(", ");
                }
                first = false;
                builder.Append(genericArgument);
            }
            builder.Append(']');
            return builder.ToString();
        }
    }

    sealed class Signature : IEquatable<Signature>
    {
        public string Name { get; }
        public CallingConventions CallingConvention { get; }
        public int GenericParameterCount { get; }

        public SignatureElement ReturnType { get; }
        public SignatureElement[] Parameters { get; }

        public Signature(string name, CallingConventions callingConvention, int genericParameterCount, SignatureElement returnType, SignatureElement[] parameters)
        {
            Name = name;
            CallingConvention = callingConvention;
            GenericParameterCount = genericParameterCount;
            ReturnType = returnType;
            Parameters = parameters;
        }

        public static bool operator !=(Signature? a, Signature? b)
        {
            return !(a == b);
        }

        public static bool operator ==(Signature? a, Signature? b)
        {
            if (Object.ReferenceEquals(a, null) && Object.ReferenceEquals(b, null))
            {
                return true;
            }
            if (Object.ReferenceEquals(a, null) || Object.ReferenceEquals(b, null))
            {
                return false;
            }
            return a.Equals(b);
        }

        public static Signature GetSignature(MethodBase methodBase)
        {
            // We want the open method handle
            if (methodBase.DeclaringType != null)
            {
                if (methodBase.DeclaringType.IsConstructedGenericType)
                {
                    var genericType = methodBase.DeclaringType.GetGenericTypeDefinition();
                    methodBase = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, genericType.TypeHandle)!;
                }
            }

            SignatureElement returnType;
            if (methodBase is ConstructorInfo constructor)
            {
                System.Diagnostics.Debug.Assert(constructor.DeclaringType != null);
                returnType = SignatureElement.FromType(constructor.DeclaringType);
            }
            else if (methodBase is MethodInfo method)
            {
                returnType = SignatureElement.FromType(method.ReturnType);
            }
            else
            {
                throw new NotImplementedException("Got a MethodBase that wasn't a ConstructorInfo or MethodInfo");
            }

            return new Signature(
                methodBase.Name,
                methodBase.CallingConvention,
                methodBase.IsGenericMethod ? methodBase.GetGenericArguments().Length : 0,
                returnType,
                SignatureElement.FromParameters(methodBase.GetParameters()));
        }

        public static Signature GetSignature(PropertyInfo property)
        {
            var accessors = property.GetAccessors(true);
            if (accessors.Length == 0)
            {
                throw new InvalidOperationException($"Property {property.Name} had no accessor methods");
            }
            var callingConvention = accessors[0].CallingConvention;

            SignatureElement returnType = SignatureElement.FromType(property.PropertyType);
            return new Signature(
                property.Name,
                callingConvention,
                0,
                returnType,
                SignatureElement.FromParameters(property.GetIndexParameters()));
        }

        public bool Equals(Signature? other)
        {
            if (other == null) return false;

            if (CallingConvention != other.CallingConvention) return false;

            if (Name != other.Name) return false;

            if (GenericParameterCount != other.GenericParameterCount) return false;

            if (Parameters.Length != other.Parameters.Length) return false;

            if (!ReturnType.Equals(other.ReturnType)) return false;

            for (int i = 0; i < Parameters.Length; ++i)
            {
                if (!Parameters[i].Equals(other.Parameters[i])) return false;
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            if (obj is Signature signature)
            {
                return Equals(signature);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, GenericParameterCount, Parameters);
        }

        public override string ToString()
        {
            var signature = new StringBuilder();

            signature.Append(ReturnType);
            signature.Append(" ");

            signature.Append(Name);

            if (GenericParameterCount != 0)
            {
                signature.Append("<");
                bool first = true;
                for (int i = 0; i < GenericParameterCount; ++i)
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;
                    signature.Append($"T{i}");
                }
                signature.Append(">");
            }

            {
                signature.Append("(");
                bool first = true;
                foreach (var param in Parameters)
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;

                    signature.Append(param.ToString());
                }
                signature.Append(")");
            }

            return signature.ToString();
        }
    }
}
