using System;
using System.Collections.Generic;

namespace Ibasa.Pikala
{
    sealed class SerializationStage<T>
    {
        Queue<Action<T>> _stage2 = new Queue<Action<T>>();
        Queue<Action<T>> _stage3 = new Queue<Action<T>>();
        Queue<Action<T>> _stage4 = new Queue<Action<T>>();

        public SerializationStage()
        {
        }

        public void PushStage2(Action<T> action)
        {
            _stage2.Enqueue(action);
        }
        public void PushStage3(Action<T> action)
        {
            _stage3.Enqueue(action);
        }
        public void PushStage4(Action<T> action)
        {
            _stage4.Enqueue(action);
        }

        public void PopStages(T state, int to = 4)
        {
            if (to < 2 || to > 4)
            {
                throw new ArgumentException("to must be 2, 3 or 4");
            }

            while (_stage2.TryDequeue(out var action))
            {
                action(state);
            }

            System.Diagnostics.Debug.Assert(_stage2.Count == 0);

            if (to == 2) return;

            while (_stage3.TryDequeue(out var action))
            {
                action(state);
                // Ensure that no stage 2 items have been added.
                System.Diagnostics.Debug.Assert(_stage2.Count == 0);
            }

            System.Diagnostics.Debug.Assert(_stage3.Count == 0);

            if (to == 3) return;

            while (_stage4.TryDequeue(out var action))
            {
                action(state);
                // Ensure that no stage 2 or 3 itesm have been added.
                System.Diagnostics.Debug.Assert(_stage2.Count == 0);
                System.Diagnostics.Debug.Assert(_stage3.Count == 0);
            }

            System.Diagnostics.Debug.Assert(_stage4.Count == 0);
        }

        public void AssertEmpty()
        {
            System.Diagnostics.Debug.Assert(_stage2.Count == 0);
            System.Diagnostics.Debug.Assert(_stage3.Count == 0);
            System.Diagnostics.Debug.Assert(_stage4.Count == 0);
        }
    }
}
