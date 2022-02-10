using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    /// <summary>
    /// Test writing out refs to dynamic modules
    /// </summary>
    public class DynamicTests
    {
        [Fact]
        public void TestDynamicAssemblyRef()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestDynamicAssemblyRef"), AssemblyBuilderAccess.Run);
            RoundTrip.Assert(pickler, assembly);
        }

        [Fact]
        public void TestDynamicModuleRef()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestDynamicModuleRef"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            RoundTrip.Assert(pickler, module);
        }

        [Fact]
        public void TestDynamicTypeRef()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestDynamicTypeRef"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test").CreateType();
            RoundTrip.Assert(pickler, type);
        }

        [Fact]
        public void TestDynamicTypeRef_UncreatedType()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestDynamicTypeRef_UncreatedType"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test");
            // We haven't created this type so we can't load it
            var exc = Assert.Throws<Exception>(() =>
            {
                var _ = RoundTrip.Do<Type>(pickler, type);
            });

            Assert.Contains("Type 'System.Reflection.Emit.TypeBuilder' is not automaticly serializable as it inherits from Type.", exc.Message);
        }

        [Fact]
        public void TestAmbiguousAssemblies()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly1 = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestAmbiguousAssemblies"), AssemblyBuilderAccess.Run);
            var module1 = assembly1.DefineDynamicModule("main");
            var type1 = module1.DefineType("test").CreateType();

            var assembly2 = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestAmbiguousAssemblies"), AssemblyBuilderAccess.Run);
            var module2 = assembly2.DefineDynamicModule("main2");
            var type2 = module2.DefineType("test2").CreateType();

            var exc = Assert.Throws<Exception>(() => RoundTrip.Do(pickler, type2));

            Assert.Contains("Ambiguous assembly name 'TestAmbiguousAssemblies, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', found multiple matching assemblies.", exc.Message);
        }

        private static void BuildEmptyMethod(MethodBuilder methodBuilder)
        {
            var body = methodBuilder.GetILGenerator();
            body.Emit(OpCodes.Ret);
        }

        [Fact]
        public void TestRequiredAndOptionalModifiers()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestRequiredAndOptionalModifiers"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test");

            // Build a method with some optional and required modifers and another method without the modifiers and make sure we can emit them correctly and use the correct signatures for them.

            var returnType = typeof(int);
            var returnTypeRequiredCustomModifiers = new Type[] { typeof(System.Runtime.CompilerServices.IsVolatile) };
            var returnTypeOptionalCustomModifiers = new Type[] { typeof(System.Runtime.CompilerServices.IsConst) };

            var parameterTypes = new Type[] { typeof(int) };
            var parameterTypeRequiredCustomModifiers = new Type[][] { new Type[] { typeof(System.Runtime.CompilerServices.IsVolatile) } };
            var parameterTypeOptionalCustomModifiers = new Type[][] { new Type[] { typeof(System.Runtime.CompilerServices.IsConst) } };

            var methodWithModifers =
                type.DefineMethod("Method", MethodAttributes.Public, CallingConventions.HasThis,
                returnType, returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers,
                parameterTypes, parameterTypeRequiredCustomModifiers, parameterTypeOptionalCustomModifiers);
            methodWithModifers.DefineParameter(1, ParameterAttributes.None, "x");
            BuildEmptyMethod(methodWithModifers);

            var methodWithoutModifers =
                type.DefineMethod("Method", MethodAttributes.Public, CallingConventions.HasThis, returnType, parameterTypes);
            methodWithoutModifers.DefineParameter(1, ParameterAttributes.None, "y");
            BuildEmptyMethod(methodWithoutModifers);

            var builtType = type.CreateType();

            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);
            foreach (var method in builtType.GetMethods())
            {
                RoundTrip.Assert(pickler, method);
            }

            pickler = new Pickler();
            var rebuiltType = RoundTrip.Do(pickler, builtType);

            var rebuiltMethods = rebuiltType.GetMethods().Where(mi => mi.Name == "Method").ToArray();
            Assert.Equal(2, rebuiltMethods.Length);

            foreach (var method in rebuiltMethods)
            {
                var parameter = method.GetParameters().Single();
                if (parameter.Name == "x")
                {
                    // Should have modifiers
                    Assert.Equal(new Type[] { typeof(System.Runtime.CompilerServices.IsVolatile) }, parameter.GetRequiredCustomModifiers());
                    Assert.Equal(new Type[] { typeof(System.Runtime.CompilerServices.IsConst) }, parameter.GetOptionalCustomModifiers());

                    // And so should the return type
                    var returnParameter = method.ReturnParameter;
                    Assert.Equal(new Type[] { typeof(System.Runtime.CompilerServices.IsVolatile) }, returnParameter.GetRequiredCustomModifiers());
                    Assert.Equal(new Type[] { typeof(System.Runtime.CompilerServices.IsConst) }, returnParameter.GetOptionalCustomModifiers());
                }
                else
                {
                    // Shouldn't have modifers
                    Assert.Empty(parameter.GetRequiredCustomModifiers());
                    Assert.Empty(parameter.GetOptionalCustomModifiers());
                }
            }
        }
    }
}