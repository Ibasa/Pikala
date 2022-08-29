using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace Ibasa.Pikala
{
    public interface IReducer
    {
        public Type Type { get; }
        public (MethodBase, object?, object?[]) Reduce(object obj);
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
        RuntimeAssembly = 20,
        RuntimeModule = 21,
        RuntimeTypeInfo = 22,
        RuntimeFieldInfo = 23,
        RuntimeRtFieldInfo = 24,
        RuntimeMdFieldInfo = 25,
        RuntimePropertyInfo = 26,
        RuntimeEventInfo = 27,
        RuntimeMethodInfo = 28,
        RuntimeConsturctorInfo = 29,
        DynamicMethod = 30,
        Assembly = 31,
        Module = 32,
        TypeInfo = 33,
        FieldInfo = 34,
        PropertyInfo = 35,
        EventInfo = 36,
        MethodInfo = 37,
        ConsturctorInfo = 38,
        MethodBase = 39,
        MemberInfo = 40,
        Delegate = 41,
        MulticastDelegate = 42,

        // Reflection
        TypeRef = 43,
        TypeDef = 44,
        ArrayType = 45,
        ByRefType = 46,
        PointerType = 47,
        GenericInstantiation = 48,
        GenericTypeParameter = 49,
        GenericMethodParameter = 50,
        TVar = 51,
        MVar = 52,

        // Common generics
        Nullable = 53,
        ValueTuple = 54,
        ValueTuple1 = 55,
        ValueTuple2 = 56,
        ValueTuple3 = 57,
        ValueTuple4 = 58,
        ValueTuple5 = 59,
        ValueTuple6 = 60,
        ValueTuple7 = 61,
        ValueTuple8 = 62,
        Tuple1 = 63,
        Tuple2 = 64,
        Tuple3 = 65,
        Tuple4 = 66,
        Tuple5 = 67,
        Tuple6 = 68,
        Tuple7 = 69,
        Tuple8 = 70,
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
        private static readonly Type runtimeRtFieldInfoType = mscorlib.GetType("System.Reflection.RtFieldInfo", true)!;
        private static readonly Type runtimeMdFieldInfoType = mscorlib.GetType("System.Reflection.MdFieldInfo", true)!;
        private static readonly Type runtimePropertyInfoType = mscorlib.GetType("System.Reflection.RuntimePropertyInfo", true)!;
        private static readonly Type runtimeMethodInfoType = mscorlib.GetType("System.Reflection.RuntimeMethodInfo", true)!;
        private static readonly Type runtimeConstructorInfoType = mscorlib.GetType("System.Reflection.RuntimeConstructorInfo", true)!;
        private static readonly Type runtimeEventInfoType = mscorlib.GetType("System.Reflection.RuntimeEventInfo", true)!;

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
                { typeof(Delegate), TypeOperation.Delegate },
                { typeof(MulticastDelegate), TypeOperation.MulticastDelegate },
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
                { runtimeTypeType, TypeOperation.RuntimeTypeInfo },
                { runtimeFieldInfoType, TypeOperation.RuntimeFieldInfo },
                { runtimeRtFieldInfoType, TypeOperation.RuntimeRtFieldInfo },
                { runtimeMdFieldInfoType, TypeOperation.RuntimeMdFieldInfo },
                { runtimeMethodInfoType, TypeOperation.RuntimeMethodInfo },
                { runtimeConstructorInfoType, TypeOperation.RuntimeConsturctorInfo },
                { runtimeEventInfoType, TypeOperation.RuntimeEventInfo },
                { runtimePropertyInfoType, TypeOperation.RuntimePropertyInfo },
                { runtimeModuleType, TypeOperation.RuntimeModule },
                { runtimeAssemblyType, TypeOperation.RuntimeAssembly },
                { typeof(DynamicMethod), TypeOperation.DynamicMethod },
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
        // TODO: These need to be parallel safe as one pickler can run multiple Serialise/Deserialise ops in parallel.
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

            // Some types we have pre-built methods for serialization            
            {
                var serializeObjectMethod = typeof(Pickler).GetMethod("Serialize_Object", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeObjectMethod != null, "Could not lookup Serialize_Object method");
                _serializationMethods.Add(typeof(object), serializeObjectMethod);

                var serializeDelegateMethod = typeof(Pickler).GetMethod("Serialize_Delegate", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeDelegateMethod != null, "Could not lookup Serialize_Delegate method");
                // Delegate and MulticastDelegate can both use Serialize_Delegate
                _serializationMethods.Add(typeof(Delegate), serializeDelegateMethod);
                _serializationMethods.Add(typeof(MulticastDelegate), serializeDelegateMethod);

                var serializeTypeMethod = typeof(Pickler).GetMethod("Serialize_Type", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeTypeMethod != null, "Could not lookup Serialize_Type method");
                // TypeBuilders/ModuleBuilder/AssemblyBuilder can use the same method as their non-builder variants.
                _serializationMethods.Add(runtimeTypeType, serializeTypeMethod);
                _serializationMethods.Add(typeof(TypeBuilder), serializeTypeMethod);

                var serializeModuleMethod = typeof(Pickler).GetMethod("Serialize_Module", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeModuleMethod != null, "Could not lookup Serialize_Module method");
                _serializationMethods.Add(runtimeModuleType, serializeModuleMethod);
                _serializationMethods.Add(typeof(ModuleBuilder), serializeModuleMethod);

                var serializeAssemblyMethod = typeof(Pickler).GetMethod("Serialize_Assembly", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeAssemblyMethod != null, "Could not lookup Serialize_Assembly method");
                _serializationMethods.Add(runtimeAssemblyType, serializeAssemblyMethod);
                _serializationMethods.Add(typeof(AssemblyBuilder), serializeAssemblyMethod);

                var serializeMethodInfoMethod = typeof(Pickler).GetMethod("Serialize_MethodInfo", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeMethodInfoMethod != null, "Could not lookup Serialize_MethodInfo method");
                _serializationMethods.Add(runtimeMethodInfoType, serializeMethodInfoMethod);
                
                var serializeDynamicMethodMethod = typeof(Pickler).GetMethod("Serialize_DynamicMethod", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeDynamicMethodMethod != null, "Could not lookup Serialize_DynamicMethod method");                
                _serializationMethods.Add(typeof(DynamicMethod), serializeDynamicMethodMethod);

                var serializeConstructorInfoMethod = typeof(Pickler).GetMethod("Serialize_ConstructorInfo", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeConstructorInfoMethod != null, "Could not lookup Serialize_ConstructorInfo method");
                _serializationMethods.Add(runtimeConstructorInfoType, serializeConstructorInfoMethod);

                var serializeFieldInfoMethod = typeof(Pickler).GetMethod("Serialize_FieldInfo", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeFieldInfoMethod != null, "Could not lookup Serialize_FieldInfo method");
                _serializationMethods.Add(runtimeRtFieldInfoType, serializeFieldInfoMethod);
                _serializationMethods.Add(runtimeMdFieldInfoType, serializeFieldInfoMethod);

                var serializePropertyInfoMethod = typeof(Pickler).GetMethod("Serialize_PropertyInfo", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializePropertyInfoMethod != null, "Could not lookup Serialize_PropertyInfo method");
                _serializationMethods.Add(runtimePropertyInfoType, serializePropertyInfoMethod);

                var serializeEventInfoMethod = typeof(Pickler).GetMethod("Serialize_EventInfo", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializeEventInfoMethod != null, "Could not lookup Serialize_EventInfo method");
                _serializationMethods.Add(runtimeEventInfoType, serializeEventInfoMethod);

                var serializePicklerMethod = typeof(Pickler).GetMethod("Serialize_Pickler", BindingFlags.NonPublic | BindingFlags.Static);
                System.Diagnostics.Debug.Assert(serializePicklerMethod != null, "Could not lookup Serialize_Pickler method");
                _serializationMethods.Add(typeof(Pickler), serializePicklerMethod);                
            }
        }

        public bool RegisterReducer(IReducer reducer)
        {
            return _reducers.TryAdd(reducer.Type, reducer);
        }

        private static FieldInfo[] GetSerializedFields(Type type)
        {
            var allFields = new List<FieldInfo>();

            // We need to go through the whole inheritance chain to get fields because GetFields will _never_ return private fields of bases classes but we need those.
            Type? t = type;
            while (t != null)
            {
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                // Sort the fields by name so we serialise in deterministic order
                foreach(var field in fields.OrderBy(field => field.Name))
                {
                    // Skip literal fields
                    if (field.IsLiteral) continue;

                    allFields.Add(field);
                }
                t = t.BaseType;
            }
            return allFields.ToArray();
        }

        private static bool IsTupleType(Type type)
        {
            // Need to check for ElementType so arrays and pointers don't get caught by this
            return !type.HasElementType && type.Assembly == mscorlib && type.Namespace == "System" && (
                type.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || type.Name.StartsWith("Tuple", StringComparison.Ordinal));
        }

        private static bool IsArrayType(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? elementType)
        {
            // Need to check for ElementType so arrays and pointers don't get caught by this
            var isArray = type.IsArray;
            if (isArray)
            {
                elementType = type.GetElementType();
                System.Diagnostics.Debug.Assert(elementType != null, "GetElementType was null for an array type");
                return true;
            }
            elementType = null;
            return false;
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
            if (type == typeof(Pickler)) return true;

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

        private SerialisedObjectTypeInfo GetCachedTypeInfo(Type type)
        {
            if (!_typeInfo.TryGetValue(type, out var maybeInfo))
            {
                maybeInfo = new SerialisedObjectTypeInfo(type);
                _typeInfo.Add(type, maybeInfo);
                BuildSerialisedObjectTypeInfo(maybeInfo, GetCachedTypeInfo);
            }
            return maybeInfo;
        }

        private void BuildSerialisedObjectTypeInfo(SerialisedObjectTypeInfo info, Func<Type, SerialisedObjectTypeInfo> recurse)
        {
            var type = info.Type;

            info.Flags =
                (type.IsValueType ? PickledTypeFlags.IsValueType : 0) |
                (type.IsSealed ? PickledTypeFlags.IsSealed : 0) |
                (type.IsAbstract ? PickledTypeFlags.IsAbstract : 0) |
                (type.HasElementType ? PickledTypeFlags.HasElementType : 0);

            // Assume builtin, we'll type check and change that below.
            info.Mode = PickledTypeMode.IsBuiltin;

            if (!type.IsAbstract)
            {
                // Work out what sort of operation this type needs
                if (type.IsPointer || type == typeof(Pointer))
                {
                    info.Error = $"Pointer types are not serializable: '{type}'";
                }

                else if (IsArrayType(type, out var arrayElement))
                {
                    info.IsArray = true;
                    info.Element = recurse(arrayElement);
                }

                else if (IsNullableType(type, out var nullableElement))
                {
                    info.IsNullable = true;
                    info.Element = recurse(nullableElement);
                }

                // Tuples!
                else if (IsTupleType(type))
                {
                    info.TupleArguments = type.GetGenericArguments().Select(recurse).ToArray();
                }

                // Reflection 
                else if (type.IsAssignableTo(typeof(Assembly)))
                {
                    if (!type.IsAssignableTo(runtimeAssemblyType) && type != typeof(AssemblyBuilder))
                    {
                        info.Error = $"Type '{type}' is not automaticly serializable as it inherits from Assembly.";
                    }
                }
                else if (type.IsAssignableTo(typeof(Module)))
                {
                    if (!type.IsAssignableTo(runtimeModuleType) && type != typeof(ModuleBuilder))
                    {
                        info.Error = $"Type '{type}' is not automaticly serializable as it inherits from Module.";
                    }
                }
                else if (type.IsAssignableTo(typeof(MemberInfo)))
                {
                    if (type.IsAssignableTo(typeof(Type)))
                    {
                        if (!type.IsAssignableTo(runtimeTypeType) && type != typeof(TypeBuilder))
                        {
                            info.Error = $"Type '{type}' is not automaticly serializable as it inherits from Type.";
                        }
                    }
                    else if (type.IsAssignableTo(typeof(FieldInfo)))
                    {
                        if (!type.IsAssignableTo(runtimeFieldInfoType) && type != typeof(FieldBuilder))
                        {
                            info.Error = $"Type '{type}' is not automaticly serializable as it inherits from FieldInfo.";
                        }
                    }
                    else if (type.IsAssignableTo(typeof(PropertyInfo)))
                    {
                        if (!type.IsAssignableTo(runtimePropertyInfoType) && type != typeof(PropertyBuilder))
                        {
                            info.Error = $"Type '{type}' is not automaticly serializable as it inherits from PropertyInfo.";
                        }
                    }
                    else if (type.IsAssignableTo(typeof(EventInfo)))
                    {
                        if (!type.IsAssignableTo(runtimeEventInfoType) && type != typeof(EventBuilder))
                        {
                            info.Error = $"Type '{type}' is not automaticly serializable as it inherits from EventInfo.";
                        }
                    }
                    else if (type.IsAssignableTo(typeof(MethodBase)))
                    {
                        if (type.IsAssignableTo(typeof(ConstructorInfo)))
                        {
                            if (!type.IsAssignableTo(runtimeConstructorInfoType) && type != typeof(ConstructorBuilder))
                            {
                                info.Error = $"Type '{type}' is not automaticly serializable as it inherits from ConstructorInfo.";
                            }
                        }
                        else if (type.IsAssignableTo(typeof(MethodInfo)))
                        {
                            if (!type.IsAssignableTo(runtimeMethodInfoType) && type != typeof(MethodBuilder) && type != typeof(DynamicMethod))
                            {
                                info.Error = $"Type '{type}' is not automaticly serializable as it inherits from MethodInfo.";
                            }
                        }
                        else if (type != typeof(MethodBase))
                        {
                            info.Error = $"Type '{type}' is not automaticly serializable as it inherits from MethodBase.";
                        }
                    }
                    else if (type != typeof(MemberInfo))
                    {
                        info.Error = $"Type '{type}' is not automaticly serializable as it inherits from MemberInfo.";
                    }
                }

                else if (IsBuiltinType(type))
                {
                    // Builtins such as int
                }

                else if (type.IsEnum)
                {
                    info.Mode = PickledTypeMode.IsEnum;
                    info.TypeCode = Type.GetTypeCode(type);
                }

                else if (type.IsAssignableTo(typeof(Delegate)))
                {
                    info.Mode = PickledTypeMode.IsDelegate;
                }

                else if (_reducers.TryGetValue(type, out var reducer) || (type.IsGenericType && _reducers.TryGetValue(type.GetGenericTypeDefinition(), out reducer)))
                {
                    info.Reducer = reducer;
                    info.Mode = PickledTypeMode.IsReduced;
                }

                else if (type.IsAssignableTo(typeof(MarshalByRefObject)))
                {
                    info.Error = $"Type '{type}' is not automaticly serializable as it inherits from MarshalByRefObject.";
                }

                else
                {
                    var fields = GetSerializedFields(type);
                    info.Mode = PickledTypeMode.IsAutoSerialisedObject;

                    info.SerialisedFields = new (SerialisedObjectTypeInfo, FieldInfo)[fields.Length];
                    for (int i = 0; i < fields.Length; ++i)
                    {
                        info.SerialisedFields[i] = (recurse(fields[i].FieldType), fields[i]);
                    }
                }
            }
        }
    }
}
