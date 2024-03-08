using System;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    /// <summary>
    /// Theses tests are all by themselves because the lambda generates a type in the test and so we have to serialize the whole test class.
    /// While serialising the whole test class should work for Pikala having this by itself makes investigation into just lambdas much easier.
    /// </summary>
    public class EmitLambdaTests
    {
        private Pickler CreatePickler()
        {
            var assemblyPickleMode = new Func<System.Reflection.Assembly, AssemblyPickleMode>(assembly =>
                assembly == System.Reflection.Assembly.GetExecutingAssembly() ? AssemblyPickleMode.PickleByValue : AssemblyPickleMode.Default
            );

            var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("EmitTest", true);

            return new Pickler(assemblyPickleMode, assemblyLoadContext);
        }

        [Fact]
        public void TestLamda()
        {
            var pickler = CreatePickler();

            Func<int, string> value = i => (i * 2).ToString();

            var result = RoundTrip.Do(pickler, value);
            Assert.Equal(value.Invoke(4), result.Invoke(4));
        }

        [Fact]
        public void TestGenericLambda()
        {
            var pickler = CreatePickler();

            TestTypes.GenericDelegateType<int> value = i => Math.Abs(i) + 1;

            var result = RoundTrip.Do<Delegate>(pickler, value);

            Assert.Equal(value.Invoke(-5), result.DynamicInvoke(-5));
        }

        [Fact]
        public void TestFSharpPrintfFormat()
        {
            var pickler = CreatePickler();

            Func<Tuple<int, double>, string> func = obj =>
            {
                var format = new Microsoft.FSharp.Core.PrintfFormat<Microsoft.FSharp.Core.FSharpFunc<Tuple<int, double>, string>, Microsoft.FSharp.Core.Unit, string, string, Tuple<int, double>>("%+A");
                var value = Microsoft.FSharp.Core.ExtraTopLevelOperators.PrintFormatToString(format);
                return value.Invoke(obj);
            };

            var result = RoundTrip.Do<Delegate>(pickler, func);

            var obj = Tuple.Create(1, 42.634);
            Assert.Equal(func(obj), result.DynamicInvoke(obj));
        }
    }

    /// <summary>
    /// Test IL generation by writing out TypeDefs
    /// </summary>
    public class EmitTests
    {
        private Pickler CreatePickler()
        {
            var assemblyPickleMode = new Func<System.Reflection.Assembly, AssemblyPickleMode>(assembly =>
                assembly == System.Reflection.Assembly.GetExecutingAssembly() ? AssemblyPickleMode.PickleByValue : AssemblyPickleMode.Default
            );

            var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("EmitTest", true);

            return new Pickler(assemblyPickleMode, assemblyLoadContext);
        }

        [Fact]
        public void TestStruct()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.StructureType();
            value.Bar = 2.3;
            value.Foo = 5;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestClass()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.ClassType("hello");
            value.Foo = 5;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestGenericMethods()
        {
            var pickler = CreatePickler();

            Func<double, int, string> func1 = TestTypes.GenericMethods.DoIt<double>;

            var result1 = RoundTrip.Do(pickler, func1);

            Assert.Equal(func1(4.3, 2), result1(4.3, 2));

            Func<string, int, string> func2 = TestTypes.GenericMethods.DoIt<string, int>;

            var result2 = RoundTrip.Do(pickler, func2);

            Assert.Equal(func2("hello", 2), result2("hello", 2));
        }

        [Fact]
        public void TestSimpleNested()
        {
            var pickler = CreatePickler();

            var value = TestTypes.NestingClass.NestedEnum.Xs;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestProperties()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.ClassTypeWithProperties("boo");

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestNestedClass()
        {
            var pickler = CreatePickler();

            var innerValue = new TestTypes.ClassTypeWithNestedClass.InnerClass() { Foo = 42 };
            var innerResult = RoundTrip.Do<object>(pickler, innerValue);
            Assert.Equal(innerValue.ToString(), innerResult.ToString());

            var outerValue = new TestTypes.ClassTypeWithNestedClass(4.5f);
            var outerResult = RoundTrip.Do<object>(pickler, outerValue);
            Assert.Equal(outerValue.ToString(), outerResult.ToString());
        }

        [Fact]
        public void TestCircularClasses()
        {
            var pickler = CreatePickler();

            var aValue = new TestTypes.CircularClassA();
            var bValue = new TestTypes.CircularClassB();

            aValue.B = bValue;
            bValue.A = aValue;

            var aResult = RoundTrip.Do<object>(pickler, aValue);
            Assert.Equal(aValue.ToString(), aResult.ToString());

            var bResult = RoundTrip.Do<object>(pickler, bValue);
            Assert.Equal(bValue.ToString(), bResult.ToString());
        }

        [Fact]
        public void TestGenericDelegate()
        {
            var pickler = CreatePickler();

            TestTypes.GenericDelegateType<int> value = Math.Abs;

            var result = RoundTrip.Do<Delegate>(pickler, value);

            Assert.Equal(value.Invoke(-5), result.DynamicInvoke(-5));
        }

        [Fact]
        public void TestNestedStruct()
        {
            var pickler = CreatePickler();

            var innerValue = new TestTypes.ClassTypeWithNestedStruct.InnerStruct() { Foo = 42 };
            var innerResult = RoundTrip.Do<object>(pickler, innerValue);
            Assert.Equal(innerValue.ToString(), innerResult.ToString());

            var outerValue = new TestTypes.ClassTypeWithNestedStruct(4.5f);
            var outerResult = RoundTrip.Do<object>(pickler, outerValue);
            Assert.Equal(outerValue.ToString(), outerResult.ToString());
        }

        [Fact]
        public void TestStaticCtor()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.StaticCtorClass();
            value.Foo = 123.443;

            var result = RoundTrip.Do<object>(pickler, value);
            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestReferenceToType()
        {
            var pickler = CreatePickler();

            var obj = new TestTypes.ClassType("baaa");
            Func<int, int> func = obj.DoInvoke;

            var value = Tuple.Create(func.Method.Name, func.Method.DeclaringType, func.Target);

            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value.Item1, result.Item1);
            Assert.Equal(value.Item2.FullName, result.Item2.FullName);
            Assert.Equal(value.Item3.ToString(), result.Item3.ToString());
        }

        [Fact]
        public void TestSelfReferenceAttribute()
        {
            var pickler = CreatePickler();

            var value = typeof(TestTypes.SelfReferencingAttribute);
            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value.FullName, result.FullName);

            void Check(System.Reflection.MemberInfo member, int expectedTag)
            {
                var attrs = member.GetCustomAttributes(result, false);
                var attr = Assert.Single(attrs);

                Assert.Equal(result, attr.GetType());

                var propertyValue = (string)result.GetProperty("Property").GetValue(attr);
                var tagValue = (int)result.GetField("Tag").GetValue(attr);

                Assert.Equal(member.Name, propertyValue);
                Assert.Equal(expectedTag, tagValue);
            }

            // Check everything has the attributes with set properties
            Check(result, 0);
            Check(result.GetConstructor(Type.EmptyTypes), 1);
            Check(result.GetProperty("Property"), 2);
            Check(result.GetField("Tag"), 3);
        }

        [Fact]
        public void TestConcreteClass()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.ConcreteClass();
            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestAsyncClass()
        {
            var pickler = CreatePickler();

            Func<System.Threading.Tasks.Task<int>> value = TestTypes.AsyncClasss.GetIntAsync;
            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value().Result, result().Result);
        }

        [Fact]
        public void TestClassWithSwitch()
        {
            var pickler = CreatePickler();

            Func<int, string> value = TestTypes.StaticClass.SwitchMethod;
            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value(0), result(0));
            Assert.Equal(value(1), result(1));
            Assert.Equal(value(2), result(2));
            Assert.Equal(value(3), result(3));
        }

        [Fact]
        public void TestSelfReferenceProperty()
        {
            var pickler = CreatePickler();

            var value = new TestTypes.SelfReferencingProperty("boo");

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestSelfReferenceStatic()
        {
            var pickler = CreatePickler();

            var a = new TestTypes.SelfReferenceStatic() { Tag = 1 };
            var b = new TestTypes.SelfReferenceStatic() { Tag = 2 };
            TestTypes.SelfReferenceStatic.Selves = new[] { a, b };
            TestTypes.SelfReferenceStatic.TagField = typeof(TestTypes.SelfReferenceStatic).GetField("Tag");

            var result = RoundTrip.Do<Array>(pickler, TestTypes.SelfReferenceStatic.Selves);

            Assert.Equal(2, result.Length);
            Assert.Equal("1", result.GetValue(0).ToString());
            Assert.Equal("2", result.GetValue(1).ToString());
            // Check that the array on the type matches via reflection
            var type = result.GetValue(0).GetType();
            Assert.Equal("SelfReferenceStatic", type.Name);
            var field = type.GetField("Selves");
            Assert.NotNull(field);
            var array = field.GetValue(null);
            Assert.Same(result, array);

            // fresh pickler so we get a new dynamic assembly
            pickler = CreatePickler();
            var tagField = RoundTrip.Do(pickler, TestTypes.SelfReferenceStatic.TagField);
            Assert.Equal("Tag", tagField.Name);
        }

        [Fact]
        public void TestEvents()
        {
            var pickler = CreatePickler();

            var obj = new TestTypes.ClassTypeWithEvents();
            var result = RoundTrip.Do<object>(pickler, obj);

            Assert.NotNull(result);
            var resultType = result.GetType();

            // Check we've got two events
            var events = resultType.GetEvents();
            Assert.Equal(2, events.Length);
            Assert.Contains(events, evt => evt.Name == "FieldEvent");
            Assert.Contains(events, evt => evt.Name == "PropertyEvent");

            var addHandler = (Action<EventHandler>)(resultType.GetMethod("AddHandler").CreateDelegate(typeof(Action<EventHandler>), result));
            var removeHandler = (Action<EventHandler>)(resultType.GetMethod("RemoveHandler").CreateDelegate(typeof(Action<EventHandler>), result));
            var invoke = (Action)(resultType.GetMethod("Invoke").CreateDelegate(typeof(Action), result));

            var invokeCount = 0;
            void Handler(object sender, EventArgs e)
            {
                ++invokeCount;
                Assert.Same(result, sender);
            }

            addHandler(Handler);
            invoke();
            // 2 because we invoke the field event and property event
            Assert.Equal(2, invokeCount);
            removeHandler(Handler);
            invoke();
            // handler was removed so invoke shouldn't of invoked our method again
            Assert.Equal(2, invokeCount);
        }

        [Fact]
        public void TestPointersAndRefs()
        {
            var pickler = CreatePickler();

            var obj = new TestTypes.ClassWithPointersAndRefs();
            var result = RoundTrip.Do<object>(pickler, obj);

            Assert.Equal(obj.ToString(), result.ToString());
        }

        [Fact]
        public void TestDefaultParameters()
        {
            var pickler = CreatePickler();

            var type = typeof(TestTypes.ClassWithDefaults);
            var defaultsMethod = type.GetMethod("Defaults");

            var resultType = RoundTrip.Do<Type>(pickler, type);
            var resultDefaultsMethod = resultType.GetMethod("Defaults");

            var compareParameter = new Action<int>((i) =>
            {
                var expected = defaultsMethod.GetParameters()[i];
                var actual = resultDefaultsMethod.GetParameters()[i];
                Assert.Equal(expected.HasDefaultValue, actual.HasDefaultValue);
                Assert.Equal(expected.DefaultValue, actual.DefaultValue);
            });

            compareParameter(0);
            compareParameter(1);
            compareParameter(2);
            compareParameter(3);
        }

        [Fact]
        public void TestLiterals()
        {
            var pickler = CreatePickler();

            var obj = new TestTypes.ClassWithLiterals();
            var result = RoundTrip.Do<object>(pickler, obj);

            Assert.Equal(obj.ToString(), result.ToString());
        }

        [Fact]
        public void TestGenericVariance()
        {
            var pickler = CreatePickler();

            var result = RoundTrip.Do<Type>(pickler, typeof(TestTypes.InterfaceWithVariance<,,,>));

            var genericParams = result.GetGenericArguments();
            Assert.Equal(4, genericParams.Length);

            Assert.Equal(System.Reflection.GenericParameterAttributes.Contravariant, genericParams[0].GenericParameterAttributes);
            Assert.Equal(System.Reflection.GenericParameterAttributes.Covariant, genericParams[1].GenericParameterAttributes);
            Assert.Equal(System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint, genericParams[2].GenericParameterAttributes);
            Assert.Equal(System.Reflection.GenericParameterAttributes.DefaultConstructorConstraint, genericParams[3].GenericParameterAttributes);
        }

        [Fact]
        public void TestInterfaceInheritance()
        {
            var pickler = CreatePickler();

            var obj = new TestTypes.InterfaceInheritance();
            var result = RoundTrip.Do<object>(pickler, obj);

            var disposable = Assert.IsAssignableFrom<IDisposable>(result);

            Assert.Equal("False", disposable.ToString());
            disposable.Dispose();
            Assert.Equal("True", disposable.ToString());
        }

        [Fact]
        public void TestInlineIntegers()
        {
            var pickler = CreatePickler();

            var func = new Func<ulong>(() =>
            {
                byte i = 127;
                short j = 32767;
                int k = 2147483647;
                long l = 9223372036854775807;
                return (ulong)(i + j + k + l);
            });

            var result = RoundTrip.Do(pickler, func);
            Assert.Equal(9223372034707325052ul, result());
        }
    }
}
