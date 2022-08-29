using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class DelegateTests
    {
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
        public void TestDelegatesAreMemoised()
        {
            var pickler = new Pickler();
            var memoryStream = new MemoryStream();
            var function = new Func<int>(StaticFunction);
            var anotherFunction = new Func<int>(() => 1);

            Assert.NotSame(function, anotherFunction);
            var combinedFunction = Delegate.Combine(function, anotherFunction, anotherFunction);

            // The invocation list should have the same delegate for item 1 and 2
            var invocationList = combinedFunction.GetInvocationList();
            Assert.NotSame(invocationList[0], invocationList[1]);
            Assert.Same(invocationList[1], invocationList[2]);

            var result = RoundTrip.Do(pickler, combinedFunction);

            // Check the invocationList has the same reference constraints
            var resultInvocationList = result.GetInvocationList();
            Assert.NotSame(resultInvocationList[0], resultInvocationList[1]);
            Assert.Same(resultInvocationList[1], resultInvocationList[2]);
        }

        [Fact]
        public void TestRecursiveDelegates()
        {
            var recursive = new TestTypes.RecursiveDelegate();
            recursive.SelfDelegate = recursive.SomeMethod;

            var pickler = new Pickler();
            // The target of the delegate itself needs the delegate to construct
            var result = RoundTrip.Do(pickler, recursive.SelfDelegate);

            Assert.Equal(recursive.SelfDelegate(1), result(1));
        }
    }
}