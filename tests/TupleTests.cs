using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class TupleTests
    {
        private class MyTuple : Tuple<int, float>
        {
            public MyTuple(int i, float f, bool b) : base(i, f)
            {
                SomeVar = b;
            }

            public readonly bool SomeVar;

            public override bool Equals([NotNullWhen(true)] object obj)
            {
                if (obj is MyTuple other)
                {
                    return Item1 == other.Item1 && Item2 == other.Item2 && SomeVar == other.SomeVar;
                }
                return false;
            }
        }


        [Fact]
        public void TestCustomTuple()
        {
            var pickler = new Pickler();
            var obj = new MyTuple(1, 2.0f, true);
            RoundTrip.Assert(pickler, obj);

            // Box into a plain tuple
            var tuple = Tuple.Create<Tuple<int, float>>(obj);
            RoundTrip.Assert(pickler, tuple);
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
        public void TestRecursiveTuple()
        {
            var array = new Tuple<object, int>[2];
            array[0] = Tuple.Create<object, int>(null, 2);

            var recursive = Tuple.Create<object, int>(array, 4);
            array[1] = recursive;

            Assert.Same(recursive, ((Tuple<object, int>[])recursive.Item1)[1]);

            var pickler = new Pickler();
            // The first item of this tuple is the array, of which the second item is the tuple
            var result = RoundTrip.Do(pickler, recursive);

            // Check the int value is the same
            Assert.Equal(recursive.Item2, result.Item2);
            // Pull out the result array
            var resultArray = (Tuple<object, int>[])result.Item1;
            // Check the tuple objects are the same
            Assert.Same(result, resultArray[1]);
        }
    }
}