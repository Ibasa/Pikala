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

        public static Arbitrary<TestTypes.StructureType> StructureType()
        {
            var genInt = Arb.Default.Int32().Generator;
            var genDbl = Arb.Default.Float().Generator;
            var genZip = Gen.Zip(genInt, genDbl);
            var gen = genZip.Select(tuple =>
            {
                var obj = new TestTypes.StructureType();
                obj.Foo = tuple.Item1;
                obj.Bar = tuple.Item2;
                return obj;
            });

            return Arb.From(gen);
        }

        public static Arbitrary<TestTypes.ClassType> ClassType()
        {
            var genBool = Arb.Default.Bool().Generator;
            var genStr = Arb.Default.String().Generator;
            var genInt = Arb.Default.Int32().Generator;
            var genZip = Gen.Zip(genBool, genStr, genInt);
            var gen = genZip.Select(tuple =>
            {
                if (tuple.Item1)
                {
                    var obj = new TestTypes.ClassType(tuple.Item2);
                    obj.Foo = tuple.Item3;
                    return obj;
                }
                return null;
            });

            return Arb.From(gen);
        }
    }
}
