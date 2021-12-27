using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class PicklerTests
    {
        [Property]
        public Property TestEnum()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<ConsoleColor>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Theory]
        [InlineData(TestTypes.NestingClass.NestedEnum.Xs)]
        [InlineData(TestTypes.NestingClass.NestedEnum.Ys)]
        public void TestNestedEnum(TestTypes.NestingClass.NestedEnum value)
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, value);
        }

        [Fact]
        public void TestNull()
        {
            var pickler = new Pickler();
            var result = RoundTrip.Do<object>(pickler, null);
            Assert.Null(result);
        }

        [Property]
        public Property TestIntArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<int[]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestByteArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<byte[]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestCharArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<char[]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestKeyValuePair()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<System.Collections.Generic.KeyValuePair<int, string>>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestIntStringDictionary()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<System.Collections.Generic.Dictionary<string, int>>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Serializable]
        unsafe struct PointerStruct
        {
            public int* Ptr;
        }

        [Fact]
        public void TestPointer()
        {
            var pickler = new Pickler();
            var memoryStream = new MemoryStream();
            unsafe
            {
                int i = 3;
                PointerStruct ptr = new PointerStruct() { Ptr = &i };
                var exc = Assert.Throws<Exception>(() =>
                {
                    pickler.Serialize(memoryStream, ptr);
                });
                Assert.Equal("Pointer types are not serializable: 'System.Reflection.Pointer'", exc.Message);
            }
        }

        [Fact]
        public void TestEmptyValueTuple()
        {
            var pickler = new Pickler();
            var obj = ValueTuple.Create();
            RoundTrip.Assert(pickler, obj);
        }

        [Theory]
        [InlineData(2, "test", true)]
        public void TestValueTuple(int i, string s, bool b)
        {
            var pickler = new Pickler();
            var obj = ValueTuple.Create(i, s, b);
            RoundTrip.Assert(pickler, obj);
        }

        [Theory]
        [InlineData(2, "test", true)]
        public void TestTuple(int i, string s, bool b)
        {
            var pickler = new Pickler();
            var obj = Tuple.Create(i, s, b);
            RoundTrip.Assert(pickler, obj);
        }

        [Fact]
        public void TestMutex()
        {
            var pickler = new Pickler();
            var memoryStream = new MemoryStream();
            var obj = new System.Threading.Mutex();
            var exc = Assert.Throws<Exception>(() =>
            {
                pickler.Serialize(memoryStream, obj);
            });
            Assert.Equal("Type 'System.Threading.Mutex' is not automaticly serializable as it inherits from MarshalByRefObject.", exc.Message);
        }

        [Fact]
        public void TestTimeZoneInfo()
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, TimeZoneInfo.Local);
            RoundTrip.Assert(pickler, TimeZoneInfo.Utc);
        }

        public static object[][] ExampleTypeSet = new Type[][]
        {
            new[] { typeof(int) } ,
            new[] { typeof(string[]) },
            new[] { typeof(Tuple<int, object>) },
            new[] { typeof(PointerStruct) },
            new[] { typeof(List<uint>[]) },
            new[] { typeof(Dictionary<,>) },
            new[] { typeof(Pickler) },
            new[] { typeof(TestTypes.ClassTypeWithIndexers) },
        };

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestType(Type type)
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, type);
        }

        [Fact]
        public void TestGenericTypeDefinition()
        {
            var pickler = new Pickler();
            var type = typeof(System.Collections.Generic.Dictionary<,>);
            RoundTrip.Assert(pickler, type);
        }

        static System.Collections.Generic.Dictionary<int, T> GetGenericDictionary<T>() { throw new NotImplementedException(); }

        [Fact]
        public void TestMethodGenericTypeDefinition()
        {
            var pickler = new Pickler();

            var type = GetType();
            var method = type.GetMethod("GetGenericDictionary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var genericType = method.ReturnType;
            RoundTrip.Assert(pickler, genericType);
        }

        [Fact]
        public void TestMethodGenericParameter()
        {
            var pickler = new Pickler();

            var type = GetType();
            var method = type.GetMethod("GetGenericDictionary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var genericParameter = method.GetGenericArguments()[0];
            RoundTrip.Assert(pickler, genericParameter);
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestAssemblyRef(Type exampleType)
        {
            var pickler = new Pickler();

            RoundTrip.Assert(pickler, exampleType.Assembly);
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestModuleRef(Type exampleType)
        {
            var pickler = new Pickler();

            RoundTrip.Assert(pickler, exampleType.Module);
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestMethodRef(Type type)
        {
            var pickler = new Pickler();

            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            foreach (var method in methods)
            {
                RoundTrip.Assert(pickler, method);
            }
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestFieldRef(Type type)
        {
            var pickler = new Pickler();

            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                RoundTrip.Assert(pickler, field);
            }
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestConstructorRef(Type type)
        {
            var pickler = new Pickler();

            var ctors = type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                RoundTrip.Assert(pickler, ctor);
            }
        }

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestPropertyRef(Type type)
        {
            var pickler = new Pickler();

            var ctors = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                RoundTrip.Assert(pickler, ctor);
            }
        }

        [Fact]
        public void TestGenericMethodRef()
        {
            var pickler = new Pickler();

            var methods = typeof(TestTypes.GenericMethods).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

            var doit1 = methods.First(mi => mi.GetGenericArguments().Length == 1);
            var doit2 = methods.First(mi => mi.GetGenericArguments().Length == 2);

            RoundTrip.Assert(pickler, doit1);
            RoundTrip.Assert(pickler, doit2);
            RoundTrip.Assert(pickler, doit1.MakeGenericMethod(typeof(int)));
            RoundTrip.Assert(pickler, doit2.MakeGenericMethod(typeof(float), typeof(string)));
        }

        private static int StaticFunction() { return 4; }

        [Fact]
        public void TestDelegate()
        {
            var pickler = new Pickler();
            var memoryStream = new MemoryStream();
            var function = new Func<int>(StaticFunction);

            var result = RoundTrip.Do(pickler, function);

            Assert.Equal(function(), result());
        }

        [Fact]
        public void TestReferencesAreDeduplicated()
        {
            var pickler = new Pickler();

            var x = Tuple.Create(1, true);
            var y = Tuple.Create(x, x);

            var z = RoundTrip.Do(pickler, y);

            Assert.Same(z.Item1, z.Item2);
        }

        [Fact]
        public void TestSelfReferentialArray()
        {
            var pickler = new Pickler();

            var array = new object[1];
            array[0] = array;

            var result = RoundTrip.Do(pickler, array);

            Assert.Same(result, result[0]);
        }

        [Fact]
        public void TestBoxedValueTypesAreMemoised()
        {
            var pickler = new Pickler();

            var box = (object)4;

            var array = new object[2];
            array[0] = box;
            array[1] = box;
            Assert.Same(array[0], array[1]);
            array = RoundTrip.Do(pickler, array);
            Assert.Same(array[0], array[1]);

            var tuple = new ValueTuple<object, object>(box, box);
            Assert.Same(tuple.Item1, tuple.Item2);
            tuple = RoundTrip.Do(pickler, tuple);
            Assert.Same(tuple.Item1, tuple.Item2);
        }

        [Fact]
        public void TestNotExplcitlySerialisableObject()
        {
            var pickler = new Pickler();

            var value = new TestTypes.PlainObject();
            value.X = 2;
            value.Y = "hello world";
            value.Z = (1, 4);

            RoundTrip.Assert(pickler, value);
        }

        [Fact]
        public void TestSelfReferentialObject()
        {
            var pickler = new Pickler();

            var value = new TestTypes.SelfReferenceObject();
            value.Foo = 124;
            value.Myself = value;

            var result = RoundTrip.Do(pickler, value);

            Assert.Same(value, value.Myself);
        }

        [Fact]
        public void TestSelfReferentialISerialisable()
        {
            var pickler = new Pickler();

            var value = new TestTypes.SelfRefernceISerialisable();
            value.Foo = 124;
            value.Myself = value;

            var exc = Assert.Throws<MemoException>(() => RoundTrip.Do(pickler, value));

            Assert.Equal("Tried to reference object from position 8 in the stream, but that object is not yet created.", exc.Message);
        }

        [Fact]
        public void TestCircularClasses()
        {
            var pickler = new Pickler();
            var aValue = new TestTypes.CircularClassA() { Foo = 4.5 };
            var bValue = new TestTypes.CircularClassB() { Bar = 123m };

            aValue.B = bValue;
            bValue.A = aValue;

            var aResult = RoundTrip.Do<object>(pickler, aValue);
            Assert.Equal(aValue.ToString(), aResult.ToString());

            var bResult = RoundTrip.Do<object>(pickler, bValue);
            Assert.Equal(bValue.ToString(), bResult.ToString());
        }

        [Fact]
        public void ArrayOfValueTypeSmallerThanBoxedType()
        {
            var pickler = new Pickler();

            var stream = new MemoryStream();
            pickler.Serialize(stream, new int[] { 1, 2, 3 });
            var valueArray = stream.ToArray();

            stream = new MemoryStream();
            pickler.Serialize(stream, new object[] { 1, 2, 3 });
            var boxedArray = stream.ToArray();

            Assert.True(valueArray.Length < boxedArray.Length);
        }

        [Fact]
        public void TestLazyValue()
        {
            var pickler = new Pickler();

            var lazyValue = new Lazy<int>(4);
            var lazyResult = RoundTrip.Do(pickler, lazyValue);

            Assert.Equal(lazyValue.IsValueCreated, lazyResult.IsValueCreated);
            Assert.Equal(lazyValue.Value, lazyResult.Value);
        }

        [Fact]
        public void TestLazyFunc()
        {
            var pickler = new Pickler();

            var lazyValue = new Lazy<int>(() => 6);
            var lazyResult = RoundTrip.Do(pickler, lazyValue);

            Assert.Equal(lazyValue.IsValueCreated, lazyResult.IsValueCreated);
            Assert.Equal(lazyValue.Value, lazyResult.Value);
        }

        [Fact]
        public void TestReadmeExample()
        {
            // This test checks that the string in README.md matches what the Pickler currently generates

            var pickler = new Pickler();
            var stream = new MemoryStream();
            pickler.Serialize(stream, (Func<int, int>)Math.Abs);
            var actual = Convert.ToBase64String(stream.ToArray());

            // Look for the README.md
            string FindReadme(DirectoryInfo directoryInfo)
            {
                var path = Path.Combine(directoryInfo.FullName, "README.md");
                if (File.Exists(path))
                {
                    return path;
                }
                return FindReadme(directoryInfo.Parent);
            }

            var readmePath = FindReadme(new DirectoryInfo(Environment.CurrentDirectory));
            var readmeText = File.ReadAllText(readmePath);
            // Look for the example line using Convert.FromBase64String
            var match = System.Text.RegularExpressions.Regex.Match(readmeText, "Convert\\.FromBase64String\\(\\\"(.+)\\\"\\)");

            Assert.True(match.Success, "Could not find Base64 example in README.md");

            var expected = match.Groups[1].Value;

            Assert.True(expected == actual, $"README.md needs updating with string \"{actual}\"");
        }

        [Property]
        public Property TestJaggedIntArray()
        {
            var pickler = new Pickler();

            return Prop.ForAll(
                Arb.From<int[][]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestMultirankIntArray()
        {
            var pickler = new Pickler();

            return Prop.ForAll(
                Arb.From<int[,]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestVariablesizeIntArray()
        {
            var pickler = new Pickler();

            return Prop.ForAll(
                Utils.ArbitraryArray(Arb.Default.Int32().Generator),
                value => RoundTrip.Assert(pickler, value));
        }
    }
}
