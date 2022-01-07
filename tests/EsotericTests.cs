﻿using System;
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

        [Fact]
        public void TestPropertyOther()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestPropertyOther"), AssemblyBuilderAccess.Run);
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

        private static MethodBuilder DefineBasicMethod(TypeBuilder type, string name, MethodAttributes attributes, Type returnType)
        {
            var method = type.DefineMethod(name, attributes, returnType, null);

            var getgen = method.GetILGenerator();
            getgen.Emit(OpCodes.Newobj, returnType);
            getgen.Emit(OpCodes.Ret);

            return method;
        }

        [Fact]
        public void TestMethodOverloadByReturnType()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestMethodOverloadByReturnType"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test");
            var intMethod = DefineBasicMethod(type, "Method", MethodAttributes.Public, typeof(int));
            var longMethod = DefineBasicMethod(type, "Method", MethodAttributes.Public, typeof(long));
            var typeInstance = type.CreateType();

            var methods = typeInstance.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.Equal(2, methods.Length);

            foreach (var method in methods)
            {
                RoundTrip.Assert(pickler, method);
            }
        }

        private static ConstructorInfo DefineDefaultCtor(TypeBuilder typeBuilder)
        {
            var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var getgen = ctor.GetILGenerator();
            getgen.Emit(OpCodes.Ret);
            return ctor;
        }

        [Fact]
        public void TestModuleAttributes()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleAttributes"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");

            var customAttributeTypeBuilder = module.DefineType("MyAttribute", TypeAttributes.Class, typeof(System.Attribute));
            customAttributeTypeBuilder.DefineField("Tag", typeof(int), FieldAttributes.Public);
            DefineDefaultCtor(customAttributeTypeBuilder);
            var customAttributeType = customAttributeTypeBuilder.CreateType();

            var moduleField = module.DefineInitializedData("field", new byte[] { 1, 2 }, FieldAttributes.Public);
            moduleField.SetCustomAttribute(new CustomAttributeBuilder(
                customAttributeType.GetConstructor(Type.EmptyTypes),
                new object[0],
                new FieldInfo[] { customAttributeType.GetField("Tag") },
                new object[] { 1 }));
            module.CreateGlobalFunctions();

            var rebuiltType = RoundTrip.Do(pickler, customAttributeType);
            var rebuiltModule = rebuiltType.Assembly.ManifestModule;
            var rebuiltField = rebuiltModule.GetField("field");
            Assert.Equal(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA, rebuiltField.Attributes);
            var rebuiltAttr = Assert.Single(rebuiltField.GetCustomAttributes(true));
            Assert.Equal(1, (int)rebuiltType.GetField("Tag").GetValue(rebuiltAttr));
        }
    }
}