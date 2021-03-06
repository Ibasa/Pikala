﻿using System;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections;

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

        private string RunFsi(string script)
        {
            var pikalaAssemblyPath = typeof(Pickler).Assembly.Location;

            var testDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

            var scriptPath = Path.Combine(testDirectory.FullName, "script.fsx");
            File.WriteAllText(scriptPath, script);

            // Copy in Pikala and other assemblies like xunit.assert
            var assemblyReference = CopyAssembly(testDirectory, typeof(Pickler).Assembly);
            CopyAssembly(testDirectory, typeof(Xunit.Assert).Assembly);
            CopyAssembly(testDirectory, typeof(Xunit.TheoryData).Assembly);

            try
            {
                // Copy the pikala assembly to temp, so we can't pick up the Pikala.Test assembly next to it

                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "dotnet";
                psi.ArgumentList.Add("fsi");
                psi.ArgumentList.Add("--exec");
                psi.ArgumentList.Add("--reference:" + assemblyReference);
                psi.ArgumentList.Add(scriptPath);
                // Set current directory to the temp directory so we can't pick up this test assembly
                psi.WorkingDirectory = Path.GetTempPath();
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                var process = new System.Diagnostics.Process();

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                process.OutputDataReceived += ((sender, data) => { if (data.Data != null) { stdout.Append(data.Data); } });
                process.ErrorDataReceived += ((sender, data) => { if (data.Data != null) { stderr.Append(data.Data); } });
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
                    throw new Exception(string.Format(
                        "FSI failed with {0}\nstdout: {1}\nstderr: {2}", process.ExitCode, stdout, stderr));
                }

                return stdout.ToString();
            }
            finally
            {
                Directory.Delete(testDirectory.FullName, true);
            }
        }

        private string Base64FromObject(object obj)
        {
            var pickler = new Pickler();
            // Remoting tests can't refernce this test assembly
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());
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
            Assert.Equal("{Foo = 4;\n Bar = 23.1;}", obj.ToString());
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
            Assert.Equal("{Foo = 3;\n Bar = 2.3;}", result.ToString());
        }

        [Fact]
        public void TestSendFunction()
        {
            Func<int, string> value = i => (i * 2).ToString();

            var script = string.Join('\n', new[]
            {
                ScriptHeader,
                "let func = deserializeBase64 \"" +  Base64FromObject(value) + "\" :?> System.Func<int, string>",
                "printfn \"%s\" (func.Invoke 1)",
                "printfn \"%s\" (func.Invoke 2)",
            });

            var result = RunFsi(script);

            Assert.Equal("24", result);
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
    }
}
