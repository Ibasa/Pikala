using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
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

        [Property]
        public Property TestIntStringDictionary()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<System.Collections.Generic.Dictionary<string, int>>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Fact]
        public void TestInterface()
        {
            var pickler = new Pickler();
            var value = Tuple.Create<IEnumerable<int>>(new int[] { 1, 2, 3 });
            RoundTrip.Assert(pickler, value);
        }

        [Fact]
        public void TestNullInvalidType()
        {
            var pickler = new Pickler();
            // These types can't be serialized but that's fine if their null.
            var value = Tuple.Create<Mutex, DynamicMethod>(null, null);
            RoundTrip.Assert(pickler, value);
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
                Assert.Equal("Pointer types are not serializable: 'System.Int32*'", exc.Message);
            }

            // Pointers can't be serialized, but it's fine if they're null.
            RoundTrip.Assert(pickler, new PointerStruct());
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
            new[] { typeof(TestTypes.ClassTypeWithEvents) },
            new[] { typeof(double?) },
            new[] { typeof(System.TypeCode?) },
            new[] { typeof(TestTypes.EnumurationType?) },
            new[] { typeof(Tuple<int, double>) },
            new[] { typeof(ValueTuple<,,>) },
            new[] { typeof(Nullable<>) },
            new[] { typeof(double*) },
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

        [Theory]
        [MemberData(nameof(ExampleTypeSet))]
        public void TestEventRef(Type type)
        {
            var pickler = new Pickler();

            var events = type.GetEvents(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            foreach (var evt in events)
            {
                RoundTrip.Assert(pickler, evt);
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
        public void TestReadmeExample()
        {
            // This test checks that the string in README.md matches what the Pickler currently generates

            // The pikala stream include the current assembly version and runtime versions so this test is only valid
            // for our dotnet6.0 run
            if (Environment.Version.Major != 6)
            {
                return;
            }

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

        [Fact]
        public void TestMemoizationWorksOnReferencesNotEquality()
        {
            var pickler = new Pickler();

            var x = new TestTypes.ReferenceEqualityClass() { Tag = 1 };
            var y = new TestTypes.ReferenceEqualityClass() { Tag = 1 };
            var z = Tuple.Create(x, x, y);

            var w = RoundTrip.Do(pickler, z);

            Assert.Same(w.Item1, w.Item2);
            Assert.NotSame(w.Item2, w.Item3);
        }

        [Fact]
        public void TestStaticReflectionValues()
        {
            // This test checks that if we have static types for reflection objects we can still handle null or values correctly

            var pickler = new Pickler();

            RoundTrip.Assert(pickler, Tuple.Create<Type, System.Reflection.Assembly, System.Reflection.Module, System.Reflection.FieldInfo>(null, null, null, null));

            RoundTrip.Assert(pickler, Tuple.Create(typeof(Stream), typeof(Stream).Module, typeof(Stream).Assembly, typeof(Stream).GetMethods()[0]));
        }

        [Fact]
        public void TestStaticArrayReflectionValues()
        {
            // This test checks that if we have static types for an array of reflection objects we can still handle them correctly.
            var pickler = new Pickler();

            RoundTrip.Assert(pickler, Tuple.Create<Type[]>(null));
            RoundTrip.Assert(pickler, Tuple.Create<Type[]>(new Type[0]));
            RoundTrip.Assert(pickler, Tuple.Create<Type[]>(new Type[] { typeof(Stream) }));

        }

        [Fact(Skip = "DynamicMethods aren't really working yet")]
        public void TestDynamicMethod()
        {
            var pickler = new Pickler();

            // Test we can handle a created dynamic method on this type
            var dynamicMethod = new DynamicMethod("test", typeof(void), null, typeof(PicklerTests));
            var il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ret);
            dynamicMethod.CreateDelegate<Action>();
            RoundTrip.Assert(pickler, Tuple.Create(dynamicMethod));

            // TODO test on module and annonymous context
        }
    }
}