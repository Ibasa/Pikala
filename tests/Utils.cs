using FsCheck;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ibasa.Pikala.Tests
{
    public static class Utils
    {
        public static void IterateArray(Array array, Action<int[]> iter)
        {
            var rank = array.Rank;
            var indices = new int[rank];
            var anyEmpty = false;
            for (int dimension = 0; dimension < rank; ++dimension)
            {
                indices[dimension] = array.GetLowerBound(dimension);
                anyEmpty |= array.GetLength(dimension) == 0;
            }

            if (anyEmpty) return;

            void Iterate()
            {
                var indicesCopy = (int[])indices.Clone();
                // Just incase we mess up and mutate indices
                iter(indicesCopy);

                var didBreak = false;
                for (int dimension = rank - 1; dimension >= 0; --dimension)
                {
                    var next = indices[dimension] + 1;
                    if (next < array.GetLowerBound(dimension) + array.GetLength(dimension))
                    {
                        indices[dimension] = next;
                        didBreak = true;
                        break;
                    }
                    else
                    {
                        indices[dimension] = array.GetLowerBound(dimension);
                    }
                }

                if (didBreak) { Iterate(); }
            }

            Iterate();
        }

        public static Arbitrary<Array> ArbitraryArray<T>(Gen<T> gen)
        {
            var genNatural = Gen.Sized(size => Gen.Choose(0, size));
            var genBounds = Gen.Two(genNatural);
            bool Filter(Tuple<int, int>[] array)
            {
                // Dimensions needs to be at least 1 and less than or equal to 32
                return 0 < array.Length && array.Length <= 32;
            }
            var genDimensions = Gen.Filter(Microsoft.FSharp.Core.FuncConvert.FromFunc(new Func<Tuple<int, int>[], bool>(Filter)), Gen.ArrayOf(genBounds));

            var genArray = GenBuilder.gen.Bind(genDimensions, Microsoft.FSharp.Core.FuncConvert.FromFunc<Tuple<int, int>[], Gen<Array>>(dimensions =>
            {
                var lengths = dimensions.Select(item => item.Item1).ToArray();
                var lowerBounds = dimensions.Select(item => item.Item2).ToArray();
                var totalItems = 1;
                foreach (var length in lengths) { totalItems *= length; }

                var genItems = Gen.ArrayOf(totalItems, gen);

                return Gen.Map(Microsoft.FSharp.Core.FuncConvert.FromFunc<T[], Array>(items =>
                {
                    var array = Array.CreateInstance(typeof(int), lengths, lowerBounds);

                    var index = 0;
                    IterateArray(array, indices =>
                    {
                        var item = items[index++];
                        array.SetValue(item, indices);
                    });
                    return array;
                }), genItems);
            }));


            IEnumerable<Array> Shrink(Array arr)
            {
                var rank = arr.Rank;
                for (int dimension = 0; dimension < rank; ++dimension)
                {
                    // If any dimension is empty just return nothing
                    if (arr.GetLength(dimension) == 0) yield break;
                }

                // For each dimension shrink in that dimension
                for (int dimension = 0; dimension < rank; ++dimension)
                {
                    var lengths = new int[rank];
                    var lowerBounds = new int[rank];
                    for (int j = 0; j < rank; ++j)
                    {
                        lengths[j] = arr.GetLength(j);
                        lowerBounds[j] = arr.GetLowerBound(j);
                    }

                    lengths[dimension] = lengths[dimension] - 1;

                    var newArr = Array.CreateInstance(typeof(T), lengths, lowerBounds);

                    IterateArray(newArr, indices =>
                    {
                        var item = arr.GetValue(indices);
                        newArr.SetValue(item, indices);
                    });

                    yield return newArr;
                }
            }
            return Arb.From(genArray, new Func<Array, IEnumerable<Array>>(Shrink));
        }
    }
}