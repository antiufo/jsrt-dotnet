using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Scripting.JavaScript.SafeHandles
{
    internal class JavaScriptValueSafeHandle : SafeHandle
    {
        private WeakReferenceStruct<JavaScriptEngine> engine_;

        public JavaScriptValueSafeHandle():
            base(IntPtr.Zero, ownsHandle: true)
        {

        }

        public JavaScriptValueSafeHandle(IntPtr handle):
            base(handle, true)
        {

        }

        internal void SetEngine(JavaScriptEngine engine)
        {
            Debug.Assert(engine != null);

            engine_ = engine.engineWeakReference;
        }

        public override bool IsInvalid
        {
            get
            {
                return handle == IntPtr.Zero;
            }
        }

        protected override bool ReleaseHandle()
        {
            if (IsInvalid || engine_.IsNil)
                return false;

            JavaScriptEngine eng;
            if (engine_.TryGetTarget(out eng))
            {
                eng.EnqueueRelease(handle);
                return true;
            }
            
            return false;
        }
    }
}
