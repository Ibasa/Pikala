using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace Ibasa.Pikala
{
    public static class AssemblyLoadContextExtensions
    {
        /// <summary>
        /// Defines a dynamic assembly that has the specified name and access rights.
        /// </summary>
        /// <param name="assemblyLoadContext">The AssemblyLoadContext to load the defined assembly into.</param>
        /// <param name="name">The name of the assembly.</param>
        /// <param name="access">The access rights of the assembly.</param>
        /// <returns>An object that represents the new assembly.</returns>
        public static AssemblyBuilder DefineDynamicAssembly(this AssemblyLoadContext assemblyLoadContext, AssemblyName name, AssemblyBuilderAccess access)
        {
            // For runtime 6.0 onwards we can set our AssemblyLoadContext as the current contextual context and then call DefineDynamicAssembly
            using var scope = assemblyLoadContext.EnterContextualReflection();
            return AssemblyBuilder.DefineDynamicAssembly(name, access);
        }
    }
}
