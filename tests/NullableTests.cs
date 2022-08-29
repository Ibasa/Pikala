using FsCheck;
using FsCheck.Xunit;
using System;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class NullableTests
    {
        [Fact]
        public void TestNull()
        {
            var pickler = new Pickler();
            var result = RoundTrip.Do<int?>(pickler, null);
            Assert.Null(result);
        }

        [Fact]
        public void TestFields()
        {
            var pickler = new Pickler();
            int? x = 1;
            int? y = null;
            var z = Tuple.Create(x, y);
            var result = RoundTrip.Do(pickler, z);
            Assert.True(z.Item1.HasValue);
            Assert.Equal(1, z.Item1.Value);
            Assert.False(z.Item2.HasValue);
        }

        [Property]
        public Property TestInt()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<int?>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestEnum()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<TypeCode?>(),
                value => RoundTrip.Assert(pickler, value));
        }
    }
}