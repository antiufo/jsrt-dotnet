using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Scripting.JavaScript
{
    public sealed class JavaScriptExecutionContext : IDisposable
    {
        private JavaScriptEngine engine_;
        private JavaScriptEngine previousEngine_;

        internal JavaScriptExecutionContext(JavaScriptEngine engine, JavaScriptEngine previousEngine)
        {
            Debug.Assert(engine != null);

            previousEngine_ = previousEngine;
            engine_ = engine;
        }

        public void Dispose()
        {
            engine_.ReleaseContextPrivate(previousEngine_);
            engine_ = null;
        }
    }
}
