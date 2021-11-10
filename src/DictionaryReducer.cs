using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ibasa.Pikala
{
    public sealed class DictionaryReducer : IReducer
    {
        readonly Type _dictionary;

        public DictionaryReducer()
        {
            _dictionary = typeof(Dictionary<,>);
        }

        public Type Type => _dictionary;

        public (MethodBase, object?, object[]) Reduce(Type type, object obj)
        {
            var getComparer = type.GetMethod("get_Comparer");
            var getEnumerator = type.GetMethod("GetEnumerator");
            var getCount = type.GetProperty("Count").GetGetMethod();

            var comparer = getComparer.Invoke(obj, null);
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

            var ctor = type.GetConstructor(new Type[] {
                typeof(IEnumerable<>).MakeGenericType(keyValuePairType),
                typeof(IEqualityComparer<>).MakeGenericType(genericParameters[0])
            });

            return (ctor, null, new object[] { items, comparer });
        }
    }
}
