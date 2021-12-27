using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    /// <summary>
    /// Tests for AssemblyLoadContext useage
    /// </summary>
    public class AssemblyLoadContextTests
    {
        [Fact]
        public void TestAssemblyLoadContext()
        {
            // Test that our dynamic assembly is loaded into the ALC passed to the pickler and not the default ALC.

            var alc = new System.Runtime.Loader.AssemblyLoadContext("TestAssemblyLoadContext", true);

            var pickler = new Pickler(null, alc);
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynamicAssembly"), AssemblyBuilderAccess.Run);
            var assembly = RoundTrip.Do<Assembly>(pickler, assemblyBuilder);

            Assert.Equal(assembly.FullName, assembly.FullName);

            Assert.Contains(assembly, alc.Assemblies);
            Assert.DoesNotContain(assembly, System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies);
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

            var pickler3 = new Pickler(null, alc);
            var assembly3 = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestSharedALC3"), AssemblyBuilderAccess.Run);
            TestAssert(pickler3, assembly3);
        }
    }
}