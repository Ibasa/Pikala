using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ibasa.Pikala.Reducers
{
    public sealed class ListReducer : IReducer
    {
        readonly Type _list;

        public ListReducer()
        {
            _list = typeof(List<>);
        }

        public Type Type => _list;

        public (MethodBase, object?, object[]) Reduce(object obj)
        {
            var type = obj.GetType();
            var toArray = type.GetMethod("ToArray");

            var genericParameters = type.GetGenericArguments();
            var array = toArray.Invoke(obj, null);

            var ctor = type.GetConstructor(new Type[] {
                typeof(IEnumerable<>).MakeGenericType(genericParameters)
            });

            return (ctor, null, new object[] { array });
        }
    }
}
