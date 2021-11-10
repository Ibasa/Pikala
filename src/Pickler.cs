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
        String,
        Enum,
        Array,

        // Memoised
        Memo,

        // Reflection
        AssemblyRef,
        ModuleRef,
        TypeRef,
        FieldRef,
        PropertyRef,
        MethodRef,
        ConstructorRef,
        AssemblyDef,
        ModuleDef,
        TypeDef,
        GenericInstantiation,
        GenericParameter,
        TVar,
        MVar,

        // Structs and classes
        Delegate,
        Reducer,
        ISerializable,
        Object,
    }

    enum TypeDef
    {
        Enum,
        Delegate,
        Struct,
        Class,
    }

    public sealed partial class Pickler
    {
        private static OpCode[] _oneByteOpCodes;
        private static OpCode[] _twoByteOpCodes;

        static Pickler()
        {
            // For converting raw IL bytes back into serialisavble instructions
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

        private Func<Assembly, bool> _pickleByValuePredicate;
        private Dictionary<Type, IReducer> _reducers;

        // Variables that are written to the start of the Pikala stream for framing checks
        private const uint _header = ((byte)'P' << 24 | (byte)'K' << 16 | (byte)'L' << 8 | (byte)'A');
        private const uint _version = 1U;

        public Pickler(Func<Assembly, bool>? pickleByValuePredicate = null)
        {
            // By default assume nothing needs to be pickled by value
            _pickleByValuePredicate = pickleByValuePredicate ?? (_ => false);
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
