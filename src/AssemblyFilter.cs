using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Ibasa.Pikala
{
    public abstract class AssemblyFilter
    {
        protected readonly Assembly mscorlib = typeof(int).Assembly;

        public abstract bool Contains(Assembly assembly);
    }

    public sealed class InclusiveAssemblyFilter : AssemblyFilter, IEnumerable<Assembly>
    {
        private HashSet<Assembly> _set;

        public InclusiveAssemblyFilter()
        {
            _set = new HashSet<Assembly>();
        }

        public bool Add(Assembly assembly)
        {
            if (assembly == mscorlib)
            {
                throw new InvalidOperationException("Can not add mscorlib to filter");
            }
            return _set.Add(assembly);
        }

        public override bool Contains(Assembly assembly)
        {
            return _set.Contains(assembly);
        }

        public IEnumerator<Assembly> GetEnumerator()
        {
            return ((IEnumerable<Assembly>)_set).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_set).GetEnumerator();
        }
    }

    public sealed class ExclusiveAssemblyFilter : AssemblyFilter, IEnumerable<Assembly>
    {
        private HashSet<Assembly> _set;

        public ExclusiveAssemblyFilter()
        {
            _set = new HashSet<Assembly>();
            // Special case that mscorlib never passes the filter
            _set.Add(mscorlib);
        }

        public bool Add(Assembly assembly)
        {
            return _set.Add(assembly);
        }

        public override bool Contains(Assembly assembly)
        {
            return !_set.Contains(assembly);
        }

        public IEnumerator<Assembly> GetEnumerator()
        {
            return ((IEnumerable<Assembly>)_set).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_set).GetEnumerator();
        }
    }
}
