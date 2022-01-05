using System;
using System.Reflection;
using System.Text;

namespace Ibasa.Pikala
{
    static class Method
    {
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

            var returnType = (method is MethodInfo methodInfo) ? methodInfo.ReturnType: typeof(void);
            if (returnType.IsGenericParameter)
            {
                if (returnType.DeclaringMethod != null)
                {
                    signature.Append("!!");
                }
                else if (returnType.DeclaringType != null)
                {
                    signature.Append("!");
                }
                else
                {
                    throw new Exception("Generic paramater had neither a DeclaringMethod or a DeclaringType!");
                }
                signature.Append(returnType.GenericParameterPosition);
            }
            else if (returnType.FullName != null)
            {
                signature.Append(returnType.FullName);
            }
            else
            {
                signature.Append(returnType.Namespace + returnType.Name);
            }
            signature.Append(" ");

            signature.Append(method.Name);

            if (method.IsGenericMethod)
            {
                signature.Append("<");
                bool first = true;
                foreach (var param in method.GetGenericArguments())
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;
                    // TypeVar names don't make up part of the signature, but when we do the signature
                    // redesign we probably want to keep them for pretty printing but not for equality checks.
                    //signature.Append(param.Name);
                }
                signature.Append(">");
            }

            {
                signature.Append("(");
                bool first = true;
                foreach (var param in method.GetParameters())
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

        public static string GetSignature(PropertyInfo property)
        {
            var signature = new StringBuilder();

            var returnType = property.PropertyType;
            if (returnType.IsGenericParameter)
            {
                if (returnType.DeclaringType != null)
                {
                    signature.Append("!");
                }
                else
                {
                    throw new Exception("Generic paramater has no DeclaringType!");
                }
                signature.Append(returnType.GenericParameterPosition);
            }
            else if (returnType.FullName != null)
            {
                signature.Append(returnType.FullName);
            }
            else
            {
                signature.Append(returnType.Namespace + returnType.Name);
            }
            signature.Append(" ");

            signature.Append(property.Name);

            {
                signature.Append("[");
                bool first = true;
                foreach (var param in property.GetIndexParameters())
                {
                    if (!first)
                    {
                        signature.Append(", ");
                    }
                    first = false;

                    var parameterType = param.ParameterType;
                    if (parameterType.IsGenericParameter)
                    {
                        if (parameterType.DeclaringType != null)
                        {
                            signature.Append("!");
                        }
                        else
                        {
                            throw new Exception("Generic paramater has no DeclaringType!");
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
                signature.Append("]");
            }

            return signature.ToString();
        }
    }
}
