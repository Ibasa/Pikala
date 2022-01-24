using System;
using System.IO;
using System.Text;
using Xunit;
using System.Linq;

namespace Ibasa.Pikala.Tests
{
    public class RemotingTests
    {
        private string CopyAssembly(DirectoryInfo testDirectory, System.Reflection.Assembly assembly)
        {
            var assemblyPath = Path.Combine(testDirectory.FullName, Path.GetFileName(assembly.Location));
            File.Copy(assembly.Location, assemblyPath);
            return assemblyPath;
        }

        private static Lazy<Version[]> DotnetSdks = new Lazy<Version[]>(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "dotnet";
            psi.ArgumentList.Add("--list-sdks");
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;

            var process = new System.Diagnostics.Process();

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += ((sender, data) => { if (data.Data != null) { stdout.AppendLine(data.Data); } });
            process.ErrorDataReceived += ((sender, data) => { if (data.Data != null) { stderr.AppendLine(data.Data); } });
            process.StartInfo = psi;

            if (!process.Start())
            {
                throw new Exception("FSI process did not start");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"FSI failed with {process.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
            }

            return
                stdout.ToString().Split(Environment.NewLine)
                .Where(line => !String.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    var parts = line.Split(" ");
                    return new Version(parts[0]);
                })
                .ToArray();
        });

        private string RunFsi(string script)
        {
            var pikalaAssemblyPath = typeof(Pickler).Assembly.Location;

            var testDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

            var scriptPath = Path.Combine(testDirectory.FullName, "script.fsx");
            File.WriteAllText(scriptPath, script);

            // Copy the pikala assembly to temp, so we can't pick up the Pikala.Test assembly next to it
            // Copy in Pikala and other assemblies like xunit.assert
            var assemblyReference = CopyAssembly(testDirectory, typeof(Pickler).Assembly);
            CopyAssembly(testDirectory, typeof(Xunit.Assert).Assembly);
            CopyAssembly(testDirectory, typeof(Xunit.TheoryData).Assembly);

            // Find an SDK version to use that matches our runtime version (e.g. we might be running as 3.1.22 but want to find 3.1.416)
            var runtimeVersion = Environment.Version;
            var sdkVersion =
                DotnetSdks.Value
                .Where(sdkVersion => sdkVersion.Major == runtimeVersion.Major && sdkVersion.Minor == runtimeVersion.Minor)
                .OrderBy(sdkVersion => sdkVersion.Build)
                .Last();

            var globalJsonPath = Path.Combine(testDirectory.FullName, "global.json");
            File.WriteAllText(globalJsonPath, $"{{\"sdk\":{{\"version\": \"{sdkVersion}\"}}}}");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "dotnet";
                psi.ArgumentList.Add("fsi");
                psi.ArgumentList.Add("--exec");
                psi.ArgumentList.Add("--reference:" + assemblyReference);
                psi.ArgumentList.Add(scriptPath);
                // Set current directory to the temp directory so we can't pick up this test assembly
                psi.WorkingDirectory = testDirectory.FullName;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                using var process = new System.Diagnostics.Process();

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                process.OutputDataReceived += ((sender, data) => { if (data.Data != null) { stdout.AppendLine(data.Data); } });
                process.ErrorDataReceived += ((sender, data) => { if (data.Data != null) { stderr.AppendLine(data.Data); } });
                process.StartInfo = psi;

                if (!process.Start())
                {
                    throw new Exception("FSI process did not start");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new Exception($"FSI failed with {process.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                // Trim whitespace and normalise newlines
                return stdout.ToString().Trim().Replace("\r\n", "\n");
            }
            finally
            {
                Directory.Delete(testDirectory.FullName, true);
            }
        }

        private string Base64FromObject(object obj)
        {
            // Remoting tests can't refernce this test assembly
            var pickler = new Pickler(assembly => assembly == System.Reflection.Assembly.GetExecutingAssembly() ? AssemblyPickleMode.PickleByValue : AssemblyPickleMode.Default);
            var memoryStream = new MemoryStream();
            pickler.Serialize(memoryStream, obj);
            return Convert.ToBase64String(memoryStream.ToArray());
        }

        private object Base64ToObject(string base64)
        {
            var pickler = new Pickler();
            var memoryStream = new MemoryStream(Convert.FromBase64String(base64));
            return pickler.Deserialize(memoryStream);
        }

        private static readonly string ScriptHeader = string.Join('\n', new[]
            {
                "open Ibasa.Pikala",
                "",
                "let pickler = Pickler()",
                "",
                "let deserializeBase64 (base64 : string) : obj =",
                "    use stream = new System.IO.MemoryStream(System.Convert.FromBase64String(base64))",
                "    pickler.Deserialize(stream)",
                "",
                "let serializeBase64 (object : obj) : string =",
                "    use stream = new System.IO.MemoryStream()",
                "    pickler.Serialize(stream, object)",
                "    System.Convert.ToBase64String(stream.ToArray())",
                "",
            });

        private static readonly string ScriptHeader_PickleByReference = string.Join('\n', new[]
            {
                "open Ibasa.Pikala",
                "",
                "let pickler = Pickler(fun _ -> AssemblyPickleMode.PickleByReference)",
                "",
                "let deserializeBase64 (base64 : string) : obj =",
                "    use stream = new System.IO.MemoryStream(System.Convert.FromBase64String(base64))",
                "    pickler.Deserialize(stream)",
                "",
                "let serializeBase64 (object : obj) : string =",
                "    use stream = new System.IO.MemoryStream()",
                "    pickler.Serialize(stream, object)",
                "    System.Convert.ToBase64String(stream.ToArray())",
                "",
            });

        private string FsiToStringObject(object obj)
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let object = deserializeBase64 \"" +  Base64FromObject(obj) + "\"",
                "printf \"%O\" object",
            });

            return RunFsi(script);
        }

        private void AssertFsiToStringObject(object obj)
        {
            var result = FsiToStringObject(obj);
            Assert.Equal(obj.ToString(), result);
        }

        [Fact]
        public void TestFSharpUnit()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let base64 = serializeBase64 typeof<unit>",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Type;
            Assert.Same(typeof(Microsoft.FSharp.Core.Unit), result);
        }

        [Fact]
        public void TestInt()
        {
            AssertFsiToStringObject(2);
        }

        [Fact]
        public void TestDelegate()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let str = deserializeBase64 \"" +  Base64FromObject("hello world") + "\" :?> string",
                "let func = deserializeBase64 \"" +  Base64FromObject((Func<string, bool>)string.IsNullOrEmpty) + "\" :?> System.Func<string, bool>",
                "let result = func.Invoke(str)",
                "printf \"%b\" result",
            });

            var result = RunFsi(script);

            Assert.Equal("false", result);
        }

        [Theory]
        [InlineData(TestTypes.EnumurationType.Foo)]
        [InlineData(TestTypes.EnumurationType.Bar)]
        public void TestEnumDef(TestTypes.EnumurationType value)
        {
            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestDelegateDef()
        {
            var value = (TestTypes.DelegateType)Math.Max;

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = deserializeBase64 \"" +  Base64FromObject(value) + "\" :?> System.Delegate",
                "let result = func.DynamicInvoke(1, 2)",
                "printf \"%O\" result",
            });

            var result = RunFsi(script);

            Assert.Equal("2", result);
        }

        [Fact]
        public void TestGenericDelegateDef()
        {
            var value = (TestTypes.GenericDelegateType<int>)Math.Abs;

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = deserializeBase64 \"" +  Base64FromObject(value) + "\" :?> System.Delegate",
                "let result = func.DynamicInvoke(-4)",
                "printf \"%O\" result",
            });

            var result = RunFsi(script);

            Assert.Equal("4", result);
        }

        [Fact]
        public void TestStructureType()
        {
            var value = new TestTypes.StructureType();
            value.Foo = 2;
            value.Bar = Math.PI;

            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestStructureTypeWithInterface()
        {
            var value = new TestTypes.StructureTypeWithInterface();
            value.Foo = 2;
            value.Bar = Math.PI;

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let object = deserializeBase64 \"" +  Base64FromObject(value) + "\" :?> System.IDisposable",
                "object.Dispose()"
            });

            var result = RunFsi(script);

            Assert.Equal("Called dispose", result);
        }

        [Fact]
        public void TestStructureTypeWithGeneric()
        {
            var value1 = new TestTypes.StructureTypeWithGeneric<int>();
            value1.Foo = 2;

            var value2 = new TestTypes.StructureTypeWithGeneric<string>();
            value2.Foo = "hello";

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let object1 = deserializeBase64 \"" +  Base64FromObject(value1) + "\"",
                "let object2 = deserializeBase64 \"" +  Base64FromObject(value2) + "\"",
                "let result = object1.Equals(object2)",
                "printf \"%b\" result",
            });

            var result = RunFsi(script);

            Assert.Equal("false", result);
        }

        [Fact]
        public void TestClassType()
        {
            var value = new TestTypes.ClassType("hello world");
            value.Foo = 2;

            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestClassTypeWithProperties()
        {
            var value = new TestTypes.ClassTypeWithProperties("bar");
            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestClassTypeWithExplcitInterface()
        {
            var value = new TestTypes.ClassTypeWithExplcitInterface(2);

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let object = deserializeBase64 \"" +  Base64FromObject(value) + "\" :?> System.IDisposable",
                "object.Dispose()",
            });

            var result = RunFsi(script);

            Assert.Equal("Called IDisposable.Dispose() on 2", result);
        }

        [Fact]
        public void TestNestedEnum()
        {
            var value = TestTypes.StaticNestingType.InnerEnum.InnerX;
            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestClassTypeWithNestedStruct()
        {
            var value = new TestTypes.ClassTypeWithNestedStruct(3.123f);
            AssertFsiToStringObject(value);
        }

        [Fact]
        public void TestReturnType()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let base64 = serializeBase64 typeof<float>",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Type;

            Assert.Equal("System.Double", result.FullName);
        }

        [Fact]
        public void TestReturnCustomType()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Frober = { Foo : int; Bar : double }",
                "let base64 = serializeBase64 typeof<Frober>",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Type;

            Assert.Equal("FSI_0001+Frober", result.FullName);

            var obj = System.Activator.CreateInstance(result, 4, 23.1);
            Assert.Equal("{ Foo = 4\n  Bar = 23.1 }", obj.ToString());
        }

        [Fact]
        public void TestReturnCustomObject()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Frober = { Foo : int; Bar : double }",
                "let frob = { Foo = 3; Bar = 2.3}",
                "let base64 = serializeBase64 frob",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            Assert.Equal("FSI_0001+Frober", result.GetType().FullName);
            Assert.Equal("{ Foo = 3\n  Bar = 2.3 }", result.ToString());
        }

        [Fact]
        public void TestSendFunction()
        {
            Func<int, string> value = i => (i * 2).ToString();

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = deserializeBase64 \"" +   Base64FromObject(value) + "\" :?> System.Func<int, string>",
                "printfn \"%s\" (func.Invoke 1)",
                "printfn \"%s\" (func.Invoke 2)",
            });

            var result = RunFsi(script);

            Assert.Equal("2\n4", result);
        }

        [Fact]
        public void TestReturnFunction()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = System.Func<int, string>(fun i -> sprintf \"%d\" (i * 2))",
                "let base64 = serializeBase64 func",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Func<int, string>;

            Assert.Equal("2", result(1));
            Assert.Equal("4", result(2));
        }

        [Fact]
        public void TestReturnFSharpMethod()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = fun list -> \"hello\" :: list",
                "let base64 = serializeBase64 func",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Microsoft.FSharp.Core.FSharpFunc<
                Microsoft.FSharp.Collections.FSharpList<string>, Microsoft.FSharp.Collections.FSharpList<string>>;

            var nil = Microsoft.FSharp.Collections.FSharpList<string>.Empty;
            var expected = Microsoft.FSharp.Collections.FSharpList<string>.Cons("hello", nil);
            Assert.Equal(expected, result.Invoke(nil));
        }

        [Fact]
        public void TestReturnFSharpFunc()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = fun i -> i * 2",
                "let base64 = serializeBase64 func",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Microsoft.FSharp.Core.FSharpFunc<int, int>;

            Assert.Equal(2, result.Invoke(1));
            Assert.Equal(4, result.Invoke(2));
        }

        [Fact]
        public void TestReturnDU()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "type MyDu = | Foo of int | Bar of string",
                "let value = Foo 2",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            var type = result.GetType();
            Assert.True(Microsoft.FSharp.Reflection.FSharpType.IsUnion(type, null));
        }

        [Fact]
        public void TestReturnRef()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let value = ref 123",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Microsoft.FSharp.Core.FSharpRef<int>;

            Assert.Equal(123, result.Value);
        }

        [Fact]
        public void TestReturnObjectExpression()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let value = { new System.IEquatable<int> with member __.Equals i = i = 123 }",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as IEquatable<int>;

            Assert.True(result.Equals(123));
            Assert.False(result.Equals(124));
        }

        [Fact]
        public void TestReturnReferenceToStatic()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let intValue = ref 1",
                "let value = fun i -> i + !intValue",
                "intValue := 123",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script)) as Microsoft.FSharp.Core.FSharpFunc<int, int>;

            Assert.Equal(124, result.Invoke(1));
        }

        [Fact]
        public void TestReturnComplexObject()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let prov() =",
                "    printfn \"Testing testing 123...\"",
                "    42",
                "type Comp<'T> = | Prov of (unit -> 'T) | Value of 'T",
                "let value = Prov prov",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            var type = result.GetType();
            Assert.True(Microsoft.FSharp.Reflection.FSharpType.IsUnion(type, null));
        }

        [Fact]
        public void TestReturnEnumWithCustomAttributes()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "[<System.FlagsAttribute>]",
                "type SomeFlags = Read = 1 | Write = 2",
                "let value = SomeFlags.Read",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            var type = result.GetType();

            Assert.Equal("SomeFlags", type.Name);
            var flagsAttribute =
                Assert.IsType<FlagsAttribute>(
                    Assert.Single(type.GetCustomAttributes(typeof(FlagsAttribute), false)));
            Assert.NotNull(flagsAttribute);

            Assert.Equal("Read", result.ToString());
        }

        [Fact]
        public void TestReturnAssembly()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "[<assembly: System.Reflection.AssemblyCompanyAttribute \"Ibasa\">]",
                "do ()",
                "let value = System.Reflection.Assembly.GetExecutingAssembly()",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            var assembly = Assert.IsAssignableFrom<System.Reflection.Assembly>(result);

            var name = assembly.GetName();
            Assert.Equal("FSI-ASSEMBLY", name.Name);
            var companysAttribute =
                Assert.IsType<System.Reflection.AssemblyCompanyAttribute>(
                    Assert.Single(assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyCompanyAttribute), false)));
            Assert.Equal("Ibasa", companysAttribute.Company);
        }

        [Fact]
        public void TestRoundtripChangingFieldTypeIncompatibly()
        {
            // Test that if we serialise an object with a custom type, then reload that object in a new domain where we've changed
            // the type of the fields thar we get a sensible error


            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "type Frober = { Foo : int; Bar : double }",
                "let frob = { Foo = 3; Bar = 2.3}",
                "let base64 = serializeBase64 frob",
                "printf \"%s\" base64",
            });

            var pickledbase64 = RunFsi(scriptA);

            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Frober = { Foo : int64; Bar : single }",
                "let obj = deserializeBase64 \"" + pickledbase64 + "\" :?> Frober",
                "printf \"%O\" obj",
            });

            var exception = Assert.Throws<Exception>(() => RunFsi(scriptB));

            Assert.Contains("Object of type 'System.Double' cannot be converted to type 'System.Single'", exception.Message);
        }

        [Fact]
        public void TestChangeTypeVarNameDoesNotAffectSignatureLookup()
        {
            // Test that if we change the name of a type var in a method we can still look that method reference up.

            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "module Test =",
                "   let doWork<'a> (args : 'a list) = args.Length",
                "let typ = System.Type.GetType(\"FSI_0001+Test\")",
                "let mi = typ.GetMethod(\"doWork\")",
                "let base64 = serializeBase64 mi",
                "printf \"%s\" base64",
            });


            var pickledbase64 = RunFsi(scriptA);

            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "module Test =",
                "   let doWork<'T> (args : 'T list) = args.Length",
                "let mi = deserializeBase64 \"" + pickledbase64 + "\" :?> System.Reflection.MethodInfo",
                "printf \"%s\" mi.Name",
            });

            var result = RunFsi(scriptB);

            Assert.Equal("doWork", result);
        }

        [Fact]
        public void TestCustomAttributesOnAssembly()
        {
            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "type MyAttribute() =",
                "   inherit System.Attribute()",
                "   member val public Property = \"hello\" with get, set",
                "   override this.ToString() = this.Property",
                "[<assembly: MyAttribute(Property = \"testing\")>]",
                "do ()",
                "let value = MyAttribute(Property = \"return\")",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var result = Base64ToObject(RunFsi(script));

            Assert.Equal("return", result.ToString());
            var customAttributeType = result.GetType();
            Assert.Equal("MyAttribute", customAttributeType.Name);

            var assembly = customAttributeType.Assembly;
            var name = assembly.GetName();
            Assert.Equal("FSI-ASSEMBLY", name.Name);

            var customAttribute = Assert.Single(assembly.GetCustomAttributes(customAttributeType, true));
            Assert.Equal("testing", customAttribute.ToString());
        }

        [Fact]
        public void TestChangeToEnumTypeErrorsCorrectly()
        {
            // Test that if we serialise an object type, then try to read it back in a new 
            // context where it's an enum we get an error.

            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "type Test = { Value : int }",
                "let value = [| { Value = 1 }; { Value = 2 } |]",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var pickledbase64 = RunFsi(scriptA);

            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Test =",
                "   | A = 1",
                "   | B = 2",
                "let value = deserializeBase64 \"" + pickledbase64 + "\" :?> Test array",
                "printf \"%O\" value",
            });

            var exception = Assert.Throws<Exception>(() => RunFsi(scriptB));

            // This is maybe not the best error, but it at least kinda makes sense
            Assert.Contains("Can not deserialize type 'FSI_0001+Test', could not find expected field 'Value@'", exception.Message);
        }

        [Fact]
        public void TestChangeFromEnumTypeErrorsCorrectly()
        {
            // Test that if we serialise an enum type, then try to read it back in a new 
            // context where it's not an enum we get an error.

            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "type Test =",
                "   | A = 1",
                "   | B = 2",
                "let value = [| Test.A; Test.B |]",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var pickledbase64 = RunFsi(scriptA);

            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Test = { Value : int }",
                "let value = deserializeBase64 \"" + pickledbase64 + "\" :?> Test array",
                "printf \"%O\" value",
            });

            var exception = Assert.Throws<Exception>(() => RunFsi(scriptB));

            Assert.Contains("Can not deserialise FSI_0001+Test expected it to be an enumeration type", exception.Message);
        }

        [Fact]
        public void TestChangeFromClassTypeMemosCorrectly()
        {
            // Test that if we serialise an object type it memoizes, if we then
            // change to a value type the memo lookups are still valid

            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "type Test = { Value : int }",
                "let value = { Value = 1 }",
                "let tuple = struct (value, value)",
                "let base64 = serializeBase64 tuple",
                "printf \"%s\" base64",
            });

            var pickledbase64 = RunFsi(scriptA);

            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Test = { Value : int }",
                "let struct(a, b) as t = deserializeBase64 \"" + pickledbase64 + "\" :?> System.ValueTuple<Test, Test>",
                "if obj.ReferenceEquals(a, b) then",
                "    printf \"%O\" t",
                "else",
                "    printf \"false\"",
            });

            var result = RunFsi(scriptB);

            Assert.Equal("({ Value = 1 }, { Value = 1 })", result);

            var scriptC = string.Join('\n', new[]
            {
                ScriptHeader,
                "[<Struct>]",
                "type Test = { Value : int }",
                "let t = deserializeBase64 \"" + pickledbase64 + "\"",
                "printf \"%O\" t",
            });

            result = RunFsi(scriptC);

            Assert.Equal("({ Value = 1 }, { Value = 1 })", result);
        }

        [Fact]
        public void TestChangeFromEnumTypeCodeErrorsCorrectly()
        {
            // Test that if we serialise an enum type, then try to read it back in a new 
            // context where it's underlying type code has changed we get an error.

            var scriptA = string.Join('\n', new[]
            {
                ScriptHeader_PickleByReference,
                "type Test =",
                "   | A = 128us",
                "   | B = 256us",
                "   | C = 10000us",
                "let value = [| Test.A; Test.B; Test.C |]",
                "let base64 = serializeBase64 value",
                "printf \"%s\" base64",
            });

            var pickledbase64 = RunFsi(scriptA);

            // Try a larger typecode
            var scriptB = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Test =",
                "   | A = 128u",
                "   | B = 256u",
                "   | C = 10000u",
                "let value = deserializeBase64 \"" + pickledbase64 + "\" :?> Test array",
                "printf \"%O\" value",
            });

            var exception = Assert.Throws<Exception>(() => RunFsi(scriptB));
            Assert.Contains("Can not deserialise FSI_0001+Test expected it to be an enumeration of UInt16 but was UInt32", exception.Message);

            // Try a smaller typecode
            var scriptC = string.Join('\n', new[]
            {
                ScriptHeader,
                "type Test =",
                "   | A = 128uy",
                "   | B = 0uy",
                "   | C = 16uy",
                "let value = deserializeBase64 \"" + pickledbase64 + "\" :?> Test array",
                "printf \"%O\" value",
            });

            exception = Assert.Throws<Exception>(() => RunFsi(scriptC));
            Assert.Contains("Can not deserialise FSI_0001+Test expected it to be an enumeration of UInt16 but was Byte", exception.Message);
        }
    }
}
