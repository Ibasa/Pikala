using System;
using System.Collections.Generic;
using System.IO;

namespace Ibasa.Pikala
{
    sealed class PicklerSerializationState
    {
        public readonly SerializationStage<PicklerSerializationState> Stages = new SerializationStage<PicklerSerializationState>();

        Dictionary<object, long> memo;
        public BinaryWriter Writer { get; private set; }

        public PicklerSerializationState(Stream stream)
        {
            memo = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
            Writer = new PickleWriter(new PickleStream(stream));
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

        public readonly HashSet<Type> SeenTypes = new HashSet<Type>();
    }
}
