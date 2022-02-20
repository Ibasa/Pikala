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

    enum ObjectOperation : byte
    {
        Null = 0,
        Memo = 1,
        Object = 2,
    }

    enum AssemblyOperation : byte
    {
        Memo = 0,
        MscorlibAssembly = 1,
        AssemblyRef = 2,
        AssemblyDef = 3,
    }

    enum ModuleOperation : byte
    {
        Memo = 0,
        MscorlibModule = 1,
        ManifestModuleRef = 2,
        ModuleRef = 3,
        ModuleDef = 4,
    }
    enum TypeOperation : byte
    {
        Memo = 0,

        // Primitives
        Void = 1,
        Boolean = 2,
        Char = 3,
        Byte = 4,
        UInt16 = 5,
        UInt32 = 6,
        UInt64 = 7,
        SByte = 8,
        Int16 = 9,
        Int32 = 10,
        Int64 = 11,
        Single = 12,
        Double = 13,
        Decimal = 14,
        UIntPtr = 15,
        IntPtr = 16,
        DBNull = 17,

        // Basic types
        Object = 18,
        String = 19,
        Assembly = 20,
        Module = 21,
        TypeInfo = 22,
        FieldInfo = 23,
        PropertyInfo = 24,
        EventInfo = 25,
        MethodInfo = 26,
        ConsturctorInfo = 27,
        MethodBase = 28,
        MemberInfo = 29,

        // Reflection
        TypeRef = 30,
        TypeDef = 31,
        ArrayType = 32,
        GenericInstantiation = 33,
        GenericTypeParameter = 34,
        GenericMethodParameter = 35,
        TVar = 36,
        MVar = 37,

        // Common generics
        Nullable = 38,
        ValueTuple = 39,
        ValueTuple1 = 40,
        ValueTuple2 = 41,
        ValueTuple3 = 42,
        ValueTuple4 = 43,
        ValueTuple5 = 44,
        ValueTuple6 = 45,
        ValueTuple7 = 46,
        ValueTuple8 = 47,
        Tuple1 = 48,
        Tuple2 = 49,
        Tuple3 = 50,
        Tuple4 = 51,
        Tuple5 = 52,
        Tuple6 = 53,
        Tuple7 = 54,
        Tuple8 = 55,
    }

    enum TypeDef : byte
    {
        Enum = 0,
        Delegate = 1,
        Struct = 2,
        Class = 3,
        Interface = 4,
        Nested = 8,
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
        private static readonly Assembly pikala = typeof(Pickler).Assembly;

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
        private static Dictionary<Type, TypeOperation> _wellKnownTypes;

        private static Version _pikalaVersion;

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

            _wellKnownTypes = new Dictionary<Type, TypeOperation>()
            {
                { typeof(object), TypeOperation.Object },
                { typeof(void), TypeOperation.Void },
                { typeof(bool), TypeOperation.Boolean },
                { typeof(char), TypeOperation.Char },
                { typeof(sbyte), TypeOperation.SByte },
                { typeof(short), TypeOperation.Int16 },
                { typeof(int), TypeOperation.Int32 },
                { typeof(long), TypeOperation.Int64 },
                { typeof(byte), TypeOperation.Byte },
                { typeof(ushort), TypeOperation.UInt16 },
                { typeof(uint), TypeOperation.UInt32 },
                { typeof(ulong), TypeOperation.UInt64 },
                { typeof(float), TypeOperation.Single },
                { typeof(double), TypeOperation.Double },
                { typeof(decimal), TypeOperation.Decimal },
                { typeof(string), TypeOperation.String },
                { typeof(UIntPtr), TypeOperation.UIntPtr },
                { typeof(IntPtr), TypeOperation.IntPtr },
                { typeof(Type), TypeOperation.TypeInfo },
                { typeof(FieldInfo), TypeOperation.FieldInfo },
                { typeof(MethodInfo), TypeOperation.MethodInfo },
                { typeof(ConstructorInfo), TypeOperation.ConsturctorInfo },
                { typeof(MethodBase), TypeOperation.MethodBase },
                { typeof(EventInfo), TypeOperation.EventInfo },
                { typeof(PropertyInfo), TypeOperation.PropertyInfo },
                { typeof(MemberInfo), TypeOperation.MemberInfo },
                { typeof(Module), TypeOperation.Module },
                { typeof(Assembly), TypeOperation.Assembly },
                { typeof(Nullable<>), TypeOperation.Nullable },
                { typeof(ValueTuple), TypeOperation.ValueTuple },
                { typeof(ValueTuple<>), TypeOperation.ValueTuple1 },
                { typeof(ValueTuple<,>), TypeOperation.ValueTuple2 },
                { typeof(ValueTuple<,,>), TypeOperation.ValueTuple3 },
                { typeof(ValueTuple<,,,>), TypeOperation.ValueTuple4 },
                { typeof(ValueTuple<,,,,>), TypeOperation.ValueTuple5 },
                { typeof(ValueTuple<,,,,,>), TypeOperation.ValueTuple6 },
                { typeof(ValueTuple<,,,,,,>), TypeOperation.ValueTuple7 },
                { typeof(ValueTuple<,,,,,,,>), TypeOperation.ValueTuple8 },
                { typeof(Tuple<>), TypeOperation.Tuple1 },
                { typeof(Tuple<,>), TypeOperation.Tuple2 },
                { typeof(Tuple<,,>), TypeOperation.Tuple3 },
                { typeof(Tuple<,,,>), TypeOperation.Tuple4 },
                { typeof(Tuple<,,,,>), TypeOperation.Tuple5 },
                { typeof(Tuple<,,,,,>), TypeOperation.Tuple6 },
                { typeof(Tuple<,,,,,,>), TypeOperation.Tuple7 },
                { typeof(Tuple<,,,,,,,>), TypeOperation.Tuple8 },
            };

            var version = pikala.GetName().Version;
            System.Diagnostics.Debug.Assert(version != null, "Pikala assembly version was null");
            _pikalaVersion = version;
        }

        private readonly Func<Assembly, AssemblyPickleMode> _assemblyPickleMode;
        private readonly Dictionary<Type, IReducer> _reducers;
        // This is only accessed during serialisation because we know type flags and mode can't change across one serialiser (TODO Correct only once reducers are passed in as an option, not a mutable value)
        // but flags might differ across various binary streams for deserialisation (e.g. you serialise once with X being reduced, next with it defaulting to auto object)
        private readonly Dictionary<Type, SerialisedObjectTypeInfo> _typeInfo;

        // Variables that are written to the start of the Pikala stream for framing checks
        private const uint _header = ((byte)'P' << 0 | (byte)'K' << 8 | (byte)'L' << 16 | (byte)'A' << 24);

        public AssemblyLoadContext AssemblyLoadContext { get; private set; }

        public Pickler(Func<Assembly, AssemblyPickleMode>? assemblyPickleMode = null, AssemblyLoadContext? assemblyLoadContext = null)
        {
            // By default assume nothing needs to be pickled by value
            AssemblyLoadContext = (assemblyLoadContext ?? AssemblyLoadContext.CurrentContextualReflectionContext) ?? AssemblyLoadContext.Default;
            _assemblyPickleMode = assemblyPickleMode ?? (_ => AssemblyPickleMode.Default);
            _reducers = new Dictionary<Type, IReducer>();
            _typeInfo = new Dictionary<Type, SerialisedObjectTypeInfo>();

            RegisterReducer(new Reducers.DictionaryReducer());
            RegisterReducer(new Reducers.ListReducer());
        }

        public bool RegisterReducer(IReducer reducer)
        {
            return _reducers.TryAdd(reducer.Type, reducer);
        }

        private static FieldInfo[] GetSerializedFields(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // Sort the fields by name so we serialise in deterministic order
            return fields.Where(field => !field.IsLiteral).OrderBy(field => field.Name).ToArray();
        }

        private static bool IsTupleType(Type type)
        {
            // Need to check for ElementType so arrays and pointers don't get caught by this
            return !type.HasElementType && type.Assembly == mscorlib && type.Namespace == "System" && (
                type.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || type.Name.StartsWith("Tuple", StringComparison.Ordinal));
        }

        private static bool IsNullableType(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? elementType)
        {
            // Need to check for ElementType so arrays and pointers don't get caught by this
            var isNullable = !type.HasElementType && type.Assembly == mscorlib && type.Namespace == "System" && type.Name == "Nullable`1";
            if (isNullable)
            {
                var genericArguments = type.GetGenericArguments();
                elementType = genericArguments[0];
                return true;
            }
            elementType = null;
            return false;
        }

        /// <summary>
        /// Returns true if this is a builtin type. That is one that pickler has specific code for handling.
        /// It will never make use of a reducer or other user code.
        /// </summary>
        private static bool IsBuiltinType(Type type)
        {
            System.Diagnostics.Debug.Assert(!type.IsGenericTypeDefinition, "Only expect closed types to be passed to IsBuiltinType", "Got type {0}", type);

            if (type.IsArray) return true;
            if (_wellKnownTypes.ContainsKey(type)) return true;
            if (IsNullableType(type, out var _)) return true;
            if (IsTupleType(type)) return true;
            return false;
        }

        /// <summary>
        /// This returns the root element type of a given type.
        /// E.g. GetRootElementType(int[][]) returns `int`, while GetElementType would return `int[]`
        /// </summary>
        private static SerialisedObjectTypeInfo GetRootElementType(SerialisedObjectTypeInfo info)
        {
            while (info.Element != null)
            {
                info = info.Element;
            }
            return info;
        }
    }
}
