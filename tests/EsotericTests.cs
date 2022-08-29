using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;
using Xunit.Abstractions;

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

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleData"), AssemblyBuilderAccess.RunAndCollect);
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

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestPropertyOther"), AssemblyBuilderAccess.RunAndCollect);
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
            var getmethod = type.DefineMethod("get_" + name, MethodAttributes.SpecialName, CallingConventions.HasThis, returnType, null);
            var setmethod = type.DefineMethod("set_" + name, MethodAttributes.SpecialName, CallingConventions.HasThis, typeof(void), new[] { returnType });

            var getgen = getmethod.GetILGenerator();
            getgen.Emit(OpCodes.Ldarg_0);
            getgen.Emit(OpCodes.Ldfld, field);
            getgen.Emit(OpCodes.Ret);

            var setgen = setmethod.GetILGenerator();
            setgen.Emit(OpCodes.Ldarg_0);
            setgen.Emit(OpCodes.Ldarg_1);
            setgen.Emit(OpCodes.Stfld, field);
            setgen.Emit(OpCodes.Ret);

            var property = type.DefineProperty(name, attributes, CallingConventions.HasThis, returnType, null);
            property.SetGetMethod(getmethod);
            property.SetSetMethod(setmethod);
            return property;
        }

        private static PropertyBuilder DefineStaticAutomaticProperty(TypeBuilder type, string name, PropertyAttributes attributes, Type returnType)
        {
            var field = type.DefineField("@backingfield_" + name, returnType, FieldAttributes.Private | FieldAttributes.Static);
            var getmethod = type.DefineMethod("get_" + name, MethodAttributes.SpecialName | MethodAttributes.Static, CallingConventions.Standard, returnType, null);
            var setmethod = type.DefineMethod("set_" + name, MethodAttributes.SpecialName | MethodAttributes.Static, CallingConventions.Standard, typeof(void), new[] { returnType });

            var getgen = getmethod.GetILGenerator();
            getgen.Emit(OpCodes.Ldsfld, field);
            getgen.Emit(OpCodes.Ret);

            var setgen = setmethod.GetILGenerator();
            setgen.Emit(OpCodes.Ldarg_0);
            setgen.Emit(OpCodes.Stsfld, field);
            setgen.Emit(OpCodes.Ret);

            var property = type.DefineProperty(name, attributes, CallingConventions.Standard, returnType, null);
            property.SetGetMethod(getmethod);
            property.SetSetMethod(setmethod);
            return property;
        }

        [Fact]
        public void TestPropertyOverloadByReturnType()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestPropertyOverloadByReturnType"), AssemblyBuilderAccess.RunAndCollect);
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

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestMethodOverloadByReturnType"), AssemblyBuilderAccess.RunAndCollect);
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
        public void TestBasicAssembly()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestBasicAssembly"), AssemblyBuilderAccess.RunAndCollect);

            var rebuiltAssembly = RoundTrip.Do(pickler, assembly);
            Assert.Equal("TestBasicAssembly", rebuiltAssembly.GetName().Name);
        }

        [Fact]
        public void TestAssemblyAttributes()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestAssemblyAttributes"), AssemblyBuilderAccess.RunAndCollect);

            var attributeType = typeof(System.ObsoleteAttribute);

            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                attributeType.GetConstructor(new Type[] { typeof(string) }),
                new object[] { "Old assembly" },
                new FieldInfo[0],
                new object[0]));

            var rebuiltAssemmly = RoundTrip.Do(pickler, assembly);
            var rebuiltAttr = Assert.Single(rebuiltAssemmly.GetCustomAttributes(true));
            Assert.Equal("Old assembly", (string)attributeType.GetProperty("Message").GetValue(rebuiltAttr));
        }

        [Fact]
        public void TestCustomAssemblyAttributes()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestCustomAssemblyAttributes"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("main");

            var customAttributeTypeBuilder = module.DefineType("MyAttribute", TypeAttributes.Class, typeof(System.Attribute));
            customAttributeTypeBuilder.DefineField("Tag", typeof(int), FieldAttributes.Public);
            DefineDefaultCtor(customAttributeTypeBuilder);
            var customAttributeType = customAttributeTypeBuilder.CreateType();

            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                customAttributeType.GetConstructor(Type.EmptyTypes),
                new object[0],
                new FieldInfo[] { customAttributeType.GetField("Tag") },
                new object[] { 1 }));

            var rebuiltType = RoundTrip.Do(pickler, customAttributeType);
            var rebuiltAssembly = rebuiltType.Assembly;
            var rebuiltAttr = Assert.Single(rebuiltAssembly.GetCustomAttributes(true));
            Assert.Equal(1, (int)rebuiltType.GetField("Tag").GetValue(rebuiltAttr));
        }

        [Fact]
        public void TestBasicModule()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestBasicModule"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("main");

            var moduleField = module.DefineInitializedData("field", new byte[] { 1, 2 }, FieldAttributes.Public);
            module.CreateGlobalFunctions();

            var rebuiltModule = RoundTrip.Do(pickler, module);
            var rebuiltField = rebuiltModule.GetField("field");
            Assert.Equal(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA, rebuiltField.Attributes);
        }

        [Fact]
        public void TestModuleAttributes()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleAttributes"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("main");

            var attributeType = typeof(System.ObsoleteAttribute);

            var moduleField = module.DefineInitializedData("field", new byte[] { 1, 2 }, FieldAttributes.Public);
            moduleField.SetCustomAttribute(new CustomAttributeBuilder(
                attributeType.GetConstructor(new Type[] { typeof(string) }),
                new object[] { "Old module" },
                new FieldInfo[0],
                new object[0]));
            module.CreateGlobalFunctions();

            var rebuiltModule = RoundTrip.Do(pickler, module);
            var rebuiltField = rebuiltModule.GetField("field");
            Assert.Equal(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA, rebuiltField.Attributes);
            var rebuiltAttr = Assert.Single(rebuiltField.GetCustomAttributes(true));
            Assert.Equal("Old module", (string)attributeType.GetProperty("Message").GetValue(rebuiltAttr));
        }

        [Fact]
        public void TestCustomModuleAttributes()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestCustomModuleAttributes"), AssemblyBuilderAccess.RunAndCollect);
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

        [Fact]
        public void TestPropertyOverloadByCallingConvention()
        {
            var pickler = new Pickler(_ => AssemblyPickleMode.PickleByReference);

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestPropertyOverloadByCallingConvention"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("main");
            var type = module.DefineType("test");
            var intProp = DefineAutomaticProperty(type, "Prop", PropertyAttributes.None, typeof(int));
            var longProp = DefineStaticAutomaticProperty(type, "Prop", PropertyAttributes.None, typeof(int));
            var typeInstance = type.CreateType();

            var properties = typeInstance.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.Equal(2, properties.Length);

            foreach (var prop in properties)
            {
                RoundTrip.Assert(pickler, prop);
            }
        }

        [Fact]
        public void TestChangeOfAttributeTypeErrorsCorrectly()
        {
            // Test that if we serialise an attribute, then try to read it back in a new 
            // context where it's underlying type has changed we get an error. This is a tricky test to write because
            // we need a TypeDef for the type using the attribute, but a TypeRef for the type that defines the attribute so we can
            // change it (if it was also a TypeDef it's part of the pikala stream and so can't change).

            var pickler = Utils.CreateIsolatedPickler(assembly =>
            {
                if (assembly.FullName.Contains("TestChangeOfAttribute_Attr"))
                {
                    return AssemblyPickleMode.PickleByReference;
                }
                return AssemblyPickleMode.Default;
            });

            // Create an assembly with our attribute type (this needs to be in the ALC)
            var attributeAssemblyV1 = pickler.AssemblyLoadContext.DefineDynamicAssembly(new AssemblyName("TestChangeOfAttribute_Attr"), AssemblyBuilderAccess.RunAndCollect);
            Type customAttributeType;
            {
                var module = attributeAssemblyV1.DefineDynamicModule("main");

                var customAttributeTypeBuilder = module.DefineType("MyAttribute", TypeAttributes.Class, typeof(System.Attribute));
                customAttributeTypeBuilder.DefineField("Tag", typeof(int), FieldAttributes.Public);
                DefineDefaultCtor(customAttributeTypeBuilder);
                customAttributeType = customAttributeTypeBuilder.CreateType();
            }

            // Create another assembly using MyAttribute
            var typeAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestChangeOfAttribute_Type"), AssemblyBuilderAccess.RunAndCollect);
            {
                typeAssembly.SetCustomAttribute(new CustomAttributeBuilder(
                    customAttributeType.GetConstructor(Type.EmptyTypes),
                    new object[0],
                    new FieldInfo[] { customAttributeType.GetField("Tag") },
                    new object[] { 1 }));
            }

            // Check we roundtrip this with the attr assembly done by reference
            var rebuiltAssembly = RoundTrip.Do<Assembly>(pickler, typeAssembly);
            var rebuiltAttr = Assert.Single(rebuiltAssembly.CustomAttributes);
            Assert.Equal(1, (int)rebuiltAttr.NamedArguments[0].TypedValue.Value);

            // Now recreate the ALC and try to deserialise the pikala stream with a different attribute
            pickler = Utils.CreateIsolatedPickler(assembly =>
            {
                if (assembly.FullName.Contains("TestChangeOfAttribute_Attr"))
                {
                    return AssemblyPickleMode.PickleByReference;
                }
                return AssemblyPickleMode.Default;
            });

            // Create an assembly with our attribute type (this needs to be in the ALC)
            var attributeAssemblyV2 = pickler.AssemblyLoadContext.DefineDynamicAssembly(new AssemblyName("TestChangeOfAttribute_Attr"), AssemblyBuilderAccess.RunAndCollect);
            {
                var module = attributeAssemblyV2.DefineDynamicModule("main");

                var customAttributeTypeBuilder = module.DefineType("MyAttribute", TypeAttributes.Class, typeof(System.Attribute));
                // No Tag field!
                DefineDefaultCtor(customAttributeTypeBuilder);
                customAttributeTypeBuilder.CreateType();
            }

            // This should fail
            var exc = Assert.Throws<MissingFieldException>(() => RoundTrip.Do<Assembly>(pickler, typeAssembly));
            Assert.Contains("Could not load field 'Tag' from type 'MyAttribute'", exc.Message);
        }

        [Fact]
        public void TestModuleField()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleField"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("main");

            var moduleField = module.DefineInitializedData("field", new byte[] { 1, 2 }, FieldAttributes.Public);
            module.CreateGlobalFunctions();

            var fieldInfo = module.GetField("field");
            Assert.NotNull(fieldInfo);

            var rebuiltField = RoundTrip.Do(pickler, fieldInfo);
            Assert.Equal("field", rebuiltField.Name);
            Assert.Equal(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA, rebuiltField.Attributes);
        }

        [Fact(Skip="Not yet working")]
        public void TestModuleMethod()
        {
            var pickler = Utils.CreateIsolatedPickler();

            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TestModuleMethod"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("main");

            var moduleMethod = module.DefineGlobalMethod("method", MethodAttributes.Public | MethodAttributes.Static, typeof(int), null);
            var getgen = moduleMethod.GetILGenerator();
            getgen.Emit(OpCodes.Newobj, typeof(int));
            getgen.Emit(OpCodes.Ret);

            module.CreateGlobalFunctions();

            // Get the methodInfo
            var methodInfo = module.GetMethod("method");
            Assert.NotNull(methodInfo);

            var rebuiltMethod = RoundTrip.Do(pickler, methodInfo);
            Assert.Equal("method", rebuiltMethod.Name);
        }
    }
}