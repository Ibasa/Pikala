using System;
using System.Reflection;
using System.Linq;
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
    }

    abstract class SignatureElement : IEquatable<SignatureElement>
    {
        public abstract bool Equals(SignatureElement other);

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
                var elementType = FromType(type.GetElementType());
                return new SignaturePointer(elementType);
            }
            else if (type.IsArray)
            {
                var elementType = FromType(type.GetElementType());
                return new SignatureArray(type.GetArrayRank(), elementType);
            }
            else if (type.IsByRef)
            {
                var elementType = FromType(type.GetElementType());
                return new SignatureByRef(elementType);
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

        public static SignatureElement[] FromParameters(ParameterInfo[]? parameters)
        {
            if (parameters == null) { return new SignatureElement[0]; }
            var result = new SignatureElement[parameters.Length];
            for (int i = 0; i < parameters.Length; ++i)
            {
                result[i] = FromType(parameters[i].ParameterType);
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

        public override bool Equals(SignatureElement other)
        {
            if (other is SignatureGenericParameter sgp)
            {
                return IsGenericTypeParameter == sgp.IsGenericTypeParameter && GenericParameterPosition == sgp.GenericParameterPosition;
            }
            return false;
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

        public override bool Equals(SignatureElement other)
        {
            if (other is SignaturePointer sp)
            {
                return ElementType.Equals(sp.ElementType);
            }
            return false;
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

        public override bool Equals(SignatureElement other)
        {
            if (other is SignatureByRef sbr)
            {
                return ElementType.Equals(sbr.ElementType);
            }
            return false;
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

        public override bool Equals(SignatureElement other)
        {
            if (other is SignatureArray sa)
            {
                return Rank == sa.Rank && ElementType.Equals(sa.ElementType);
            }
            return false;
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
            Type = type;
        }

        public override bool Equals(SignatureElement other)
        {
            if (other is SignatureType st)
            {
                return Type == st.Type;
            }
            return false;
        }

        public override string ToString()
        {
            return Type.FullName;
        }
    }

    sealed class SignatureConstructedGenericType : SignatureElement
    {
        public Type GenericTypeDefinition { get; private set; }
        public SignatureElement[] GenericArguments { get; private set; }

        public SignatureConstructedGenericType(Type genericTypeDefinition, SignatureElement[] genericArguments)
        {
            System.Diagnostics.Debug.Assert(genericTypeDefinition.IsGenericTypeDefinition);
            GenericTypeDefinition = genericTypeDefinition;
            GenericArguments = genericArguments;
        }

        public override bool Equals(SignatureElement other)
        {
            if (other is SignatureConstructedGenericType scgt)
            {
                if (GenericTypeDefinition != scgt.GenericTypeDefinition)
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
        public string Name { get; private set; }
        public int GenericParameterCount { get; private set; }

        public SignatureElement ReturnType { get; private set; }
        public SignatureElement[] Parameters { get; private set; }

        public Signature(string name, int genericParameterCount, SignatureElement returnType, SignatureElement[] parameters)
        {
            Name = name;
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
                    methodBase = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, genericType.TypeHandle);
                }
            }

            SignatureElement returnType;
            if (methodBase is ConstructorInfo constructor)
            {
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
                methodBase.IsGenericMethod ? methodBase.GetGenericArguments().Length : 0,
                returnType,
                SignatureElement.FromParameters(methodBase.GetParameters()));
        }

        public static Signature GetSignature(PropertyInfo property)
        {
            SignatureElement returnType = SignatureElement.FromType(property.PropertyType);
            return new Signature(
                property.Name,
                0,
                returnType,
                SignatureElement.FromParameters(property.GetIndexParameters()));
        }

        public bool Equals(Signature? other)
        {
            if (other == null) return false;

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
