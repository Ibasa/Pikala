using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    delegate (Type, FsCheck.Random.StdGen) GenerateType(FsCheck.Random.StdGen stdGen);

    // Small class to do structural checks of Lists and Dictionaries in our test set
    class EqualityComparer : System.Collections.IEqualityComparer
    {
        public new bool Equals(object x, object y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;

            if (x is System.Collections.IStructuralEquatable xeq)
            {
                return xeq.Equals(y, this);
            }

            // If these are dictionaries
            if (x is System.Collections.IDictionary xd && y is System.Collections.IDictionary yd)
            {
                if (xd.Count != yd.Count) return false;

                var enumerator = xd.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (!yd.Contains(enumerator.Key)) return false;

                    var yvalue = yd[enumerator.Key];

                    if (!Equals(enumerator.Value, yvalue)) return false;
                }

                return true;
            }

            // If these are lists
            if (x is System.Collections.IList xl && y is System.Collections.IList yl)
            {
                if (xl.Count != yl.Count) return false;

                for (int i = 0; i < xl.Count; ++i)
                {
                    if (!Equals(xl[i], yl[i])) return false;
                }

                return true;
            }

            return x.Equals(y);
        }

        public int GetHashCode(object obj)
        {
            return obj.GetHashCode();
        }
    }

    public class CompatibilityTests
    {
        static int Next(ref FsCheck.Random.StdGen stdGen)
        {
            var (z, next) = FsCheck.Random.stdNext(stdGen);
            stdGen = next;
            return z;
        }

        static int Next(int min, int max, ref FsCheck.Random.StdGen stdGen)
        {
            var (z, next) = FsCheck.Random.range.Invoke(Tuple.Create(min, max)).Invoke(stdGen);
            stdGen = next;
            return z;
        }

        static object StaticGenericInvoke(System.Reflection.MethodInfo method, Type[] typeArguments, object[] argumenets)
        {
            return method.MakeGenericMethod(typeArguments).Invoke(null, argumenets);
        }

        static object GenericInvoke(System.Reflection.MethodInfo method, Type[] typeArguments, object obj, object[] argumenets)
        {
            return method.MakeGenericMethod(typeArguments).Invoke(obj, argumenets);
        }

        static System.Reflection.MethodInfo ArbFrom
        {
            get
            {
                //FsCheck.Arb.From<T>
                return typeof(FsCheck.Arb).GetMethod("From", 1, Type.EmptyTypes);
            }
        }

        static System.Reflection.MethodInfo GenEval
        {
            get
            {
                //FsCheck.Gen.Eval<T>
                return typeof(FsCheck.Gen).GetMethod("Eval");
            }
        }

        static object GetArbGenerator(object arb)
        {
            var type = arb.GetType();
            var prop = type.GetProperty("Generator");
            return prop.GetMethod.Invoke(arb, null);
        }

        static object Eval(object gen, Type type, ref FsCheck.Random.StdGen stdGen)
        {
            var size = Next(1, 100, ref stdGen);
            var obj = StaticGenericInvoke(GenEval, new Type[] { type }, new object[] { size, stdGen, gen });
            return obj;
        }

        static (Type, FsCheck.Random.StdGen) GenerateArray(FsCheck.Random.StdGen stdGen)
        {
            var element = GenerateType(ref stdGen);
            // TODO FsCheck only supports 1 and 2 arrays via Arb.From<T>,
            // oddly MakeArrayType(int rank) makes arrays types that FsCheck can't handle.
            //var rank = Next(1, 2, ref stdGen);
            return (element.MakeArrayType(), stdGen);
        }

        static (Type, FsCheck.Random.StdGen) GenerateDictionary(FsCheck.Random.StdGen stdGen)
        {
            // Limit to the basic types for dictionary keys
            var z = Next(0, BasicTypes.Length - 1, ref stdGen);
            var key = BasicTypes[z];
            var value = GenerateType(ref stdGen);
            var dictType = typeof(Dictionary<,>).MakeGenericType(key, value);
            return (dictType, stdGen);
        }

        static (Type, FsCheck.Random.StdGen) GenerateList(FsCheck.Random.StdGen stdGen)
        {
            var item = GenerateType(ref stdGen);
            var listType = typeof(List<>).MakeGenericType(item);
            return (listType, stdGen);
        }

        static (Type, FsCheck.Random.StdGen) GenerateTuple(FsCheck.Random.StdGen stdGen)
        {
            var itemTypes = Enumerable.Range(0, Next(1, 7, ref stdGen)).Select(_ => GenerateType(ref stdGen)).ToArray();

            var tupleTypes = new Type[]
            {
                typeof(Tuple<>), typeof(Tuple<,>), typeof(Tuple<,,>), typeof(Tuple<,,,>),
                typeof(Tuple<,,,,>), typeof(Tuple<,,,,,>), typeof(Tuple<,,,,,,>),
            };

            var tupleType = tupleTypes[itemTypes.Length - 1].MakeGenericType(itemTypes);
            return (tupleType, stdGen);
        }

        static (Type, FsCheck.Random.StdGen) GenerateValueTuple(FsCheck.Random.StdGen stdGen)
        {
            var itemTypes = Enumerable.Range(0, Next(0, 7, ref stdGen)).Select(_ => GenerateType(ref stdGen)).ToArray();

            var tupleTypes = new Type[]
            {
                typeof(ValueTuple), typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
                typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
            };

            if (itemTypes.Length == 0)
            {
                return (tupleTypes[0], stdGen);
            }

            var tupleType = tupleTypes[itemTypes.Length].MakeGenericType(itemTypes);
            return (tupleType, stdGen);
        }

        static GenerateType FromConstant(Type type)
        {
            return stdGen => (type, stdGen);
        }

        static Type[] BasicTypes = new Type[]
            {
                typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long),
                typeof(char), typeof(sbyte), typeof(ushort), typeof(uint), typeof(ulong),
                typeof(string), typeof(ConsoleColor), typeof(TestTypes.EnumurationType),
                typeof(TestTypes.StructureType), typeof(TestTypes.ClassType),
            };

        static Type GenerateType(ref FsCheck.Random.StdGen stdGen)
        {
            // Generate an object randomly!
            var generators = (BasicTypes.Select(t => FromConstant(t))).Concat(new GenerateType[]
            {
                GenerateArray,
                GenerateDictionary,
                GenerateList,
                GenerateTuple,
                GenerateValueTuple,
            }).ToArray();


            var z = Next(0, generators.Length - 1, ref stdGen);
            var (obj, next) = generators[z](stdGen);
            stdGen = next;
            return obj;
        }

        static IEnumerable<(string, object)> GenerateObjectsInternal(int seed)
        {
            var stdGen = FsCheck.Random.mkStdGen(seed);

            yield return ("null", null);
            yield return ("Tuple[int,string]", Tuple.Create(123, "hello"));

            for (int i = 0; i < 100; ++i)
            {
                var type = GenerateType(ref stdGen);
                var arb = StaticGenericInvoke(ArbFrom, new Type[] { type }, Array.Empty<object>());
                var gen = GetArbGenerator(arb);
                var obj = Eval(gen, type, ref stdGen);
                yield return ($"Random_{i:D2}", obj);
            }
        }

        public static IEnumerable<object[]> GenerateObjects(int seed)
        {
            FsCheck.Arb.Register(typeof(Arbitraries));

            foreach (var (name, obj) in GenerateObjectsInternal(seed))
            {
                yield return new object[] { name, obj };
            }
        }

        private static string FindTestData([System.Runtime.CompilerServices.CallerFilePath] string file = "")
        {
            // We just want the file name not the full path
            file = Path.GetFileName(file);

            // Look up the current directory chain to find this file, TestData should be next to it
            var currentDirectory = new DirectoryInfo(Environment.CurrentDirectory);
            while (currentDirectory != null)
            {
                if (File.Exists(Path.Combine(currentDirectory.FullName, file)))
                {
                    break;
                }
                currentDirectory = currentDirectory.Parent;
            }
            if (currentDirectory == null)
            {
                throw new Exception($"Could not find {file}");
            }
            return Path.Combine(currentDirectory.FullName, "TestData");
        }

        [Theory]
        [MemberData(nameof(GenerateObjects), 0x178E12DF)]
        public void TestCompatibility(string name, object obj)
        {
            var pickler = new Pickler();
            var stream = new MemoryStream();
            pickler.Serialize(stream, obj);
            var actualBytes = stream.ToArray();

            // Different versions of the runtime will pull in different versions of dependent assemblies.
            // So we only expect deterministic results for the same runtime version.
            var version = $"{Environment.Version.Major}.{Environment.Version.Minor}";

            // If TestData is missing this will regenerate it
            var filePath = Path.Combine(FindTestData(), version, name + ".bin");
            byte[] expectedBytes;
            try
            {
                expectedBytes = File.ReadAllBytes(filePath);

                string? error = null;
                if (expectedBytes.Length != actualBytes.Length)
                {
                    error = $"Length did not match";
                }

                for (int i = 0; i < Math.Min(expectedBytes.Length, actualBytes.Length); i++)
                {
                    // Try to find the first byte that didn't match. This might not find anything because one is a subset is the other in which case we fall back to the error string set above.
                    if (expectedBytes[i] != actualBytes[i])
                    {
                        error = $"Byte at index {i} did not match. Expected {expectedBytes[i]}, actual {actualBytes[i]}";
                        break;
                    }
                }

                if (error != null)
                {
                    throw new Exception($"Serialised bytes did not match\nObject: {obj}\nExpected length: { expectedBytes.Length}\nActual length: { actualBytes.Length}\n{error}");
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllBytes(filePath, actualBytes);
                throw new Exception($"Wrote new serialised bytes, rerun test to verify determinism\nObject: {obj}");
            }

            // If we're regenerating this will just be the bytes we just wrote, but still worth checking read works
            var actualObj = pickler.Deserialize(new MemoryStream(expectedBytes));
            // We using a special equaility check here so we can do structural equality over dictionaries and similar types
            var comparer = new EqualityComparer();
            if (!comparer.Equals(obj, actualObj))
            {
                throw new Xunit.Sdk.EqualException(obj, actualObj);
            }
        }
    }
}
