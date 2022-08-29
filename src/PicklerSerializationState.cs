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
            if (memo.TryGetValue(value, out var id))
            {
                if (op != null)
                {
                    Writer.Write(op.Value);
                }
                Writer.Write15BitEncodedLong(id);
                return true;
            }
            return false;
        }

        public void AddMemo(object value)
        {
            // Save it in the memo for any later (or self) references
            memo.Add(value, memo.Count + 1);
        }

        public readonly HashSet<Type> SeenTypes = new HashSet<Type>();
    }
}
