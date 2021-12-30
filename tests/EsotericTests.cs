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

        [Fact]
        public void TestPropertyOther()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(DynamicAssemblyName, AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("type");
            var property = type.DefineProperty("prop", PropertyAttributes.None, typeof(int), null);
            var other = type.DefineMethod("other", MethodAttributes.Private, typeof(void), null);
            other.GetILGenerator().Emit(OpCodes.Ret);
            property.AddOtherMethod(other);
            var getter = type.DefineMethod("get", MethodAttributes.Private, typeof(int), null);
            getter.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
            getter.GetILGenerator().Emit(OpCodes.Ret);
            property.SetGetMethod(getter);

            var builtType = type.CreateType();

            var rtType = RoundTrip.Do(pickler, builtType);
            Assert.NotNull(rtType);

            var prop = rtType.GetProperty("prop", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(prop);

            var accessors = prop.GetAccessors(true);
            Assert.Equal(2, accessors.Length);

            var getMethod = accessors[0];
            Assert.Equal("get", getMethod.Name);

            var otherMethod = accessors[1];
            Assert.Equal("other", otherMethod.Name);

        }
    }
}