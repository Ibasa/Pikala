﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Ibasa.Pikala
{
    abstract class MemoCallback
    {
        public abstract object InvokeUntyped();

        public abstract void SetValue(object value);
    }

    sealed class MemoCallback<T, R> : MemoCallback where T : class where R : class
    {
        long _memoPosition, _objectPosition;
        Func<T, R>? _handler;
        R? _result;
        T? _value;

        public MemoCallback(long memoPosition, long objectPosition, Func<T, R> handler)
        {
            _memoPosition = memoPosition;
            _objectPosition = objectPosition;
            _handler = handler;
        }

        public R Invoke()
        {
            if (_handler == null)
            {
                // We know if _handler is null we must of called it and it will of returned a value for _result.
                return _result!;
            }
            else
            {
                if (_value == null)
                {
                    throw new Exception($"Tried to reference object from position {_objectPosition} in the stream with callback for {_memoPosition}, but that object is not yet created.");
                }
                _result = _handler(_value);
                _value = null;
                _handler = null;
                return _result;
            }
        }

        public override void SetValue(object value)
        {
            System.Diagnostics.Debug.Assert(_value == null, "Trying to set a memo callback value that's already set");
            System.Diagnostics.Debug.Assert(_result == null, "Trying to set a memo callback value that's already finished");
            System.Diagnostics.Debug.Assert(value != null, "Can't set a null object for a memo callback");
            _value = (T)value;
        }

        public override object InvokeUntyped()
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
        // We need to lookup callbacks based on the origional position, and by the object their waiting for position
        Dictionary<long, MemoCallback> memoCallbacks_byMemoPosition;
        Dictionary<long, MemoCallback> memoCallbacks_byObjPosition;
        List<Action> staticFields;
        public BinaryReader Reader { get; private set; }

        Dictionary<System.Reflection.Assembly, Dictionary<string, PickledTypeInfoDef>> _constructedTypes;

        public PicklerDeserializationState(Stream stream)
        {
            memo = new Dictionary<long, object>();
            memoCallbacks_byMemoPosition = new Dictionary<long, MemoCallback>();
            memoCallbacks_byObjPosition = new Dictionary<long, MemoCallback>();
            staticFields = new List<Action>();
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

        public void DoStaticFields()
        {
            foreach (var staticFieldReader in staticFields)
            {
                staticFieldReader();
            }
        }

        public object DoMemo(long objectPosition, long memoPosition)
        {
            if (memo.TryGetValue(memoPosition, out var value))
            {
                if (memoCallbacks_byObjPosition.TryGetValue(objectPosition, out var callback))
                {
                    callback.SetValue(value);
                    memoCallbacks_byObjPosition.Remove(objectPosition);
                }

                return value;
            }
            else if (memoCallbacks_byMemoPosition.TryGetValue(memoPosition, out var callback))
            {
                var result = callback.InvokeUntyped();
                System.Diagnostics.Debug.Assert(!memoCallbacks_byMemoPosition.ContainsKey(memoPosition), "Invoked a memo callback but was still present in callback map after");
                return result;
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
                System.Diagnostics.Debug.Assert(!memoCallbacks_byObjPosition.ContainsKey(position), "Not memoing this object but it has a callback for it");
                return value;
            }

            if (memoCallbacks_byObjPosition.TryGetValue(position, out var callback))
            {
                callback.SetValue(value);
                memoCallbacks_byObjPosition.Remove(position);
            }
            memoCallbacks_byMemoPosition.Remove(position);

            memo.Add(position, value);
            return value;
        }

        public object DoMemo()
        {
            // We might have a callback waiting to deserialize the object at this position but it was just a memo to a previous object,
            // make sure we still set and clear the callback
            var objectPosition = Reader.BaseStream.Position - 1;

            var position = Reader.Read15BitEncodedLong();

            return DoMemo(objectPosition, position);
        }

        public MemoCallback<T, R> RegisterMemoCallback<T, R>(long offset, Func<T, R> callback) where T : class where R : class
        {
            var objectOffset = Reader.BaseStream.Position;

            System.Diagnostics.Debug.Assert(offset != objectOffset, "Can't create a callback for the same object currently deserializing");

            var memocallback = new MemoCallback<T, R>(offset, objectOffset, callback);
            memoCallbacks_byMemoPosition.Add(offset, memocallback);
            memoCallbacks_byObjPosition.Add(objectOffset, memocallback);
            return memocallback;
        }

        Stack<(Action?, Action?)> trailers = new Stack<(Action?, Action?)>();
        int trailerDepth = 0;

        public void CheckTrailers()
        {
            if (trailers.Count != 0)
            {
                throw new Exception("Serialization trailers count should of been zero");
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
                    if (postTrailer != null)
                    {
                        postTrailers.Add(postTrailer);
                    }
                }

                foreach (var postTrailer in postTrailers)
                {
                    postTrailer();
                }
            }
            --trailerDepth;

            return result;
        }

        public void PushTrailer(Action? trailer, Action? footer, Action? staticField)
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
        // This is built in from .NET 5 onwards.
        class ReferenceEqualityComparer : EqualityComparer<object>
        {
            private static IEqualityComparer<object>? _defaultComparer;

            public new static IEqualityComparer<object> Default
            {
                get { return _defaultComparer ?? (_defaultComparer = new ReferenceEqualityComparer()); }
            }

            public override bool Equals(object? x, object? y)
            {
                return ReferenceEquals(x, y);
            }

            public override int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        Dictionary<object, long> memo;
        public BinaryWriter Writer { get; private set; }

        public PicklerSerializationState(Stream stream)
        {
            memo = new Dictionary<object, long>(ReferenceEqualityComparer.Default);
            Writer = new BinaryWriter(new PickleStream(stream));
        }
        public bool MaybeWriteMemo(object value, byte? op)
        {
            if (memo.TryGetValue(value, out var offset))
            {
                if (op != null)
                {
                    Writer.Write(op.Value);
                }
                Writer.Write15BitEncodedLong(offset);
                return true;
            }
            return false;
        }

        public void AddMemo(long position, object value)
        {
            // Save it in the memo for any later (or self) references
            memo.Add(value, position);
#if DEBUG
            // In debug mode we do a sanity check that we haven't possibly screwed up memoisation by checking that every position stored in
            // memo is unique
            var set = new HashSet<long>(memo.Values);
            System.Diagnostics.Debug.Assert(set.Count == memo.Count, "Two distinct objects tried to memoise to the same position");
#endif
        }

        Stack<Action> trailers = new Stack<Action>();
        List<Action> statics = new List<Action>();
        int trailerDepth = 0;

        public readonly HashSet<Type> SeenTypes = new HashSet<Type>();

        public void CheckTrailers()
        {
            if (trailers.Count != 0)
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
