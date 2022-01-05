using System;
using System.Reflection;
using System.Text;

namespace Ibasa.Pikala
{
    static class Method
    {
        public static void AppendType(StringBuilder signature, Type type)
        {
            if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod != null)
                {
                    signature.Append("!!");
                }
                else if (type.DeclaringType != null)
                {
                    signature.Append('!');
                }
                else
                {
                    throw new Exception("Generic paramater had neither a DeclaringMethod or a DeclaringType!");
                }
                signature.Append(type.GenericParameterPosition);
            }
            else
            {
                if (type.DeclaringType != null)
                {
                    AppendType(signature, type.DeclaringType);
                    signature.Append('+');
                    signature.Append(type.Name);
                }
                else if (string.IsNullOrEmpty(type.Namespace))
                {
                    signature.Append(type.Name);
                }
                else
                {
                    signature.Append(type.Namespace);
                    signature.Append('.');
                    signature.Append(type.Name);
                }

                if (type.IsGenericType)
                {
                    signature.Append('<');
                    bool first = true;
                    foreach (var arg in type.GetGenericArguments())
                    {
                        if (!first)
                        {
                            signature.Append(',');
                        }
                        first = false;

                        AppendType(signature, arg);
                    }
                    signature.Append('>');
                }
            }
        }

        public static string GetSignature(MethodBase method)
        {
            var signature = new StringBuilder();

            // We want the open method handle
            if (method.DeclaringType != null)
            {
                if (method.DeclaringType.IsConstructedGenericType)
                {
                    var genericType = method.DeclaringType.GetGenericTypeDefinition();
                    method = MethodBase.GetMethodFromHandle(method.MethodHandle, genericType.TypeHandle)!;
                }
            }

            AppendType(signature, (method is MethodInfo methodInfo) ? methodInfo.ReturnType : typeof(void));
            signature.Append(' ');

            signature.Append(method.Name);

            if (method.IsGenericMethod)
            {
                signature.Append('<');
                bool first = true;
                foreach (var param in method.GetGenericArguments())
                {
                    if (!first)
                    {
                        signature.Append(',');
                    }
                    first = false;
                    // TypeVar names don't make up part of the signature, but when we do the signature
                    // redesign we probably want to keep them for pretty printing but not for equality checks.
                    //signature.Append(param.Name);
                }
                signature.Append('>');
            }

            {
                signature.Append('(');
                bool first = true;
                foreach (var param in method.GetParameters())
                {
                    if (!first)
                    {
                        signature.Append(',');
                    }
                    first = false;

                    AppendType(signature, param.ParameterType);
                }
                signature.Append(')');
            }

            return signature.ToString();
        }

        public static string GetSignature(PropertyInfo property)
        {
            var signature = new StringBuilder();

            AppendType(signature, property.PropertyType);
            signature.Append(' ');
            signature.Append(property.Name);

            {
                signature.Append('(');
                bool first = true;
                foreach (var param in property.GetIndexParameters())
                {
                    if (!first)
                    {
                        signature.Append(',');
                    }
                    first = false;

                    AppendType(signature, param.ParameterType);
                }
                signature.Append(')');
            }

            return signature.ToString();
        }
    }
}
