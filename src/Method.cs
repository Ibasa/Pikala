using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Reflection.Emit;

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
                    method = MethodBase.GetMethodFromHandle(method.MethodHandle, genericType.TypeHandle);
                }
            }

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
                    signature.Append(param.Name);
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
    }
}
