﻿using System;
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
        Tuple = 21,
        ValueTuple = 22,

        // Memoised
        Memo = 23,

        // Reflection
        Mscorlib = 24,
        AssemblyRef = 25,
        ManifestModuleRef = 26,
        ModuleRef = 27,
        TypeRef = 28,
        FieldRef = 29,
        PropertyRef = 30,
        MethodRef = 31,
        ConstructorRef = 32,
        AssemblyDef = 33,
        ModuleDef = 34,
        TypeDef = 35,
        GenericInstantiation = 36,
        GenericParameter = 37,
        TVar = 38,
        MVar = 39,

        // Structs and classes
        Delegate = 40,
        Reducer = 41,
        ISerializable = 42,
        Object = 43,

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
    }
}
