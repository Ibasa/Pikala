# Pikala for dotnet

[![.NET Core](https://github.com/Ibasa/Pikala/actions/workflows/dotnetcore.yml/badge.svg?branch=main)](https://github.com/Ibasa/Pikala/actions/workflows/dotnetcore.yml) [![NuGet latest release](https://img.shields.io/nuget/v/Ibasa.Pikala.svg)](https://www.nuget.org/packages/Ibasa.Pikala)

Ibasa.Pikala is a .NET library for for pickling object graphs, it's designed to solve the same problems as [python cloudpickle](https://github.com/cloudpipe/cloudpickle) but for dotnet.

Pikala can also serialise type definitions and reconstruct them into dynamic assemblies on the other side. This is especially useful for cluster and cloud computing, see [Introduction to Pikala](https://blog.ibasa.uk/programming/dotnet/pikala/2021/03/01/pikala.html).

Pikal is only supported to send objects between the exact same version.

Using Pikala for **long-term object storage is not supported and strongly discouraged**.

**Security notice**: one should **only load pikala data from trusted sources** as otherwise Deserialize can lead to arbitrary code execution resulting in a critical security vulnerability.

## Installation

You can add this library to your project using [NuGet](http://www.nuget.org/).

[![NuGet latest release](https://img.shields.io/nuget/v/Ibasa.Pikala.svg)](https://www.nuget.org/packages/Ibasa.Pikala)

## Usage

Serialize a `Tuple<int, string>`:

```csharp
using Ibasa.Pikala

var obj = Tuple.Create(123, "hello world");

var memoryStream = new MemoryStream();
var pickler = new Pickler();
pickler.Serialize(memoryStream, obj);
```

Deserialize a `Func<int, int>`:

```csharp
using Ibasa.Pikala

// This is a snapshot of serializing `(Func<int, int>)Math.Abs`
var memoryStream = new MemoryStream(Convert.FromBase64String("UEtMQQEAAAAmIhoYFg1TeXN0ZW0uRnVuY2AyAhoYFgxTeXN0ZW0uSW50MzIVHAAAAAAAAAABAB0RQWJzKFN5c3RlbS5JbnQzMikAGhgWC1N5c3RlbS5NYXRo"));

var pickler = new Pickler();
var function = pickler.Deserialize(memoryStream) as Func<int, int>;

var result = function(-123);
Assert.Equal(123, result);
```

Tell Pikala to serialize type definitions not references for the current assembly:
```csharp
using Ibasa.Pikala
var pickler = new Pickler(assembly => assembly == System.Reflection.Assembly.GetExecutingAssembly() ? AssemblyPickleMode.PickleByValue : AssemblyPickleMode.Default);
```

Customize how Pikala serializes a type:
```csharp
public sealed class DictionaryReducer : IReducer
{
    readonly Type _dictionary;

    public DictionaryReducer()
    {
        _dictionary = typeof(Dictionary<,>);
    }

    public Type Type => _dictionary;

    public (MethodBase, object, object[]) Reduce(Type type, object obj)
    {
        var getEnumerator = type.GetMethod("GetEnumerator");
        var getCount = type.GetProperty("Count").GetGetMethod();

        var enumerator = getEnumerator.Invoke(obj, null);
        var count = (int)getCount.Invoke(obj, null);

        var genericParameters = enumerator.GetType().GetGenericArguments();
        var keyValuePairType = typeof(KeyValuePair<,>).MakeGenericType(genericParameters);

        var items = Array.CreateInstance(keyValuePairType, count);

        var enumeratorType = enumerator.GetType();
        var getCurrent = enumeratorType.GetProperty("Current").GetGetMethod();
        var moveNext = enumeratorType.GetMethod("MoveNext");

        var index = 0;
        while((bool)(moveNext.Invoke(enumerator, null)))
        {
            var value = getCurrent.Invoke(enumerator, null);
            items.SetValue(value, index++);
        }

        var ctor = type.GetConstructor(new Type[] { typeof(IEnumerable<>).MakeGenericType(keyValuePairType) });

        return (ctor, null, new object[] { items });
    }
}


var pickler = new Pickler();
pickler.RegisterReducer(new DictionaryReducer());
```

## FAQ

### What types can be serialized?

Pikala makes a best effort to serialize most objects.

* Pointers will be explicitly failed (this doesn't apply to `System.IntPtr` and `UIntPtr` which are serialized as 64bit integers).
* Primitive types (like `int` or `string` are explicitly handled and written out by `System.IO.BinaryWriter`.
* Reflection types like `Type` and `FieldInfo` are explicitly handled and either written out as named references or as full definitions that can be rebuilt into dynamic modules via `System.Reflection.Emit`.
* Otherwise types are handled in the following order:
    1) If the Pickler has an IReducer registered for the object type that is used.
    2) If the type inherits from [`System.Runtime.Serialization.ISerializable `](https://docs.microsoft.com/en-us/dotnet/api/system.runtime.serialization.iserializable) then `ISerializable.GetObjectData` is used to serialize, and the `(SerializationInfo, StreamingContext)` constructor is used to deserialize.
    3) If the object inherits from [`System.MarshalByRefObject`](https://docs.microsoft.com/en-us/dotnet/api/system.marshalbyrefobject) Pikala will now explicitly fail.
    4) Otherwise Pikala tries to serialize each field on the object.

### What's an IReducer?

IReducer is for reducing a complex object to a simpler one that can be serialized. Pikala has some built-in reducers, such as the one for `Dictionary<TKey, TValue>` which causes that to be serialized as a `KeyValuePair<TKey, TValue>[]` rather than trying to serialize the internal bucket structure of a `Dictionary`. (In actuality `Dictionary` is an `ISerialisable` instance, and would serialize ok without a reducer but the reduced version is a bit neater and is a good test type for the reduction framework).

Pikala is open to PRs for any BCL type that could have a better `IReducer` than it's default behaviour.

### Is this safe?

See the security warning at the top of the readme. You should only deserialize data you trust. It is possible to construct malicious data which will execute arbitrary code during deserialization. Never deserialize data that could have come from an untrusted source, or that could have been tampered with.

Consider signing data if you need to ensure that it has not been tampered with. Treat data fed into Pikala the same as you would an executable file to be run.

## License

This project is licensed under the LGPL License - see the [LICENSE](LICENSE) file for details
