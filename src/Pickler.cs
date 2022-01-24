using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace Ibasa.Pikala
{
    public interface IReducer
    {
        public Type Type { get; }
        public (MethodBase, object?, object?[]) Reduce(Type type, object obj);
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
        MscorlibAssembly = 24,
        AssemblyRef = 25,
        ManifestModuleRef = 26,
        ModuleRef = 27,
        TypeRef = 28,
        FieldRef = 29,
        PropertyRef = 30,
        EventRef = 31,
        MethodRef = 32,
        ConstructorRef = 33,
        AssemblyDef = 34,
        ModuleDef = 35,
        TypeDef = 36,
        ArrayType = 37,
        GenericInstantiation = 38,
        GenericTypeParameter = 39,
        GenericMethodParameter = 40,
        TVar = 41,
        MVar = 42,

        // Structs and classes
        Delegate = 43,
        Reducer = 44,
        ISerializable = 45,
        Object = 46,

        // Basic Types
        MscorlibModule,
        TypeVoid,
        TypeBoolean,
        TypeByte,
        TypeSByte,
        TypeInt16,
        TypeUInt16,
        TypeInt32,
        TypeUInt32,
        TypeInt64,
        TypeUInt64,
        TypeIntPtr,
        TypeUIntPtr,
        TypeChar,
        TypeDouble,
        TypeSingle,
        TypeDecimal,
        TypeString,
        TypeAssembly,
        TypeModule,
        TypeTypeInfo,
        TypeFieldInfo,
        TypeMethodInfo,
        TypePropertyInfo,
        TypeConsturctorInfo,
        TypeEventInfo,
        TypeMemberInfo,

        // This is written as a byte so we're limited to 255 operations
    }

    enum TypeDef : byte
    {
        Enum = 0,
        Delegate = 1,
        Struct = 2,
        Class = 3,
        Nested = 4,
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

    /// <summary>
    /// Sometimes our operation cache doesn't know the exact operation to do but we do know a rough grouping.
    /// E.g. The Type "Assembly" is always an AssemblyRef or Def or Mscorlib but we need to look at the value itself to work that out while our cache is by type.
    /// </summary>
    enum OperationGroup
    {
        FullyKnown,
        Assembly,
        Module,
        Type,
        NonSerializable,
    }

    sealed class OperationCacheEntry
    {
        public readonly OperationGroup Group;
        public readonly TypeCode TypeCode;
        public readonly PickleOperation? Operation;
        public readonly IReducer? Reducer;
        public readonly Tuple<ValueTuple<string, Type>[], FieldInfo[]>? Fields;
        public readonly Type[]? GenericArguments;
        public readonly string? ErrorMessage;

        public OperationCacheEntry(string errorMessage)
        {
            TypeCode = TypeCode.Object;
            Group = OperationGroup.NonSerializable;
            Operation = null;
            Reducer = null;
            Fields = null;
            GenericArguments = null;
            ErrorMessage = errorMessage;
        }

        public OperationCacheEntry(TypeCode typeCode, OperationGroup group)
        {
            System.Diagnostics.Debug.Assert(group != OperationGroup.FullyKnown, "Passed FullyKnown into OperationCacheEntry without operation");
            TypeCode = typeCode;
            Group = group;
            Operation = null;
            Reducer = null;
            Fields = null;
            GenericArguments = null;
        }

        public OperationCacheEntry(TypeCode typeCode, PickleOperation operation)
        {
            TypeCode = typeCode;
            Group = OperationGroup.FullyKnown;
            Operation = operation;
            Reducer = null;
            Fields = null;
            GenericArguments = null;
        }

        public OperationCacheEntry(TypeCode typeCode, bool isValueTuple, Type[]? genericArguments)
        {
            TypeCode = typeCode;
            Group = OperationGroup.FullyKnown;
            Operation = isValueTuple ? PickleOperation.ValueTuple : PickleOperation.Tuple;
            Reducer = null;
            Fields = null;
            GenericArguments = genericArguments;
        }


        public OperationCacheEntry(TypeCode typeCode, IReducer reducer)
        {
            TypeCode = typeCode;
            Group = OperationGroup.FullyKnown;
            Operation = PickleOperation.Reducer;
            Reducer = reducer;
            Fields = null;
            GenericArguments = null;
        }

        public OperationCacheEntry(TypeCode typeCode, FieldInfo[] fields)
        {
            TypeCode = typeCode;
            Group = OperationGroup.FullyKnown;
            Operation = PickleOperation.Object;
            Reducer = null;
            GenericArguments = null;

            var fieldNamesAndTypes = new ValueTuple<string, Type>[fields.Length];
            for (int i = 0; i < fields.Length; ++i)
            {
                fieldNamesAndTypes[i] = ValueTuple.Create(fields[i].Name, fields[i].FieldType);
            }
            Fields = Tuple.Create(fieldNamesAndTypes, fields);
        }
    }

    public sealed partial class Pickler
    {
        private static readonly Assembly mscorlib = typeof(int).Assembly;
        private static readonly Type runtimeTypeType = mscorlib.GetType("System.RuntimeType", true)!;
        private static readonly Type runtimeModuleType = mscorlib.GetType("System.Reflection.RuntimeModule", true)!;
        private static readonly Type runtimeAssemblyType = mscorlib.GetType("System.Reflection.RuntimeAssembly", true)!;
        private static readonly Type runtimeFieldInfoType = mscorlib.GetType("System.Reflection.RuntimeFieldInfo", true)!;
        private static readonly Type runtimePropertyInfoType = mscorlib.GetType("System.Reflection.RuntimePropertyInfo", true)!;
        private static readonly Type runtimeMethodInfoType = mscorlib.GetType("System.Reflection.RuntimeMethodInfo", true)!;
        private static readonly Type runtimeConstructorInfoType = mscorlib.GetType("System.Reflection.RuntimeConstructorInfo", true)!;
        private static readonly Type runtimeEventInfoType = mscorlib.GetType("System.Reflection.RuntimeEventInfo", true)!;
        private static readonly Type runtimeModuleBuilderType = typeof(System.Reflection.Emit.ModuleBuilder);
        private static readonly Type runtimeAssemblyBuilderType = typeof(System.Reflection.Emit.AssemblyBuilder);

        private static OpCode[] _oneByteOpCodes;
        private static OpCode[] _twoByteOpCodes;
        private static Dictionary<Type, PickleOperation> wellKnownTypes;

        static Pickler()
        {
            // For converting raw IL bytes back into serialisable instructions
            _oneByteOpCodes = new OpCode[0xe1];
            _twoByteOpCodes = new OpCode[0xef];
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var opCodeObj = field.GetValue(null);
                System.Diagnostics.Debug.Assert(opCodeObj != null, "GetValue for OpCode field returned null");
                var opCode = (OpCode)opCodeObj;

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

            wellKnownTypes = new Dictionary<Type, PickleOperation>()
            {
                { typeof(void), PickleOperation.TypeVoid },
                { typeof(bool), PickleOperation.TypeBoolean },
                { typeof(char), PickleOperation.TypeChar },
                { typeof(sbyte), PickleOperation.TypeSByte },
                { typeof(short), PickleOperation.TypeInt16 },
                { typeof(int), PickleOperation.TypeInt32 },
                { typeof(long), PickleOperation.TypeInt64 },
                { typeof(byte), PickleOperation.TypeByte },
                { typeof(ushort), PickleOperation.TypeUInt16 },
                { typeof(uint), PickleOperation.TypeUInt32 },
                { typeof(ulong), PickleOperation.TypeUInt64 },
                { typeof(float), PickleOperation.TypeSingle },
                { typeof(double), PickleOperation.TypeDouble },
                { typeof(decimal), PickleOperation.TypeDecimal },
                { typeof(string), PickleOperation.TypeString },
                { typeof(UIntPtr), PickleOperation.TypeUIntPtr },
                { typeof(IntPtr), PickleOperation.TypeIntPtr },
                { typeof(Type), PickleOperation.TypeTypeInfo },
                { typeof(FieldInfo), PickleOperation.TypeFieldInfo },
                { typeof(MethodInfo), PickleOperation.TypeMethodInfo },
                { typeof(ConstructorInfo), PickleOperation.TypeConsturctorInfo },
                { typeof(EventInfo), PickleOperation.TypeEventInfo },
                { typeof(PropertyInfo), PickleOperation.TypePropertyInfo },
                { typeof(MemberInfo), PickleOperation.TypeMemberInfo },
                { typeof(Module), PickleOperation.TypeModule },
                { typeof(Assembly), PickleOperation.TypeAssembly },
            };
        }

        private Func<Assembly, AssemblyPickleMode> _assemblyPickleMode;
        private Dictionary<Type, IReducer> _reducers;
        // This is keyed by the static type of the object we're serialising or deserialising
        private Dictionary<Type, PickleOperation?> _inferCache;
        // This is keyed by the runtime type of the object we're serialising
        private Dictionary<Type, OperationCacheEntry> _operationCache;

        // Variables that are written to the start of the Pikala stream for framing checks
        private const uint _header = ((byte)'P' << 0 | (byte)'K' << 8 | (byte)'L' << 16 | (byte)'A' << 24);
        private const uint _version = 1U;

        public AssemblyLoadContext AssemblyLoadContext { get; private set; }

        public Pickler(Func<Assembly, AssemblyPickleMode>? assemblyPickleMode = null, AssemblyLoadContext? assemblyLoadContext = null)
        {
            // By default assume nothing needs to be pickled by value
            AssemblyLoadContext = (assemblyLoadContext ?? AssemblyLoadContext.CurrentContextualReflectionContext) ?? AssemblyLoadContext.Default;
            _assemblyPickleMode = assemblyPickleMode ?? (_ => AssemblyPickleMode.Default);
            _reducers = new Dictionary<Type, IReducer>();
            _inferCache = new Dictionary<Type, PickleOperation?>();
            _operationCache = new Dictionary<Type, OperationCacheEntry>();

            RegisterReducer(new DictionaryReducer());
        }

        private PickleOperation? InferOperationFromStaticType(SerialisedObjectTypeInfo? info, Type staticType)
        {
            PickleOperation? Infer(SerialisedObjectTypeInfo? info, Type staticType)
            {
                if ((info != null && info.Flags.HasFlag(PickledTypeFlags.IsValueType)) || (info == null && staticType.IsValueType))
                {
                    // This is a static value type, we probably didn't write an operation out for this

                    if (staticType == typeof(bool))
                    {
                        return PickleOperation.Boolean;
                    }
                    else if (staticType == typeof(char))
                    {
                        return PickleOperation.Char;
                    }
                    else if (staticType == typeof(sbyte))
                    {
                        return PickleOperation.SByte;
                    }
                    else if (staticType == typeof(short))
                    {
                        return PickleOperation.Int16;
                    }
                    else if (staticType == typeof(int))
                    {
                        return PickleOperation.Int32;
                    }
                    else if (staticType == typeof(long))
                    {
                        return PickleOperation.Int64;
                    }
                    else if (staticType == typeof(byte))
                    {
                        return PickleOperation.Byte;
                    }
                    else if (staticType == typeof(ushort))
                    {
                        return PickleOperation.UInt16;
                    }
                    else if (staticType == typeof(uint))
                    {
                        return PickleOperation.UInt32;
                    }
                    else if (staticType == typeof(ulong))
                    {
                        return PickleOperation.UInt64;
                    }
                    else if (staticType == typeof(float))
                    {
                        return PickleOperation.Single;
                    }
                    else if (staticType == typeof(double))
                    {
                        return PickleOperation.Double;
                    }
                    else if (staticType == typeof(decimal))
                    {
                        return PickleOperation.Decimal;
                    }
                    else if (staticType == typeof(IntPtr))
                    {
                        return PickleOperation.IntPtr;
                    }
                    else if (staticType == typeof(UIntPtr))
                    {
                        return PickleOperation.UIntPtr;
                    }
                    else if (IsTupleType(staticType))
                    {
                        return PickleOperation.ValueTuple;
                    }
                    else if ((info != null && info.Flags.HasFlag(PickledTypeFlags.IsEnum)) || (info == null && staticType.IsEnum))
                    {
                        return PickleOperation.Enum;
                    }
                }

                return null;
            }

            if (!_inferCache.TryGetValue(staticType, out var operation))
            {
                operation = Infer(info, staticType);
                _inferCache.Add(staticType, operation);
            }
            return operation;
        }

        public bool RegisterReducer(IReducer reducer)
        {
            return _reducers.TryAdd(reducer.Type, reducer);
        }

        private static FieldInfo[] GetSerializedFields(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // Sort the fields by name so we serialise in deterministic order
            return fields.Where(field => !field.IsLiteral && !field.IsNotSerialized).OrderBy(field => field.Name).ToArray();
        }

        private static bool IsTupleType(Type type)
        {
            return !type.HasElementType && type.Assembly == mscorlib && type.Namespace == "System" && (
                type.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || type.Name.StartsWith("Tuple", StringComparison.Ordinal));
        }

        private static bool IsNullableType(Type type)
        {
            return !type.HasElementType && type.Assembly == mscorlib && type.Namespace == "System" && type.Name == "Nullable`1";
        }
    }
}
