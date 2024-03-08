using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ibasa.Pikala
{
    enum SerializationStage
    {
        Declarations = 0,
        Definitions,
        Completion,
    }

    sealed class SerializationStage<T>
    {
        List<Queue<Action<T>>> _stages = new List<Queue<Action<T>>>();

        public SerializationStage()
        {
        }

        public void PushStage(SerializationStage stage, Action<T> action)
        {
            var index = (int)stage;

            while (index >= _stages.Count)
            {
                _stages.Add(new Queue<Action<T>>());
            }

            var queue = _stages[(int)stage];
            queue.Enqueue(action);
        }

        public void PopStages(T state, SerializationStage to = SerializationStage.Completion)
        {
            var index = (int)to;
            if (index < 0 || index > (int)SerializationStage.Completion)
            {
                throw new ArgumentException("to must be a valid SerializationStage");
            }

            for (int i = 0; i <= index; i++)
            {
                if (i >= _stages.Count)
                {
                    return;
                }

                var queue = _stages[i];

                while (queue.TryDequeue(out var action))
                {
                    action(state);

                    // Ensure that no lower stage items have been added
                    for (int j = 0; j < i; j++)
                    {
                        var lower = _stages[j];
                        System.Diagnostics.Debug.Assert(
                            lower.Count == 0,
                            string.Format("Stage {0} was added to while processing stage {1}",
                                (SerializationStage)j,
                                (SerializationStage)i
                            ));
                    }
                }

                System.Diagnostics.Debug.Assert(queue.Count == 0, string.Format("Stage {0} was not cleared", (SerializationStage)i));
            }
        }

        public void AssertEmpty()
        {
            for (int i = 0; i < _stages.Count; i++)
            {
                var queue = _stages[i];
                System.Diagnostics.Debug.Assert(queue.Count == 0,
                    string.Format("Stage {0} was not empty", (SerializationStage)i));
            }
        }
    }
}
