using FsCheck;
using FsCheck.Xunit;
using System;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class ObjectTests
    {
        [Property]
        public Property TestKeyValuePair()
        {
            var pickler = new Pickler();
            return Prop.ForAll(
                Arb.From<System.Collections.Generic.KeyValuePair<int, string>>(),
                value => RoundTrip.Assert(pickler, value));
        }

        [Fact]
        public void TestTimeZoneInfo()
        {
            var pickler = new Pickler();
            RoundTrip.Assert(pickler, TimeZoneInfo.Local);
            RoundTrip.Assert(pickler, TimeZoneInfo.Utc);
        }

        [Fact]
        public void TestNotExplcitlySerialisableObject()
        {
            var pickler = new Pickler();

            var value = new TestTypes.PlainObject();
            value.X = 2;
            value.Y = "hello world";
            value.Z = (1, 4);

            RoundTrip.Assert(pickler, value);
        }

        [Fact]
        public void TestPrivateFields()
        {
            var pickler = new Pickler();

            var value = new TestTypes.DerivedClassWithPrivateFields(2, 2);
            RoundTrip.Assert(pickler, value);
        }

        [Fact]
        public void TestSelfReferentialObject()
        {
            var pickler = new Pickler();

            var value = new TestTypes.SelfReferenceObject();
            value.Foo = 124;
            value.Myself = value;

            var result = RoundTrip.Do(pickler, value);

            Assert.Same(value, value.Myself);
        }

        [Fact]
        public void TestCircularClasses()
        {
            var pickler = new Pickler();
            var aValue = new TestTypes.CircularClassA() { Foo = 4.5 };
            var bValue = new TestTypes.CircularClassB() { Bar = 123m };

            aValue.B = bValue;
            bValue.A = aValue;

            var aResult = RoundTrip.Do<object>(pickler, aValue);
            Assert.Equal(aValue.ToString(), aResult.ToString());

            var bResult = RoundTrip.Do<object>(pickler, bValue);
            Assert.Equal(bValue.ToString(), bResult.ToString());
        }
    }
}