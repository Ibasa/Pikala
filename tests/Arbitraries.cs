using FsCheck;
using System;

namespace Ibasa.Pikala.Tests
{
    public static class Arbitraries
    {
        public static Arbitrary<ValueTuple> ValueTuple()
        {
            var gen = Gen.Fresh(() => System.ValueTuple.Create());
            return Arb.From(gen);
        }
    }
}
