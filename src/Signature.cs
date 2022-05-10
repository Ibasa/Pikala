using System;
using System.Diagnostics.CodeAnalysis;
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
    }

    // Parameter class of (reqs + opts + type)
    // Get rid of SignatureElement, just use the normal type serialiser (Which means we need to refify on that type with generic context)

    abstract class SignatureElement : IEquatable<SignatureElement>
    {
        public abstract bool Equals(SignatureElement? other);

        public abstract Type Reify(GenericTypeContext typeContext);

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

        public override Type Reify(GenericTypeContext typeContext)
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
            return result;
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

        public override Type Reify(GenericTypeContext typeContext)
        {
            var inner = ElementType.Reify(typeContext);
            return inner.MakePointerType();
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

        public override Type Reify(GenericTypeContext typeContext)
        {
            var inner = ElementType.Reify(typeContext);
            return inner.MakeByRefType();
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

        public override Type Reify(GenericTypeContext typeContext)
        {
            var inner = ElementType.Reify(typeContext);
            if (Rank == 1)
            {
                return inner.MakeArrayType();
            }
            return inner.MakeArrayType(Rank);
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

        public override Type Reify(GenericTypeContext typeContext)
        {
            return Type;
        }

        public override string ToString()
        {
            return Type.FullName!;
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

        public override Type Reify(GenericTypeContext typeContext)
        {
            var genericArguments = new Type[GenericArguments.Length];
            for (int i = 0; i < GenericArguments.Length; ++i)
            {
                genericArguments[i] = GenericArguments[i].Reify(typeContext);
            }

            return GenericTypeDefinition.MakeGenericType(genericArguments);
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

    sealed class SignatureLocation : IEquatable<SignatureLocation>
    {
        // We might have a name, and if we do we can use it to improve ToString results
        public string? Name { get; }

        public SignatureElement Element { get; }

        public System.Collections.Immutable.IImmutableSet<Type> RequiredCustomModifiers { get; }
        public System.Collections.Immutable.IImmutableSet<Type> OptionalCustomModifiers { get; }

        private sealed class TypeComparer : System.Collections.Generic.Comparer<Type>
        {
            private static int CompareStrings(string? x, string? y)
            {
                if (x == null && y == null) return 0;
                else if (x == null && y != null) return -1;
                else if (x != null && y == null) return 1;

                return x!.CompareTo(y);
            }

            public override int Compare([AllowNull] Type x, [AllowNull] Type y)
            {
                if (x == null && y == null) return 0;
                if (x == null && y != null) return -1;
                if (x != null && y == null) return 1;

                var cmp = CompareStrings(x.Assembly.FullName, y.Assembly.FullName);
                if (cmp != 0) return cmp;

                cmp = CompareStrings(x.FullName, y.FullName);
                if (cmp != 0) return cmp;

                cmp = CompareStrings(x.Namespace, y.Namespace);
                if (cmp != 0) return cmp;

                return CompareStrings(x.Name, y.Name);
            }
        }

        private static readonly TypeComparer Comparer = new TypeComparer();

        public SignatureLocation(SignatureElement element, Type[] requiredCustomModifiers, Type[] optionalCustomModifiers, string? name)
        {
            Name = name;
            Element = element;
            RequiredCustomModifiers = System.Collections.Immutable.ImmutableSortedSet.ToImmutableSortedSet(requiredCustomModifiers, Comparer);
            OptionalCustomModifiers = System.Collections.Immutable.ImmutableSortedSet.ToImmutableSortedSet(optionalCustomModifiers, Comparer);
        }

        public static SignatureLocation FromParameter(ParameterInfo parameter)
        {
            return FromParameter(parameter.ParameterType, parameter.GetRequiredCustomModifiers(), parameter.GetOptionalCustomModifiers(), parameter.Name);
        }

        public static SignatureLocation FromParameter(Type type, Type[] requiredCustomModifiers, Type[] optionalCustomModifiers, string? name)
        {
            var elementType = SignatureElement.FromType(type);
            return new SignatureLocation(elementType, requiredCustomModifiers, optionalCustomModifiers, name);
        }

        public static SignatureLocation[] FromParameters(ParameterInfo[] parameters)
        {
            var result = new SignatureLocation[parameters.Length];
            for (int i = 0; i < parameters.Length; ++i)
            {
                result[i] = FromParameter(parameters[i]);
            }
            return result;
        }

        public bool Equals(SignatureLocation? other)
        {
            if (other == null) return false;

            if (!Element.Equals(other.Element)) return false;
            if (!RequiredCustomModifiers.SetEquals(other.RequiredCustomModifiers)) return false;
            if (!OptionalCustomModifiers.SetEquals(other.OptionalCustomModifiers)) return false;

            return true;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();

            builder.Append(Element.ToString());

            foreach (var mod in RequiredCustomModifiers)
            {
                builder.Append(" modreq ");
                builder.Append(mod.ToString());
            }

            foreach (var mod in OptionalCustomModifiers)
            {
                builder.Append(" modopt ");
                builder.Append(mod.ToString());
            }

            if (Name != null)
            {
                builder.Append(" ");
                builder.Append(Name);
            }

            return builder.ToString();
        }
        public (Type, Type[], Type[]) Reify(GenericTypeContext typeContext)
        {
            var element = Element.Reify(typeContext);
            return (element, System.Linq.Enumerable.ToArray(RequiredCustomModifiers), System.Linq.Enumerable.ToArray(OptionalCustomModifiers));
        }

        // TODO These are wrong and shouldn't be used
        public static SignatureLocation FromType(Type type)
        {
            var elementType = SignatureElement.FromType(type);
            return new SignatureLocation(elementType, Type.EmptyTypes, Type.EmptyTypes, null);
        }

        public static SignatureLocation[] FromTypes(Type[]? types)
        {
            if (types == null) { return new SignatureLocation[0]; }
            var result = new SignatureLocation[types.Length];
            for (int i = 0; i < types.Length; ++i)
            {
                result[i] = FromType(types[i]);
            }
            return result;
        }
    }

    sealed class Signature : IEquatable<Signature>
    {
        public string Name { get; }
        public CallingConventions CallingConvention { get; }
        public int GenericParameterCount { get; }

        public SignatureLocation ReturnType { get; }
        public SignatureLocation[] Parameters { get; }

        public Signature(string name, CallingConventions callingConvention, int genericParameterCount, SignatureLocation returnType, SignatureLocation[] parameters)
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

            SignatureLocation returnType;
            if (methodBase is ConstructorInfo constructor)
            {
                System.Diagnostics.Debug.Assert(constructor.DeclaringType != null);
                returnType = SignatureLocation.FromParameter(constructor.DeclaringType, Type.EmptyTypes, Type.EmptyTypes, null);
            }
            else if (methodBase is MethodInfo method)
            {
                returnType = SignatureLocation.FromParameter(method.ReturnParameter);
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
                SignatureLocation.FromParameters(methodBase.GetParameters()));
        }

        public static Signature GetSignature(PropertyInfo property)
        {
            var accessors = property.GetAccessors(true);
            if (accessors.Length == 0)
            {
                throw new InvalidOperationException($"Property {property.Name} had no accessor methods");
            }
            var callingConvention = accessors[0].CallingConvention;

            SignatureLocation returnType = SignatureLocation.FromParameter(property.PropertyType, property.GetRequiredCustomModifiers(), property.GetOptionalCustomModifiers(), null);
            return new Signature(
                property.Name,
                callingConvention,
                0,
                returnType,
                SignatureLocation.FromParameters(property.GetIndexParameters()));
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
