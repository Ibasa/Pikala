using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace Ibasa.Pikala
{
    public static class AssemblyLoadContextExtensions
    {
        private static Guid _ddaGuid = new Guid("75468177-0C74-489F-967D-AC6BAB6BA7A4");
        private static object _ddaLock = new object();

        private static Assembly? LookupWorkaroundAssembly(AssemblyLoadContext assemblyLoadContext)
        {
            // We need some hackery here. Assemblies.MoveNext can throw :(
            Assembly[]? assemblies;
            do
            {
                try
                {
                    assemblies = System.Linq.Enumerable.ToArray(assemblyLoadContext.Assemblies);
                }
                catch
                {
                    assemblies = null;
                }
            } while (assemblies == null);

            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName();
                if (name.Name == "DefineDynamicAssembly" && assembly.ManifestModule.ModuleVersionId == _ddaGuid)
                {
                    return assembly;
                }
            }
            return null;
        }

        private static Assembly BuildAndLoadWorkaroundAssembly(AssemblyLoadContext assemblyLoadContext)
        {
            var contentId = new System.Reflection.Metadata.BlobContentId(_ddaGuid, 0x04030201);

            var ilBuilder = new System.Reflection.Metadata.BlobBuilder();
            var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();

            metadata.AddAssembly(
                metadata.GetOrAddString("DefineDynamicAssembly"),
                new Version(1, 0, 0, 0),
                default(System.Reflection.Metadata.StringHandle),
                default(System.Reflection.Metadata.BlobHandle),
                flags: 0,
                AssemblyHashAlgorithm.None);

            metadata.AddModule(
                0,
                metadata.GetOrAddString("DefineDynamicAssembly.dll"),
                metadata.GetOrAddGuid(_ddaGuid),
                default(System.Reflection.Metadata.GuidHandle),
                default(System.Reflection.Metadata.GuidHandle));

            var mscorlibName = typeof(object).Assembly.GetName();
            var mscorlibPublicKey = metadata.GetOrAddBlob(mscorlibName.GetPublicKey());

            var mscorlibAssemblyRef = metadata.AddAssemblyReference(
                metadata.GetOrAddString(mscorlibName.Name),
                mscorlibName.Version,
                metadata.GetOrAddString(mscorlibName.CultureName),
                mscorlibPublicKey,
                default(AssemblyFlags),
                default(System.Reflection.Metadata.BlobHandle));

            var assemblyBuilderRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection.Emit"),
                metadata.GetOrAddString("AssemblyBuilder"));

            var assemblyNameRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection"),
                metadata.GetOrAddString("AssemblyName"));

            var assemblyBuilderAccessRef = metadata.AddTypeReference(
                mscorlibAssemblyRef,
                metadata.GetOrAddString("System.Reflection.Emit"),
                metadata.GetOrAddString("AssemblyBuilderAccess"));

            var ddaSignature = new System.Reflection.Metadata.BlobBuilder();
            new System.Reflection.Metadata.Ecma335.BlobEncoder(ddaSignature)
                .MethodSignature()
                .Parameters(2,
                    returnType => returnType.Type().Type(assemblyBuilderRef, false),
                    parameters =>
                    {
                        parameters.AddParameter().Type().Type(assemblyNameRef, false);
                        parameters.AddParameter().Type().Type(assemblyBuilderAccessRef, true);
                    });

            var defineDynamicAssemblyRef = metadata.AddMemberReference(
                assemblyBuilderRef,
                metadata.GetOrAddString("DefineDynamicAssembly"),
                metadata.GetOrAddBlob(ddaSignature));

            var codeBuilder = new System.Reflection.Metadata.BlobBuilder();
            var il = new System.Reflection.Metadata.Ecma335.InstructionEncoder(codeBuilder);
            il.LoadArgument(0);
            il.LoadArgument(1);
            il.Call(defineDynamicAssemblyRef);
            il.OpCode(System.Reflection.Metadata.ILOpCode.Ret);

            var methodBodyStream = new System.Reflection.Metadata.Ecma335.MethodBodyStreamEncoder(ilBuilder);
            var ddaOffset = methodBodyStream.AddMethodBody(il);

            var parameters = metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("name"), 0);
            metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("access"), 1);

            var ddaMethodDef = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL,
                metadata.GetOrAddString("DefineDynamicAssembly"),
                metadata.GetOrAddBlob(ddaSignature),
                ddaOffset,
                parameterList: parameters);

            metadata.AddTypeDefinition(
                default(TypeAttributes),
                default(System.Reflection.Metadata.StringHandle),
                metadata.GetOrAddString("<Module>"),
                baseType: default(System.Reflection.Metadata.EntityHandle),
                fieldList: System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1),
                methodList: ddaMethodDef);

            var metadataRootBuilder = new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata);

            var peHeaderBuilder = new System.Reflection.PortableExecutable.PEHeaderBuilder();
            var peBuilder = new System.Reflection.PortableExecutable.ManagedPEBuilder(
                peHeaderBuilder,
                metadataRootBuilder,
                ilBuilder,
                flags: System.Reflection.PortableExecutable.CorFlags.ILOnly,
                deterministicIdProvider: content => contentId);

            var builder = new System.Reflection.Metadata.BlobBuilder();
            peBuilder.Serialize(builder);

            var memoryStream = new System.IO.MemoryStream();
            builder.WriteContentTo(memoryStream);
            memoryStream.Position = 0;

            return assemblyLoadContext.LoadFromStream(memoryStream);
        }

        /// <summary>
        /// Defines a dynamic assembly that has the specified name and access rights.
        /// </summary>
        /// <param name="assemblyLoadContext">The AssemblyLoadContext to load the defined assembly into.</param>
        /// <param name="name">The name of the assembly.</param>
        /// <param name="access">The access rights of the assembly.</param>
        /// <returns>An object that represents the new assembly.</returns>
        public static AssemblyBuilder DefineDynamicAssembly(this AssemblyLoadContext assemblyLoadContext, AssemblyName name, AssemblyBuilderAccess access)
        {
            if (Environment.Version.Major >= 6)
            {
                // For runtime 6.0 onwards we can set our AssemblyLoadContext as the current contextual context and then call DefineDynamicAssembly
                using var scope = assemblyLoadContext.EnterContextualReflection();
                return AssemblyBuilder.DefineDynamicAssembly(name, access);
            }
            else
            {
                // Else before net6 DefineDynamicAssembly did not check the contextual ALC, it instead used the callers ALC. 

                // If our ALC is the intended ALC we can just call DefineDynamicAssembly
                if (assemblyLoadContext == AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()))
                {
                    return AssemblyBuilder.DefineDynamicAssembly(name, access);
                }

                // Otherwise to get around this we do some "fun" here where we build a tiny assembly with one method to call
                // AssemblyBuilder.DefineDynamicAssembly, load that into the ALC we want to use then invoke the method on it.

                Assembly? ddaAssembly;
                lock (_ddaLock)
                {
                    // Take the lock now because we want to ensure we only build this shim assembly once.
                    // We lock around lookup as well because looking up the assembly requires a traversal over all loaded assemblies and we want to 
                    // minimize the confussion of loading the DDA assembly at the same time as iterating over it (it may be the thing causing AccessViolations).

                    ddaAssembly = LookupWorkaroundAssembly(assemblyLoadContext);
                    if (ddaAssembly == null)
                    {
                        // We haven't created a ddaAssembly for this AssemblyLoadContext. 
                        ddaAssembly = BuildAndLoadWorkaroundAssembly(assemblyLoadContext);
                    }

                    // Assert that the context of our shim assembly does match the ALC we're trying to define a new dynamic assembly on
                    try
                    {
                        System.Diagnostics.Debug.Assert(AssemblyLoadContext.GetLoadContext(ddaAssembly) == assemblyLoadContext, "Failed to load into defined ALC");
                    }
                    catch
                    {
                        // We've seen GetLoadContext spuriously fail, so just ignore checking this assert if that happens
                    }
                }

                // Use reflection to look up the shim DefineDynamicAssembly method and invoke it
                var ddaMethod = ddaAssembly.ManifestModule.GetMethod("DefineDynamicAssembly");
                System.Diagnostics.Debug.Assert(ddaMethod != null, "Failed to GetMethod(\"DefineDynamicAssembly\")");

                return (AssemblyBuilder)ddaMethod.Invoke(null, new object[] { name, access })!;
            }
        }
    }
}
