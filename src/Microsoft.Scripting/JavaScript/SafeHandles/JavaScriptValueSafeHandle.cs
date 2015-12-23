﻿using System;
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
        internal JavaScriptValueSafeHandle(IntPtr handle):
            base(handle, true)
        {

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
            if (IsInvalid)
                return false;

            uint count;
            var error = NativeMethods.JsRelease(handle, out count);

            Debug.Assert(error == JsErrorCode.JsNoError);
            return true;
        }
    }
}
