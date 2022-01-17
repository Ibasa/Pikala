﻿using Ibasa.Pikala.Tests.TestTypes;
using System;
using System.IO;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public sealed class FactLargeTest : FactAttribute
    {
        public FactLargeTest()
        {
            if (IntPtr.Size == 4)
            {
                Skip = "Skipping due to 32bit process";
            }
        }
    }

    [CollectionDefinition("LargeTests", DisableParallelization = true)]
    public class LargeTests
    {
        private void LargeArrayTest<X, Y>(Func<X> generator, Func<X, Y> checker, long expectedSize)
        {
            // Large array tests output so much data they won't fit into a .NET byte array, we have to serialize to a FileStream.
            var file = Path.GetTempFileName();
            try
            {
                Y Write()
                {
                    var value = generator();

                    using var fileStream = File.OpenWrite(file);
                    var pickler = new Pickler();
                    pickler.Serialize(fileStream, value);

                    // Check the size is what we expected
                    Assert.Equal(expectedSize, fileStream.Length);

                    // These are huge arrays so we just check a subset of properties
                    return checker(value);
                }

                Y Read()
                {
                    using var fileStream = File.OpenRead(file);
                    var pickler = new Pickler();
                    var value = (X)pickler.Deserialize(fileStream);
                    return checker(value);
                }

                var x = Write();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                var y = Read();
                Assert.Equal(x, y);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [FactLargeTest]
        public void Test2GBPrimitiveArray()
        {
            LargeArrayTest(() =>
            {
                var value = (long[])Array.CreateInstance(typeof(long), 268435456);
                value[0] = 1L;
                value[value.Length - 1] = 100L;
                return value;
            },
            value =>
            {
                // These are huge arrays so we just check that the lengths, first, and last elements are correct.
                return (value.Length, value[0], value[value.Length - 1]);
            },
            2147483678);
        }

        [FactLargeTest]
        public void Test3GBPrimitiveArray()
        {
            LargeArrayTest(() =>
            {
                var value = (long[])Array.CreateInstance(typeof(long), 402653184);
                value[0] = 1L;
                value[value.Length - 1] = 100L;
                return value;
            },
            value =>
            {
                // These are huge arrays so we just check that the lengths, first, and last elements are correct.
                return (value.Length, value[0], value[value.Length - 1]);
            },
            3221225502);
        }

        [FactLargeTest]
        public void Test2GBTupleArray()
        {
            var gb2 = 2L * 1024L * 1024L * 1024L;
            var length = gb2 / (8 + 4);

            LargeArrayTest(() =>
            {
                var value = (ValueTuple<int, double>[])Array.CreateInstance(typeof(ValueTuple<int, double>), length);
                value[0] = ValueTuple.Create(2, 3.14);
                value[value.Length - 1] = new ValueTuple<int, double>(-14, double.NegativeInfinity);
                return value;
            },
            value =>
            {
                // These are huge arrays so we just check that the lengths, first, and last elements are correct.
                return (value.Length, value[0], value[value.Length - 1]);
            },
            2147483711);
        }

        [FactLargeTest]
        public void Test2GBComplexArray()
        {
            var gb2 = 2L * 1024L * 1024L * 1024L;
            var sizeofT = System.Runtime.CompilerServices.Unsafe.SizeOf<StructureType>();
            var length = gb2 / sizeofT;

            LargeArrayTest(() =>
            {
                var value = (StructureType[])Array.CreateInstance(typeof(StructureType), length);
                value[0] = new StructureType() { Foo = 2, Bar = 3.14 };
                value[value.Length - 1] = new StructureType() { Foo = -14, Bar = double.NegativeInfinity };
                return value;
            },
            value =>
            {
                // These are huge arrays so we just check that the lengths, first, and last elements are correct.
                return (value.Length, value[0], value[value.Length - 1]);
            },
            2147483883);
        }

        [FactLargeTest]
        public void Test3GBComplexArray()
        {
            var gb2 = 3L * 1024L * 1024L * 1024L;
            var sizeofT = System.Runtime.CompilerServices.Unsafe.SizeOf<StructureType>();
            var length = gb2 / sizeofT;

            LargeArrayTest(() =>
            {
                var value = (StructureType[])Array.CreateInstance(typeof(StructureType), length);
                value[0] = new StructureType() { Foo = 2, Bar = 3.14 };
                value[value.Length - 1] = new StructureType() { Foo = -14, Bar = double.NegativeInfinity };
                return value;
            },
            value =>
            {
                // These are huge arrays so we just check that the lengths, first, and last elements are correct.
                return (value.Length, value[0], value[value.Length - 1]);
            },
            3221225707);
        }
    }
}
