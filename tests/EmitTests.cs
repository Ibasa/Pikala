using System;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    /// <summary>
    /// This test is all by itself because the lamda generates a type in the test and so we have to serialize the whole test class. 
    /// While serialising the whole test class should work for Pikala having this by itself makes investigation into just lamdas much easier.
    /// </summary>
    public class EmitLambdaTest
    {
        [Fact]
        public void TestLamda()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            Func<int, string> value = i => (i * 2).ToString();

            var result = RoundTrip.Do(pickler, value);
            Assert.Equal(value.Invoke(4), result.Invoke(4));
        }

        [Fact]
        public void TestGenericLambda()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            TestTypes.GenericDelegateType<int> value = i => Math.Abs(i) + 1;

            var result = RoundTrip.Do<Delegate>(pickler, value);

            Assert.Equal(value.Invoke(-5), result.DynamicInvoke(-5));
        }

        [Fact]
        public void TestFSharpPrintfFormat()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());
            var obj = Tuple.Create(1, 42.634);

            Func<Tuple<int, double>, string> func = obj =>
            {
                var format = new Microsoft.FSharp.Core.PrintfFormat<Microsoft.FSharp.Core.FSharpFunc<Tuple<int, double>, string>, Microsoft.FSharp.Core.Unit, string, string, Tuple<int, double>>("%+A");
                var value = Microsoft.FSharp.Core.ExtraTopLevelOperators.PrintFormatToString(format);
                return value.Invoke(obj);
            };

            var result = RoundTrip.Do<Delegate>(pickler, func);

            Assert.Equal(func(obj), result.DynamicInvoke(obj));
        }
    }

    /// <summary>
    /// Test IL generation by writing out TypeDefs
    /// </summary>
    public class EmitTests
    {
        [Fact]
        public void TestStruct()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = new TestTypes.StructureType();
            value.Bar = 2.3;
            value.Foo = 5;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestClass()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = new TestTypes.ClassType("hello");
            value.Foo = 5;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestGenericMethods()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

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
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = TestTypes.NestingClass.NestedEnum.Xs;

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestProperties()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = new TestTypes.ClassTypeWithProperties("boo");

            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestNestedClass()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

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
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

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
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            TestTypes.GenericDelegateType<int> value = Math.Abs;

            var result = RoundTrip.Do<Delegate>(pickler, value);

            Assert.Equal(value.Invoke(-5), result.DynamicInvoke(-5));
        }

        [Fact]
        public void TestNestedStruct()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

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
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = new TestTypes.StaticCtorClass();
            value.Foo = 123.443;

            var result = RoundTrip.Do<object>(pickler, value);
            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestReferenceToType()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

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
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = typeof(TestTypes.SelfReferencingAttribute);
            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value.FullName, result.FullName);
        }

        [Fact]
        public void TestConcreteClass()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            var value = new TestTypes.ConcreteClass();
            var result = RoundTrip.Do<object>(pickler, value);

            Assert.Equal(value.ToString(), result.ToString());
        }

        [Fact]
        public void TestAsyncClass()
        {
            var pickler = new Pickler();
            pickler.UnreferanceableAssemblies.Add(System.Reflection.Assembly.GetExecutingAssembly());

            Func<System.Threading.Tasks.Task<int>> value = TestTypes.AsyncClasss.GetIntAsync;
            var result = RoundTrip.Do(pickler, value);

            Assert.Equal(value().Result, result().Result);
        }
    }
}
