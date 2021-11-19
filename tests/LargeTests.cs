using System;
using Xunit;
using System.IO;

namespace Ibasa.Pikala.Tests
{
    public sealed class FactSkip32Bit : FactAttribute
    {
        public FactSkip32Bit()
        {
            if (IntPtr.Size == 4)
            {
                Skip = "Skipping due to 32bit process";
            }
        }
    }

    public class LargeTests
    {
        private void LargeArrayTest<X, Y>(Func<X> generator, Func<X, Y> checker)
        {
            var pickler = new Pickler();
            // Large array tests output so much data they won't fit into a .NET byte array, we have to serialize to a FileStream.
            var file = Path.GetTempFileName();
            try
            {
                Y Write()
                {
                    var value = generator();

                    using var fileStream = File.OpenWrite(file);
                    pickler.Serialize(fileStream, value);

                    // These are huge arrays so we just check a subset of properties
                    return checker(value);
                }

                Y Read()
                {
                    using var fileStream = File.OpenRead(file);
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

        [FactSkip32Bit]
        public void Test2GBArray()
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
            });
        }

        [FactSkip32Bit]
        public void Test3GBArray()
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
            });
        }
    }
}
