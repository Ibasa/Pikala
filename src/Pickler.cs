using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Reflection.Emit;

namespace Ibasa.Pikala
{
    public interface IReducer
    {
        public Type Type { get; }
        public (MethodBase, object?, object[]) Reduce(Type type, object obj);
    }

    enum PickleOperation : byte
    {
        Null = 0,

        // Primitives
        Boolean = 1,
        Byte = 2,
        SByte = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        IntPtr = 10,
        UIntPtr = 11,
        Char = 12,
        Double = 13,
        Single = 14,
        Decimal = 15,
        DBNull = 16,

        // Basic types
        String = 17,
        Enum = 18,
        Array = 19,
        SZArray = 20,

        // Memoised
        Memo = 21,

        // Reflection
        Mscorlib = 22,
        AssemblyRef = 23,
        ManifestModuleRef = 24,
        ModuleRef = 25,
        TypeRef = 26,
        FieldRef = 27,
        PropertyRef = 28,
        MethodRef = 29,
        ConstructorRef = 30,
        AssemblyDef = 31,
        ModuleDef = 32,
        TypeDef = 33,
        GenericInstantiation = 34,
        GenericParameter = 35,
        TVar = 36,
        MVar = 37,

        // Structs and classes
        Delegate = 38,
        Reducer = 39,
        ISerializable = 40,
        Object = 41,

        // This is written as a byte so we're limited to 255 operations
    }

    enum TypeDef
    {
        Enum,
        Delegate,
        Struct,
        Class,
    }

    /// <summary>
    /// How the assembly should be pickled, either explictly by reference or by value or the implict default.
    /// </summary>
    /// <remarks>
    /// The default behaviour is to pickle dynamic assemblies by value and all other assemblies by reference.
    /// </remarks>
    public enum AssemblyPickleMode
    {
        Default = 0,
        PickleByReference,
        PickleByValue,
    }

    public sealed partial class Pickler
    {
        private static readonly Assembly mscorlib = typeof(int).Assembly;

        private static OpCode[] _oneByteOpCodes;
        private static OpCode[] _twoByteOpCodes;

        static Pickler()
        {
            // For converting raw IL bytes back into serialisable instructions
            _oneByteOpCodes = new OpCode[0xe1];
            _twoByteOpCodes = new OpCode[0xef];
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var opCode = (OpCode)field.GetValue(null);

                if (opCode.OpCodeType == OpCodeType.Nternal) continue;

                if (opCode.Size == 1)
                {
                    _oneByteOpCodes[opCode.Value] = opCode;
                }
                else
                {
                    _twoByteOpCodes[opCode.Value & 0xFF] = opCode;
                }
            }
        }

        private Func<Assembly, AssemblyPickleMode> _assemblyPickleMode;
        private Dictionary<Type, IReducer> _reducers;

        // Variables that are written to the start of the Pikala stream for framing checks
        private const uint _header = ((byte)'P' << 0 | (byte)'K' << 8 | (byte)'L' << 16 | (byte)'A' << 24);
        private const uint _version = 1U;

        public Pickler(Func<Assembly, AssemblyPickleMode>? assemblyPickleMode = null)
        {
            // By default assume nothing needs to be pickled by value
            _assemblyPickleMode = assemblyPickleMode ?? (_ => AssemblyPickleMode.Default);
            _reducers = new Dictionary<Type, IReducer>();

            RegisterReducer(new DictionaryReducer());
        }

        public bool RegisterReducer(IReducer reducer)
        {
            return _reducers.TryAdd(reducer.Type, reducer);
        }

        private static FieldInfo[] GetSerializedFields(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return fields.Where(field => !field.IsLiteral && !field.IsNotSerialized).ToArray();
        }

        /// <summary>
        /// Return true if the type is either a value type, or a sealed reference type from mscorlib.
        /// </summary>
        /// <remarks>
        /// This is used when deciding if we need type tokens or not based on static context, if the static type
        /// is a value type or a sealed type then we know that the runtime type must be equal to that (because there
        /// are no subtypes). Contrast this with when the static type is a non-sealed reference type, the runtime type
        /// that we serialise (and then later need to deserialse) could be any subtype of it.
        ///
        /// This only applies to types within mscorlib because we don't expect them to change, but they're common enough
        /// that this gives a good saving of type tokens. Other types might be value type when we serialise them, but by the
        /// time we come to deserialise the user may of changed them to a reference type leading the deserialiser to think there
        /// should be a type token.
        /// </remarks>
        private bool IsStaticallyFinal(Type staticType)
        {
            return staticType.Assembly == mscorlib && (staticType.IsValueType || staticType.IsSealed);
        }
    }
}
