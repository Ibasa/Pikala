using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Ibasa.Pikala
{
    public sealed class MemoException : Exception
    {
        public MemoException(long position)
        {
            Position = position;
        }

        public long Position { get; }

        public override string Message => $"Tried to reference object from position {Position} in the stream, but that object is not yet created.";
    }

    sealed class PicklerDeserializationState : IDisposable
    {
        Dictionary<long, object> memo;
        public BinaryReader Reader { get; private set; }

        Dictionary<System.Reflection.Assembly, Dictionary<string, PickledTypeInfoDef>> _constructedTypes;

        public readonly SerializationStage<PicklerDeserializationState> Stages = new SerializationStage<PicklerDeserializationState>();

        public PicklerDeserializationState(Stream stream)
        {
            memo = new Dictionary<long, object>();
            Reader = new BinaryReader(new PickleStream(stream));
            _constructedTypes = new Dictionary<System.Reflection.Assembly, Dictionary<string, PickledTypeInfoDef>>();
            AppDomain.CurrentDomain.TypeResolve += CurrentDomain_TypeResolve;
        }

        public bool IsConstructedAssembly(System.Reflection.Assembly assembly)
        {
            return _constructedTypes.ContainsKey(assembly);
        }

        public void AddTypeDef(PickledTypeInfoDef type)
        {
            var assembly = type.TypeBuilder.Assembly;
            var name = type.TypeBuilder.Name;
            if (!string.IsNullOrEmpty(type.TypeBuilder.Namespace))
            {
                name = type.TypeBuilder.Namespace + "." + name;
            }

            if (_constructedTypes.TryGetValue(assembly, out var mapping))
            {
                mapping.Add(name, type);
            }
            else
            {
                mapping = new Dictionary<string, PickledTypeInfoDef>();
                mapping.Add(name, type);
                _constructedTypes.Add(assembly, mapping);
            }
        }

        private Dictionary<Type, SerialisedObjectTypeInfo> typeInfoMap = new Dictionary<Type, SerialisedObjectTypeInfo>();

        public SerialisedObjectTypeInfo? HasSeenType(Type type)
        {
            if (typeInfoMap.TryGetValue(type, out var serialisedObjectTypeInfo))
            {
                return serialisedObjectTypeInfo;
            }
            return null;
        }

        public void AddSeenType(Type type, SerialisedObjectTypeInfo info)
        {
            typeInfoMap.Add(type, info);
        }

        private System.Reflection.Assembly? CurrentDomain_TypeResolve(object? sender, ResolveEventArgs args)
        {
            if (args.RequestingAssembly != null && args.Name != null)
            {
                if (_constructedTypes.TryGetValue(args.RequestingAssembly, out var types))
                {
                    if (types.TryGetValue(args.Name, out var type))
                    {
                        var (_, isComplete) = type.Resolve();
                        if (isComplete == true)
                        {
                            return args.RequestingAssembly;
                        }
                        else
                        {
                            throw new Exception($"Tried to load type '{args.Name}' from assembly '{args.RequestingAssembly}' but it's not yet fully defined");
                        }
                    }
                    else
                    {
                        throw new Exception($"Tried to load type '{args.Name}' from assembly '{args.RequestingAssembly}' but it's not yet defined");
                    }
                }
            }
            return null;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.TypeResolve -= CurrentDomain_TypeResolve;
        }

        public object DoMemo(long memoPosition)
        {
            if (memo.TryGetValue(memoPosition, out var value))
            {
                return value;
            }
            else
            {
                throw new MemoException(memoPosition);
            }
        }

        [return: NotNull]
        public T SetMemo<T>(long position, bool shouldMemo, [DisallowNull] T value)
        {
            if (!shouldMemo)
            {
                return value;
            }

            memo.Add(position, value);
            return value;
        }

        public object DoMemo()
        {
            var position = Reader.Read15BitEncodedLong();
            return DoMemo(position);
        }
    }
}
