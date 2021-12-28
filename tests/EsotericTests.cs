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
        [Fact]
        public void TestModuleData()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleData"), AssemblyBuilderAccess.Run);
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

        private static PropertyBuilder DefineAutomaticProperty(TypeBuilder type, string name, PropertyAttributes attributes, Type returnType)
        {
            var field = type.DefineField("@backingfield_" + name, returnType, FieldAttributes.Private);
            var getmethod = type.DefineMethod("get_" + name, MethodAttributes.SpecialName, returnType, null);
            var setmethod = type.DefineMethod("set_" + name, MethodAttributes.SpecialName, typeof(void), new[] { returnType });

            var getgen = getmethod.GetILGenerator();
            getgen.Emit(OpCodes.Ldarg_0);
            getgen.Emit(OpCodes.Ldfld, field);
            getgen.Emit(OpCodes.Ret);

            var setgen = setmethod.GetILGenerator();
            setgen.Emit(OpCodes.Ldarg_0);
            setgen.Emit(OpCodes.Ldarg_1);
            setgen.Emit(OpCodes.Stfld, field);
            setgen.Emit(OpCodes.Ret);

            var property = type.DefineProperty(name, attributes, returnType, null);
            property.SetGetMethod(getmethod);
            property.SetSetMethod(setmethod);
            return property;
        }

        [Fact]
        public void TestPropertyOverloadByReturnType()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestPropertyOverloadByReturnType"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test");
            var intProp = DefineAutomaticProperty(type, "Prop", PropertyAttributes.None, typeof(int));
            var longProp = DefineAutomaticProperty(type, "Prop", PropertyAttributes.None, typeof(long));
            var typeInstance = type.CreateType();

            var properties = typeInstance.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.Equal(2, properties.Length);

            foreach (var prop in properties)
            {
                RoundTrip.Assert(pickler, prop);
            }
        }
    }
}