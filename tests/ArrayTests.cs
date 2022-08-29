using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class ArrayTests
    {
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
        public Property TestNullableArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<double?[]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestSystemEnumArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
            Arb.From<System.ConsoleColor[]>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestCustomEnumArray()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
            Arb.From<TestTypes.EnumurationType[]>(),
                value => RoundTrip.Assert(pickler, value));
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
        public void TestSelfReferentialArray()
        {
            var pickler = new Pickler();

            var array = new object[1];
            array[0] = array;

            var result = RoundTrip.Do(pickler, array);

            Assert.Same(result, result[0]);
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
        public Property TestNestedIntArray()
        {
            var pickler = new Pickler();

            return Prop.ForAll(
                Arb.From<int[][][]>(),
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

        [Fact]
        public void TestNonZeroLowerBound()
        {
            var pickler = new Pickler();

            var array = Array.CreateInstance(typeof(double), new int[] { 2 }, new int[] { 1 });
            array.SetValue(0.0, 1);
            array.SetValue(1.0, 2);

            var result = RoundTrip.Do(pickler, array);

            Assert.Equal(2, result.GetLength(0));
            Assert.Equal(1, result.GetLowerBound(0));
            Assert.Equal(2, result.GetUpperBound(0));
            Assert.Equal(0.0, result.GetValue(1));
            Assert.Equal(1.0, result.GetValue(2));
        }

        [Property]
        public Property TestVariablesizeIntArray()
        {
            var pickler = new Pickler();

            return Prop.ForAll(
                Utils.ArbitraryArray(Arb.Default.Int32().Generator),
                value => RoundTrip.Assert(pickler, value));
        }

        [Fact]
        public void TestArrayVariance()
        {
            var pickler = new Pickler();

            var stringArray = new string[2] { "A", "B" };
            var objArray = (object[])stringArray;
            // Wrap in a tuple so the array type is staticly known
            var tuple = Tuple.Create(objArray);
            var result = RoundTrip.Do(pickler, tuple);

            var array = tuple.Item1;
            // Should be able to write this
            array[0] = "C";
            // This should fail
            Assert.Throws<ArrayTypeMismatchException>(() => array[1] = 4);
        }
    }
}