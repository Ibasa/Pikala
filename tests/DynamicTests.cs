using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;
using System.Linq;

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

            Assert.Contains("Could not load type 'test' from module '<In Memory Module>'", exc.Message);
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

        [Fact]
        public void TestSharedALC()
        {
            // This test is to test that if we create one ALC and use it with two picklers that there are no issues with the workaround assembly we use.
            // We're checking for racing and duplication problems here.

            var alc = new System.Runtime.Loader.AssemblyLoadContext("TestSharedALC", true);

            var pickler1 = new Pickler(null, alc);
            var pickler2 = new Pickler(null, alc);

            var assembly1 = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestSharedALC1"), AssemblyBuilderAccess.Run);
            var assembly2 = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestSharedALC2"), AssemblyBuilderAccess.Run);

            void TestAssert(Pickler pickler, AssemblyBuilder assemblyBuilder)
            {
                var assembly = RoundTrip.Do<Assembly>(pickler, assemblyBuilder);
                Assert.Equal(assemblyBuilder.FullName, assembly.FullName);
            }

            var task1 = System.Threading.Tasks.Task.Run(() => TestAssert(pickler1, assembly1));
            var task2 = System.Threading.Tasks.Task.Run((() => TestAssert(pickler2, assembly2)));

            System.Threading.Tasks.Task.WaitAll(task1, task2);
        }
    }
}