using FsCheck;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
