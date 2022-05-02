using FsCheck;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ibasa.Pikala.Tests
{
    public static class Utils
    {
        public static Pickler CreateIsolatedPickler(Func<System.Reflection.Assembly, AssemblyPickleMode> assemblyPickleMode = null)
        {
            var alc = new System.Runtime.Loader.AssemblyLoadContext("Pikala.Tests", true);
            return new Pickler(assemblyPickleMode, alc);
        }

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

            var didBreak = true;
            while (didBreak)
            {
                var indicesCopy = (int[])indices.Clone();
                // Just incase we mess up and mutate indices
                iter(indicesCopy);

                didBreak = false;
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
            }
        }

        private static int[] Factors(int n)
        {
            var primes = Primes().GetEnumerator();
            primes.MoveNext();

            var factors = new List<int>();

            while (primes.Current < n / 2)
            {
                if (n % primes.Current == 0)
                {
                    // n can be divided by current prime, so do so and add to factor list
                    n /= primes.Current;
                    factors.Add(primes.Current);
                }
                else
                {
                    primes.MoveNext();
                }
            }

            return factors.ToArray();
        }

        private static IEnumerable<int> Primes()
        {
            yield return 2;
            yield return 3;

            // The p > 0 looks odd, but here we're relying on wrap around behaviour that p will go negative after (Int.MaxValue - 1 + 2)
            for (int p = 5; p > 0; p += 2)
            {
                var factors = Factors(p);
                if (factors.Length == 0)
                {
                    yield return p;
                }
            }
        }


        /// <summary>
        /// Like FsCheck Gen.Piles but over a product not a sum
        /// </summary>
        private static Gen<int[]> Product(int k, int product)
        {
            if (k <= 0)
            {
                return Gen.Constant(Array.Empty<int>());
            }

            var factors = Factors(product);

            Gen<int[]> productGen;
            if (factors.Length == k)
            {
                // Nothing to do but the shuffle over the constant list
                productGen = Gen.Constant(factors);
            }
            else if (factors.Length < k)
            {
                // k >= 1 but factors might be empty in which case add "product" once then 1's

                if (factors.Length == 0)
                {
                    // Add "product" and then some 1's then shuffle
                    var diff = k - factors.Length - 1;
                    productGen = Gen.Constant(Enumerable.Append(Enumerable.Concat(factors, Enumerable.Repeat(1, diff)), product).ToArray());
                }
                else
                {
                    // Just need to add some 1's then shuffle
                    var diff = k - factors.Length;
                    productGen = Gen.Constant(Enumerable.Concat(factors, Enumerable.Repeat(1, diff)).ToArray());
                }
            }
            else
            {
                // The complex case, we've got more factors than we want so we need to reduce some of them together
                // but we want that process to be random as well, so recurse via a Gen
                Gen<int[]> Reduce(int[] factors)
                {
                    // Got to the right length
                    if (factors.Length == k) { return Gen.Constant(factors); }
                    // Only one factor can't reduce anymore
                    if (factors.Length == 1) { return Gen.Constant(factors); }

                    // Pick two elements to reduce together
                    var iGen = Gen.Choose(0, factors.Length - 1);
                    var jGen = Gen.Choose(0, factors.Length - 2);

                    return GenBuilder.gen.Bind(Gen.Zip(iGen, jGen), Microsoft.FSharp.Core.FuncConvert.FromFunc<Tuple<int, int>, Gen<int[]>>(ij =>
                    {
                        var (i, j) = ij;
                        // Fix up j so it doesn't equal i, and so i < j
                        if (i <= j) { ++j; }
                        else
                        {
                            int temp = i;
                            i = j;
                            j = temp;
                        }

                        // We know i < j, so we can copy up to j skip j copy the rest then overwrite at i
                        var newFactors = Enumerable.Concat(factors.Take(j), factors.Skip(j + 1)).ToArray();
                        newFactors[i] = factors[i] * factors[j];

                        return Reduce(newFactors);
                    }));
                }

                productGen = Reduce(factors);
            }


            return GenBuilder.gen.Bind(productGen, Microsoft.FSharp.Core.FuncConvert.FromFunc<int[], Gen<int[]>>(product => Gen.Shuffle(product)));
        }

        public static Arbitrary<Array> ArbitraryArray<T>(Gen<T> gen)
        {
            // Dimensions needs to be at least 1 and less than or equal to 32
            var genDimensions = Gen.Sized(size => Gen.Choose(1, Math.Min(size, 32)));

            var genBoundsAndLengths = GenBuilder.gen.Bind(genDimensions, Microsoft.FSharp.Core.FuncConvert.FromFunc<int, Gen<Tuple<int[], int[]>>>(dimensions =>
            {
                // bounds need to be 0 or positive
                var genNatural = Gen.Sized(size => Gen.Choose(0, size));
                var genBounds = Gen.ArrayOf(dimensions, genNatural);

                // Total length should be a function of size but no larger than 0X7FEFFFFF
                var genLengths = GenBuilder.gen.Bind(genNatural, Microsoft.FSharp.Core.FuncConvert.FromFunc<int, Gen<int[]>>(length =>
                {
                    return Product(dimensions, Math.Min(0X7FEFFFFF, length));
                }));

                return Gen.Zip(genLengths, genBounds);
            }));

            var genArray = GenBuilder.gen.Bind(genBoundsAndLengths, Microsoft.FSharp.Core.FuncConvert.FromFunc<Tuple<int[], int[]>, Gen<Array>>(lengthsAndbounds =>
            {
                var (lengths, bounds) = lengthsAndbounds;
                var totalItems = 1;
                foreach (var length in lengths) { totalItems *= length; }

                var genItems = Gen.ArrayOf(totalItems, gen);

                return Gen.Map(Microsoft.FSharp.Core.FuncConvert.FromFunc<T[], Array>(items =>
                {
                    var array = Array.CreateInstance(typeof(T), lengths, bounds);

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

                var elementType = arr.GetType().GetElementType();

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

                    var newArr = Array.CreateInstance(elementType, lengths, lowerBounds);

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