using FsCheck;
using FsCheck.Xunit;
using System;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class PrimitiveTests
    {
        [Fact]
        public void TestNull()
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, null);
        }

        [Fact]
        public void TestDBNull()
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, DBNull.Value);
        }

        [Property]
        public Property TestBool()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<bool>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestChar()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<char>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestByte()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<byte>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestSByte()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<sbyte>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestShort()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<short>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestUShort()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<ushort>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestInt()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<int>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestUInt()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<uint>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestLong()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<long>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestULong()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<ulong>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestFloat()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<float>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestDouble()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<double>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestDecimal()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<decimal>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestIntPtr()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<long>(),
                value => RoundTrip.Assert(pickler, new IntPtr(value)));
        }

        [Property]
        public Property TestUIntPtr()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<ulong>(),
                value => RoundTrip.Assert(pickler, new UIntPtr(value)));
        }

        [Property]
        public Property TestDateTime()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<DateTime>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestTimeSpan()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<TimeSpan>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Property]
        public Property TestString()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<string>(),
                value => RoundTrip.Assert(pickler, value));
        }
    }
}
