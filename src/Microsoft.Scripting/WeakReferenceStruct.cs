using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Scripting.JavaScript;
using System.Runtime.InteropServices;

namespace Microsoft.Scripting
{
    struct WeakReferenceStruct<T>
    {
        private GCHandle handle;

        public WeakReferenceStruct(T value)
        {
            handle = GCHandle.Alloc(value, GCHandleType.Weak);
        }

        public bool IsNil => handle == default(GCHandle);

        internal bool TryGetTarget(out T target)
        {
            target = (T)handle.Target;
            return target != null;
        }
    }
}
