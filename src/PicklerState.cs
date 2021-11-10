using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Ibasa.Pikala
{ 
    abstract class MemoCallback
    {
        public abstract object? InvokeUntyped();
    }

    sealed class MemoCallback<T> : MemoCallback
    {
        Func<T> _handler;
        [MaybeNull]
        T _result;

        public MemoCallback(Func<T> handler)
        {
            _handler = handler;
        }

        [return:MaybeNull]
        public T Invoke()
        {
            if (_handler == null)
            {
                return _result;
            }
            else
            {
                _result = _handler();
                _handler = null;
                return _result;
            }
        }

        public override object? InvokeUntyped()
        {
            return Invoke();
        }
    }

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
        Dictionary<long, MemoCallback> memoCallbacks;
        List<(long, Action<object>)> fixups;
        List<Action> staticFields;
        public BinaryReader Reader { get; private set; }

        Dictionary<System.Reflection.Assembly, Dictionary<string, PickledTypeInfoDef>> _constructedTypes;

        public PicklerDeserializationState(Stream stream)
        {
            memo = new Dictionary<long, object>();
            memoCallbacks = new Dictionary<long, MemoCallback>();
            fixups = new List<(long, Action<object>)>();
            staticFields = new List<Action>();
            Reader = new BinaryReader(new PickleStream(stream));
            _constructedTypes = new Dictionary<System.Reflection.Assembly, Dictionary<string, PickledTypeInfoDef>>();
            AppDomain.CurrentDomain.TypeResolve += CurrentDomain_TypeResolve;
        }

        public void AddTypeDef(PickledTypeInfoDef type)
        {
            var assembly = type.TypeBuilder.Assembly;
            var name = type.TypeBuilder.Name;
            if (!string.IsNullOrEmpty(type.TypeBuilder.Namespace))
            {
                name = type.TypeBuilder.Namespace + "." + name;
            }

            Dictionary<string, PickledTypeInfoDef> mapping;
            if (_constructedTypes.TryGetValue(assembly, out mapping))
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

        private System.Reflection.Assembly CurrentDomain_TypeResolve(object sender, ResolveEventArgs args)
        {
            if (_constructedTypes.TryGetValue(args.RequestingAssembly, out var types))
            {
                if (types.TryGetValue(args.Name, out var type))
                {
                    if (type.FullyDefined)
                    {
                        type.CreateType();
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
            else
            {
                throw new Exception($"Tried to load type '{args.Name}' from assembly '{args.RequestingAssembly}' but assembly isn't known");
            }
            return args.RequestingAssembly;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.TypeResolve -= CurrentDomain_TypeResolve;
        }


        public void RegisterFixup(long position, Action<object> fixup)
        {
            fixups.Add((position, fixup));
        }

        public void DoFixups()
        {
            foreach (var (position, fixup) in fixups)
            {
                if (memo.TryGetValue(position, out var value))
                {
                    fixup(value);
                }
                else
                {
                    throw new MemoException(position);
                }
            }
        }

        public void DoStaticFields()
        {
            foreach (var staticFieldReader in staticFields)
            {
                staticFieldReader();
            }
        }

        public T SetMemo<T>(long position, T value)
        {
            memo.Add(position, value);
            return value;
        }

        public object DoMemo()
        {
            // Because of memo callbacks we sometimes look up positions that are themselves memos
            var memoPosition = Reader.BaseStream.Position - 1;
            var position = Reader.ReadInt64();
            if (memo.TryGetValue(position, out var value))
            {
                memo.Add(memoPosition, value);
                return value;
            }
            else if(memoCallbacks.TryGetValue(position, out var callback))
            {
                var result = callback.InvokeUntyped();
                memo.Add(memoPosition, result);
                return result;
            }
            else
            {
                throw new MemoException(position);
            }
        }

        public MemoCallback<R> RegisterMemoCallback<T, R>(long offset, Func<T, R> callback) where T : class
        {
            var objectOffset = Reader.BaseStream.Position;
            Func<R> handler = () => {
                if (memo.TryGetValue(objectOffset, out var obj))
                {
                    var result = callback(obj as T);
                    memoCallbacks.Remove(offset);
                    return result;
                }
                else
                {
                    throw new Exception($"Tried to reference object from position {offset} in the stream with callback for {objectOffset}, but that object is not yet created.");
                }
            };

            var memocallback = new MemoCallback<R>(handler);
            memoCallbacks.Add(offset, memocallback);
            return memocallback;
        }

        Stack<(Action, Action)> trailers = new Stack<(Action, Action)>();
        int trailerDepth = 0;

        public void CheckTrailers()
        {
            if (trailers.Count != 0)
            {
                throw new Exception("Serializatino trailers count should of been zero");
            }
        }

        public T RunWithTrailers<T>(Func<T> action)
        {
            int depth = trailerDepth++;

            var result = action();

            if (depth == 0)
            {
                var postTrailers = new List<Action>();
                while (trailers.Count > 0)
                {
                    var (preTrailer, postTrailer) = trailers.Pop();
                    if (preTrailer != null)
                    {
                        preTrailer();
                    }
                    postTrailers.Add(postTrailer);
                }

                foreach (var postTrailer in postTrailers)
                {
                    postTrailer();
                }
            }
            --trailerDepth;

            return result;
        }

        public void PushTrailer(Action trailer, Action footer, Action staticField)
        {
            trailers.Push((trailer, footer));
            if (staticField != null)
            {
                staticFields.Add(staticField);
            }
        }
    }

    sealed class PicklerSerializationState
    {
        Dictionary<object, long> memo;
        public BinaryWriter Writer { get; private set; }

        public PicklerSerializationState(Stream stream)
        {
            memo = new Dictionary<object, long>();
            Writer = new BinaryWriter(new PickleStream(stream));
        }

        public bool DoMemo(object value, Type staticType)
        {
            if (!staticType.IsValueType)
            {
                if (memo.TryGetValue(value, out var offset))
                {
                    Writer.Write((byte)PickleOperation.Memo);
                    Writer.Write(offset);
                    return true;
                }
                else
                {
                    // If this isn't a value type save it in the memo for any later (or self) references
                    memo.Add(value, Writer.BaseStream.Position);
                    return false;
                }
            }
            return false;
        }

        Stack<Action> trailers = new Stack<Action>();
        List<Action> statics = new List<Action>();
        int trailerDepth = 0;

        public void CheckTrailers()
        {
            if(trailers.Count != 0)
            {
                throw new Exception("Serialization trailers count should of been zero");
            }

            foreach (var staticFieldWriter in statics)
            {
                staticFieldWriter();
            }
        }

        public void RunWithTrailers(Action action)
        {
            int depth = trailerDepth++;

            action();

            if (depth == 0)
            {
                var postTrailers = new List<Action>();
                while (trailers.Count > 0)
                {
                    var trailer = trailers.Pop();
                    trailer();
                }
            }

            --trailerDepth;
        }

        public void PushTrailer(Action trailer, Action staticFields)
        {
            trailers.Push(trailer);
            statics.Add(staticFields);
        }
    }
}
