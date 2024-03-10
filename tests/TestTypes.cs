using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

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

        public abstract class BaseClassWithPrivateFields
        {
            private int _x;

            public BaseClassWithPrivateFields(int x)
            {
                _x = x;
            }

            public int X => _x;
        }

        public sealed class DerivedClassWithPrivateFields : BaseClassWithPrivateFields
        {
            private int _x;

            public DerivedClassWithPrivateFields(int x, int y) : base(y)
            {
                _x = x;
            }

            public override bool Equals(object obj)
            {
                if (obj is DerivedClassWithPrivateFields other)
                {
                    return X == other.X && _x == other._x;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_x, X);
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

        public enum EnumurationType { Foo = 2, Bar = 3 }

        public delegate int DelegateType(int x, int y);

        public delegate int GenericDelegateType<T>(T x);

        public struct StructureType : IStructuralEquatable
        {
            public int Foo;
            public double Bar;

            public override string ToString()
            {
                return $"{Foo}, {Bar}";
            }

            public bool Equals(object other, IEqualityComparer comparer)
            {
                if (other is StructureType that)
                {
                    return comparer.Equals(Foo, that.Foo) && comparer.Equals(Bar, that.Bar);
                }
                return false;
            }

            public int GetHashCode(IEqualityComparer comparer)
            {
                return HashCode.Combine(Foo.GetHashCode(), Bar.GetHashCode());
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Foo.GetHashCode(), Bar.GetHashCode());
            }
        }

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

        public class ClassType : IStructuralEquatable
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

            public override bool Equals(object obj)
            {
                if (obj is ClassType that)
                {
                    return Foo == that.Foo && Bar == that.Bar;
                }
                return false;
            }

            public bool Equals(object other, IEqualityComparer comparer)
            {
                if (other is ClassType that)
                {
                    return comparer.Equals(Foo, that.Foo) && comparer.Equals(Bar, that.Bar);
                }
                return false;
            }

            public int GetHashCode(IEqualityComparer comparer)
            {
                return HashCode.Combine(Foo.GetHashCode(), Bar == null ? 0 : Bar.GetHashCode());
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Foo.GetHashCode(), Bar == null ? 0 : Bar.GetHashCode());
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

        public class ClassTypeWithIndexers
        {
            private string[] _current;

            public ClassTypeWithIndexers(string[] current)
            {
                _current = current;
            }

            public string this[int index]
            {
                get { return _current[index]; }
            }

            public int this[string value]
            {
                get { return Array.IndexOf(_current, value); }
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
        /// Class to test that memoization only cares about reference equality, not overriden Equals
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

        public class SelfReferenceStatic
        {
            public int Tag;

            public static System.Reflection.FieldInfo TagField;

            public static SelfReferenceStatic[] Selves;

            public override string ToString()
            {
                return Tag.ToString();
            }
        }

        public sealed class ClassTypeWithEvents
        {
            public event EventHandler FieldEvent;

            private EventHandler? _delegate = null;
            public event EventHandler PropertyEvent
            {
                add { _delegate += value; }
                remove { _delegate -= value; }
            }

            public void Invoke()
            {
                var args = new EventArgs();
                if (FieldEvent != null)
                {
                    FieldEvent(this, args);
                }
                if (_delegate != null)
                {
                    _delegate(this, args);
                }
            }

            public void AddHandler(EventHandler handler)
            {
                FieldEvent += handler;
                PropertyEvent += handler;
            }

            public void RemoveHandler(EventHandler handler)
            {
                FieldEvent -= handler;
                PropertyEvent -= handler;
            }
        }

        public sealed class ClassWithPointersAndRefs
        {
            public string OverloadTest1(int i)
            {
                return i.ToString();
            }

            public string OverloadTest1(ref int i)
            {
                i = 4;
                return i.ToString();
            }

            public unsafe string OverloadTest1(int* i)
            {
                *i = *i * 2;
                return (*i).ToString();
            }

            public string OverloadTest2(string s)
            {
                return s;
            }

            public string OverloadTest2(out string s)
            {
                s = "";
                return "hello";
            }

            public override string ToString()
            {
                // Weird ToString to test calling all the overloads
                unsafe
                {
                    var i = 0;
                    var a = OverloadTest1(i); // 0
                    var b = OverloadTest1(ref i); // 4
                    var c = OverloadTest1(&i); // 8
                    var d = OverloadTest2("world"); // world
                    var e = OverloadTest2(out d); // hello (and d = "")
                    return a + b + c + d + e; // 048hello
                }
            }
        }

        public sealed class ClassWithDefaults
        {
            public void Defaults(int i = 2, string x = "hi", string y = null, IComparer z = null, CancellationToken w = default)
            {

            }
        }

        public sealed class ClassWithLiterals
        {
            private const int integer = 1;
            private const string nonnullString = "hello";
            private const string nullString = null;
            private const object nullObject = null;
            private const EnumurationType enumuration = EnumurationType.Bar;

            public override string ToString()
            {
                // Strange ToString to test that the literals are set correctly
                // Have to use reflection because normally constants are just folded into the code inline
                var fields = typeof(ClassWithLiterals).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var results = new List<string>();
                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    if (value == null)
                    {
                        results.Add("null");
                    }
                    else
                    {
                        results.Add(value.ToString());
                    }
                }
                return String.Join(":", results);
            }
        }

        public sealed class RecursiveDelegate
        {
            public Func<int, int> SelfDelegate;

            public int SomeMethod(int i)
            {
                return i + 1;
            }
        }

        public interface InterfaceWithVariance<in T, out U, V, W>
            where V : class
            where W : new()
        {
            public U Method(T a, ref V b, ref W c);
        }

        public interface IIsDisposed : IDisposable
        {
            bool IsDisposed();
        }

        public sealed class InterfaceInheritance : IIsDisposed
        {
            bool disposed;

            public void Dispose()
            {
                disposed = true;
            }

            public bool IsDisposed()
            {
                return disposed;
            }

            public override string ToString()
            {
                return disposed.ToString();
            }
        }

        // Test that a typedef works even if it's only seen in a method body
        public sealed class TypeDefInMethod
        {
            private int _x;

            public TypeDefInMethod(int x)
            {
                _x = x;
            }

            public int Method(int y)
            {
                var obj = new PlainObject()
                {
                    X = _x,
                    Z = (y, y),
                };

                return obj.GetHashCode();
            }
        }
    }
}
