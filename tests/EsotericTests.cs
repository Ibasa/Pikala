using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    /// <summary>
    /// Test writing out Refs and Defs for esoteric modules we can't write in C#/F#.
    /// </summary>
    public class EsotericTests
    {
        private static AssemblyName DynamicAssemblyName = new AssemblyName("DynamicTest");

        [Fact]
        public void TestModuleData()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(DynamicAssemblyName, AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var undata = module.DefineUninitializedData("uninit", 128, FieldAttributes.Public);
            var indata = module.DefineInitializedData("init", new byte[] { 1, 2 }, FieldAttributes.Private);
            module.CreateGlobalFunctions();

            var rebuiltModule = RoundTrip.Do(pickler, module);

            var rebuiltUndata = rebuiltModule.GetField("uninit", BindingFlags.Public | BindingFlags.Static);
            var rebuiltIndata = rebuiltModule.GetField("init", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.Equal("uninit", rebuiltUndata.Name);
            Assert.Equal("init", rebuiltIndata.Name);

            Assert.Equal("$ArrayType$128", rebuiltUndata.FieldType.FullName);
            Assert.Equal("$ArrayType$2", rebuiltIndata.FieldType.FullName);

            var rebuiltDataValue = rebuiltIndata.GetValue(null);

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(rebuiltDataValue, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var addr = handle.AddrOfPinnedObject();
                unsafe
                {
                    var bytes = new ReadOnlySpan<byte>(addr.ToPointer(), 2);
                    Assert.Equal(1, bytes[0]);
                    Assert.Equal(2, bytes[1]);
                }
            }
            finally
            {
                handle.Free();
            }
        }
    }
}