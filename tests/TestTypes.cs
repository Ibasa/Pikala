﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Ibasa.Pikala.Tests
{
    namespace TestTypes
    {
        public class PlainObject
        {
            public int X;
            public string Y;
            public (int, int) Z;

            public override bool Equals(object obj)
            {
                if (obj is PlainObject)
                {
                    var other = (PlainObject)obj;
                    return X == other.X && Y == other.Y && Z == other.Z;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(X, Y, Z);
            }
        }

        public class NestingClass
        {
            public enum NestedEnum { Xs, Ys }
        }

        public static class GenericMethods
        {
            public static T DoIt<T, U>(T x, U y)
            {
                return x;
            }

            public static string DoIt<T>(T x, int y)
            {
                return x.ToString() + " " + y.ToString();
            }
        }

        public sealed class SelfReferenceObject
        {
            public int Foo;
            public SelfReferenceObject Myself;
        }

        public sealed class SelfRefernceISerialisable : ISerializable
        {
            public int Foo;
            public SelfRefernceISerialisable Myself;

            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                info.AddValue("Foo", Foo);
                info.AddValue("Myself", Myself);
            }

            public SelfRefernceISerialisable()
            {

            }

            private SelfRefernceISerialisable(SerializationInfo info, StreamingContext context)
            {
                Foo = info.GetInt32("Foo");
                Myself = info.GetValue("Myself", typeof(SelfRefernceISerialisable)) as SelfRefernceISerialisable;
            }
        }

        public enum EnumurationType { Foo = 2, Bar = 3 }

        public delegate int DelegateType(int x, int y);

        public delegate int GenericDelegateType<T>(T x);

        [Serializable]
        public struct StructureType
        {
            public int Foo;
            public double Bar;

            public override string ToString()
            {
                return $"{Foo}, {Bar}";
            }
        }

        [Serializable]
        public struct StructureTypeWithInterface : IDisposable
        {
            public int Foo;
            public double Bar;

            public void Dispose()
            {
                Console.WriteLine("Called dispose");
            }

            public override string ToString()
            {
                return $"{Foo}, {Bar}";
            }
        }

        [Serializable]
        public struct StructureTypeWithGeneric<T> : IEquatable<StructureTypeWithGeneric<T>>
        {
            public T Foo;

            public bool Equals(StructureTypeWithGeneric<T> other)
            {
                return Foo.Equals(other.Foo);
            }

            public override bool Equals(object obj)
            {
                if (obj is StructureTypeWithGeneric<T>)
                {
                    return Equals((StructureTypeWithGeneric<T>)obj);
                }
                return false;
            }

            public override int GetHashCode()
            {
                return Foo.GetHashCode();
            }

            public override string ToString()
            {
                return Foo.ToString();
            }
        }

        [Serializable]
        public class ClassType
        {
            public int Foo;
            private string Bar;

            public ClassType(string bar)
            {
                Foo = 0;
                Bar = bar;
            }

            public int DoInvoke(int value)
            {
                return value + Foo;
            }

            public override string ToString()
            {
                return $"{Foo}, {Bar}";
            }
        }

        public class ClassTypeWithProperties
        {
            private string _current;

            public ClassTypeWithProperties(string current)
            {
                _current = current;
            }

            public string Current => _current + " world";

            public override string ToString()
            {
                return Current;
            }
        }

        public class ClassTypeWithExplcitInterface : IDisposable
        {
            readonly int X;

            public ClassTypeWithExplcitInterface(int x)
            {
                X = x;
            }

            void IDisposable.Dispose()
            {
                Console.WriteLine("Called IDisposable.Dispose() on {0}", X);
            }

            public void Dispose()
            {
                Console.WriteLine("Called Dispose() on {0}", X);
            }
        }

        public class ClassTypeWithInterfaceProperties : IEnumerator<string>
        {
            private string _current;

            public ClassTypeWithInterfaceProperties(string current)
            {
                _current = current;
            }

            string IEnumerator<string>.Current => _current;

            object IEnumerator.Current => _current;

            public string Current => _current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return false;
            }

            public void Reset()
            {

            }
        }

        public class ClassTypeWithNestedClass
        {
            public sealed class InnerClass
            {
                public int Foo;
            }

            private float Bar;

            public ClassTypeWithNestedClass(float bar)
            {
                Bar = bar;
            }

            public InnerClass GetInner()
            {
                return new InnerClass { Foo = (int)Bar };
            }

            public override string ToString()
            {
                return GetInner().Foo.ToString();
            }
        }

        public class ClassTypeWithNestedStruct
        {
            public struct InnerStruct
            {
                public int Foo;
            }

            private float Bar;

            public ClassTypeWithNestedStruct(float bar)
            {
                Bar = bar;
            }

            public InnerStruct GetInner()
            {
                return new InnerStruct { Foo = (int)Bar };
            }

            public override string ToString()
            {
                return GetInner().Foo.ToString();
            }
        }

        public static class StaticNestingType
        {
            public struct InnerStruct
            {
                public int Foo;
            }

            public enum InnerEnum
            {
                InnerX,
                InnerY,
            }
        }

        public sealed class CircularClassB
        {
            public decimal Bar;
            public CircularClassA A;

            public override string ToString()
            {
                return $"I'm a B with an A.{A?.Foo}";
            }
        }

        public sealed class CircularClassA
        {
            public double Foo;
            public CircularClassB B;

            public override string ToString()
            {
                return $"I'm an A with a B.{B?.Bar}";
            }
        }

        public sealed class StaticCtorClass
        {
            static int Setup;
            static StaticCtorClass()
            {
                Setup = 1;
            }

            public StaticCtorClass()
            {
                Foo = 2.3;
            }

            public double Foo;

            public override string ToString()
            {
                return $"Static = {Setup} Instance = {Foo}";
            }
        }

        [SelfReferencing(Property = "SelfReferencingAttribute", Tag = 0)]
        [System.AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
        sealed class SelfReferencingAttribute : Attribute
        {
            [SelfReferencing(Property = ".ctor", Tag = 1)]
            public SelfReferencingAttribute()
            {
            }

            [SelfReferencing(Property = "Property", Tag = 2)]
            public string Property { get; set; }

            [SelfReferencing(Property = "Tag", Tag = 3)]
            public int Tag;
        }

        public abstract class AbstractClass
        {
            public abstract int GetSomething();

            public virtual int GetSomethingElse()
            {
                return 5;
            }

            public override string ToString()
            {
                return $"{GetSomething()}, {GetSomethingElse()}";
            }
        }

        public sealed class ConcreteClass : AbstractClass
        {
            public override int GetSomething()
            {
                return 4;
            }
        }

        public sealed class AsyncClasss
        {
            public static async System.Threading.Tasks.Task<int> GetIntAsync()
            {
                int i = 4;
                await System.Threading.Tasks.Task.Delay(1);
                return i + 1;
            }
        }

        public static class StaticClass
        {
            public static string SwitchMethod(int i)
            {
                switch (i)
                {
                    case 0: return "Hello";
                    case 1: return " ";
                    case 2: return "World";
                    case 3: return "!";
                }

                throw new Exception("i out of range");
            }
        }

        public class SelfReferencingProperty
        {
            private string _current;

            public SelfReferencingProperty(string current)
            {
                _current = current;
            }

            public SelfReferencingProperty Current
            {
                get { return this; }
                set { _current = value._current; }
            }

            public override string ToString()
            {
                return _current;
            }
        }

        /// <summary>
        /// Class to test that memoisation only cares about reference equality, not overrider equality
        /// </summary>
        public sealed class ReferenceEqualityClass
        {
            public int Tag { get; set; }

            public override bool Equals(object obj)
            {
                if (obj is ReferenceEqualityClass other)
                {
                    return other.Tag == Tag;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return Tag.GetHashCode();
            }
        }
    }
}
