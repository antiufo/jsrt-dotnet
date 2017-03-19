using Microsoft.Scripting.JavaScript.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Scripting.JavaScript
{
    public delegate void JavaScriptExternalObjectFinalizeCallback(object additionalData);
    public delegate JavaScriptValue JavaScriptCallableFunction(JavaScriptEngine callingEngine, bool asConstructor, JavaScriptValue thisValue, JavaScriptValue[] arguments);

    public sealed class JavaScriptEngine : IDisposable
    {
        private class NativeFunctionThunkData
        {
            public JavaScriptCallableFunction callback;
            public WeakReference<JavaScriptEngine> engine;
        }

        private class ExternalObjectThunkData
        {
            public WeakReference<JavaScriptEngine> engine;
            public WeakReference<object> userData;
            public object userDataStrong;
            public JavaScriptExternalObjectFinalizeCallback callback;
        }

        private JavaScriptEngineSafeHandle handle_;
        private WeakReference<JavaScriptRuntime> runtime_;
        private JavaScriptConverter converter_;
        private List<NativeFunctionThunkData> nativeFunctionThunks_;
        private static NativeFunctionThunkCallback NativeCallback;
        private static IntPtr NativeCallbackPtr;


        private List<IntPtr[]>[] IntPtrArrayPool = new List<IntPtr[]>[10];
        private List<object[]>[] ObjectArrayPool = new List<object[]>[10];
        private List<JavaScriptValue[]>[] JavaScriptValueArrayPool = new List<JavaScriptValue[]>[10];

        internal IntPtr[] BorrowArrayOfIntPtr(int length)
        {
            return BorrowArray(IntPtrArrayPool, length);
        }
        internal void ReleaseArrayOfIntPtr(IntPtr[] array)
        {
            ReleaseArray(IntPtrArrayPool, array, false);
        }


        internal object[] BorrowArrayOfObjects(int length)
        {
            return BorrowArray(ObjectArrayPool, length);
        }
        internal void ReleaseArrayOfObjects(object[] array)
        {
            ReleaseArray(ObjectArrayPool, array, true);
        }



        internal JavaScriptValue[] BorrowArrayOfJavaScriptValue(int length)
        {
            return BorrowArray(JavaScriptValueArrayPool, length);
        }
        internal void ReleaseArrayOfJavaScriptValue(JavaScriptValue[] array)
        {
            ReleaseArray(JavaScriptValueArrayPool, array, true);
        }

        private void ReleaseArray<T>(List<T[]>[] pool, T[] array, bool clear)
        {
            if (array.Length >= pool.Length || array.Length == 0) return;
            var z = pool[array.Length];
            if (z == null) return;
            if (z.Count >= 10) return;
            if (clear)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    array[i] = default(T);
                }
            }
            z.Add(array);
        }

        private T[] BorrowArray<T>(List<T[]>[] pool, int length)
        {
            if (length == 0) return Array.Empty<T>();
            if (length >= pool.Length) return new T[length];
            var z = pool[length];
            if (z == null)
            {
                pool[length] = z = new List<T[]>();
            }
            if (z.Count == 0) return new T[length];
            var last = z[z.Count - 1];
            z.RemoveAt(z.Count - 1);
            return last;


        }

        private static JsFinalizeCallback FinalizerCallback;
        private static IntPtr FinalizerCallbackPtr;
        private HashSet<ExternalObjectThunkData> externalObjects_;
        private ChakraApi api_;

        private List<IntPtr> handlesToRelease_;
        private object handleReleaseLock_;

        private JavaScriptValue undefined_, true_, false_;
        private JavaScriptObject global_, null_;
        public Exception lastException;

        static JavaScriptEngine()
        {
            NativeCallback = NativeCallbackThunk;
            NativeCallbackPtr = Marshal.GetFunctionPointerForDelegate(NativeCallback);

            FinalizerCallback = FinalizerCallbackThunk;
            FinalizerCallbackPtr = Marshal.GetFunctionPointerForDelegate(FinalizerCallback);
        }

        internal JavaScriptEngine(JavaScriptEngineSafeHandle handle, JavaScriptRuntime runtime, ChakraApi api)
        {
            Debug.Assert(handle != null);
            Debug.Assert(runtime != null);
            Debug.Assert(api != null);
            api_ = api;

            handle_ = handle;
            runtime_ = new WeakReference<JavaScriptRuntime>(runtime);
            converter_ = new JavaScriptConverter(this);
            nativeFunctionThunks_ = new List<NativeFunctionThunkData>();
            externalObjects_ = new HashSet<ExternalObjectThunkData>();

            handlesToRelease_ = new List<IntPtr>();
            handleReleaseLock_ = new object();

            using (AcquireContext())
            {
                JavaScriptValueSafeHandle global;
                Errors.ThrowIfIs(api_.JsGetGlobalObject(out global));
                global_ = CreateObjectFromHandle(global);
                JavaScriptValueSafeHandle undef;
                Errors.ThrowIfIs(api_.JsGetUndefinedValue(out undef));
                undefined_ = CreateValueFromHandle(undef);
                JavaScriptValueSafeHandle @null;
                Errors.ThrowIfIs(api_.JsGetNullValue(out @null));
                null_ = CreateObjectFromHandle(@null);
                JavaScriptValueSafeHandle @true;
                Errors.ThrowIfIs(api_.JsGetTrueValue(out @true));
                true_ = CreateValueFromHandle(@true);
                JavaScriptValueSafeHandle @false;
                Errors.ThrowIfIs(api_.JsGetFalseValue(out @false));
                false_ = CreateValueFromHandle(@false);
            }
        }

        public JavaScriptRuntime Runtime
        {
            get
            {
                JavaScriptRuntime result;
                if (!runtime_.TryGetTarget(out result))
                    throw new ObjectDisposedException(nameof(JavaScriptEngine));

                return result;
            }
        }

        public JavaScriptConverter Converter
        {
            get
            {
                return converter_;
            }
        }

        #region Internal methods
        internal ChakraApi Api
        {
            get
            {
                return api_;
            }
        }

        internal void EnqueueRelease(IntPtr handle)
        {
            lock (handleReleaseLock_)
            {
                handlesToRelease_.Add(handle);
            }
        }


        [ThreadStatic]
        private static JavaScriptEngine currentEngine;

        public JavaScriptExecutionContext AcquireContext()
        {
            var c = currentEngine;
            if (c != this)
            {
                ClaimContextPrivate();
                currentEngine = this;
            }
            return new JavaScriptExecutionContext(this, c);
        }

        private void ClaimContextPrivate()
        {
            if (handle_ == null)
                throw new ObjectDisposedException(nameof(JavaScriptEngine));

            Errors.ThrowIfIs(api_.JsSetCurrentContext(handle_));

            if (handlesToRelease_.Count > 0)
            {
                lock (handleReleaseLock_)
                {
                    foreach (IntPtr handle in handlesToRelease_)
                    {
                        uint count;
                        var error = api_.JsRelease(handle, out count);
                        Debug.Assert(error == JsErrorCode.JsNoError);
                    }
                    
                    handlesToRelease_.Clear();
                }
            }
        }

        internal void ReleaseContextPrivate(JavaScriptEngine previous)
        {
            if (currentEngine != this) throw new Exception("Incorrect JavaScriptExecutionContext disposal.");
            if (previous != currentEngine)
            {
                Errors.ThrowIfIs(api_.JsReleaseCurrentContext());
                currentEngine = previous;
                if (previous != null)
                {
                    previous.ClaimContextPrivate();
                }
            }
            
        }

        internal JavaScriptValue CreateValueFromHandle(JavaScriptValueSafeHandle handle)
        {
            Debug.Assert(!(handle.IsClosed || handle.IsInvalid));

            JsValueType kind;
            Errors.ThrowIfIs(api_.JsGetValueType(handle, out kind));

            JavaScriptValue result = null;
            switch (kind)
            {
                case JsValueType.JsArray:
                    result = new JavaScriptArray(handle, JavaScriptValueType.Array, this);
                    break;

                case JsValueType.JsFunction:
                    result =new JavaScriptFunction(handle, JavaScriptValueType.Function, this);
                    break;

                case JsValueType.JsObject:
                case JsValueType.JsNull:
                case JsValueType.JsError:
                    result = new JavaScriptObject(handle, JavaScriptValueType.Object, this);
                    break;

                case JsValueType.JsSymbol:
                    result = new JavaScriptSymbol(handle, JavaScriptValueType.Symbol, this);
                    break;

                case JsValueType.JsArrayBuffer:
                    result = new JavaScriptArrayBuffer(handle, JavaScriptValueType.ArrayBuffer, this);
                    break;

                case JsValueType.JsTypedArray:
                    result = new JavaScriptTypedArray(handle, JavaScriptValueType.TypedArray, this);
                    break;

                case JsValueType.JsDataView:
                    result = new JavaScriptDataView(handle, JavaScriptValueType.DataView, this);
                    break;

                case JsValueType.JsBoolean:
                case JsValueType.JsNumber:
                case JsValueType.JsString:
                case JsValueType.JsUndefined:
                default:
                    result = new JavaScriptValue(handle, kind.ToApiValueType(), this);
                    break;
            }
            
            return result;
        }

        internal JavaScriptObject CreateObjectFromHandle(JavaScriptValueSafeHandle handle)
        {
            JsValueType kind;
            Errors.ThrowIfIs(api_.JsGetValueType(handle, out kind));

            JavaScriptObject result = null;
            switch (kind)
            {
                case JsValueType.JsArray:
                    result = new JavaScriptArray(handle, JavaScriptValueType.Array, this);
                    break;

                case JsValueType.JsFunction:
                    result = new JavaScriptFunction(handle, JavaScriptValueType.Function, this);
                    break;

                case JsValueType.JsObject:
                case JsValueType.JsError:
                case JsValueType.JsNull:
                    result = new JavaScriptObject(handle, JavaScriptValueType.Object, this);
                    break;

                case JsValueType.JsArrayBuffer:
                    result = new JavaScriptArrayBuffer(handle, JavaScriptValueType.ArrayBuffer, this);
                    break;

                case JsValueType.JsTypedArray:
                    result = new JavaScriptTypedArray(handle, JavaScriptValueType.TypedArray, this);
                    break;

                case JsValueType.JsDataView:
                    result = new JavaScriptDataView(handle, JavaScriptValueType.DataView, this);
                    break;

                case JsValueType.JsBoolean:
                case JsValueType.JsNumber:
                case JsValueType.JsString:
                case JsValueType.JsUndefined:
                case JsValueType.JsSymbol:
                default:
                    throw new ArgumentException();
            }

            return result;
        }

        internal JavaScriptArray CreateArrayFromHandle(JavaScriptValueSafeHandle handle)
        {
            JsValueType kind;
            Errors.ThrowIfIs(api_.JsGetValueType(handle, out kind));

            switch (kind)
            {
                case JsValueType.JsArray:
                    var result = new JavaScriptArray(handle, JavaScriptValueType.Array, this);
                    return result;

                case JsValueType.JsFunction:
                case JsValueType.JsObject:
                case JsValueType.JsError:
                case JsValueType.JsNull:
                case JsValueType.JsArrayBuffer:
                case JsValueType.JsTypedArray:
                case JsValueType.JsDataView:
                case JsValueType.JsBoolean:
                case JsValueType.JsNumber:
                case JsValueType.JsString:
                case JsValueType.JsUndefined:
                case JsValueType.JsSymbol:
                default:
                    throw new ArgumentException();
            }
        }
        #endregion

        #region Base properties
        public JavaScriptObject GlobalObject
        {
            get
            {
                return global_;
            }
        }

        public JavaScriptValue UndefinedValue
        {
            get
            {
                return undefined_;
            }
        }

        public JavaScriptObject NullValue
        {
            get
            {
                return null_;
            }
        }

        public JavaScriptValue TrueValue
        {
            get
            {
                return true_;
            }
        }

        public JavaScriptValue FalseValue
        {
            get
            {
                return false_;
            }
        }

        public bool HasException
        {
            get
            {
                bool has;
                Errors.ThrowIfIs(api_.JsHasException(out has));

                return has;
            }
        }

        public event EventHandler RuntimeExceptionRaised;
        internal void OnRuntimeExceptionRaised()
        {
            var rer = RuntimeExceptionRaised;
            if (rer != null)
                rer(this, EventArgs.Empty);
        }
        #endregion

        #region Code execution
        public JavaScriptFunction EvaluateScriptText(string code)
        {
            return Evaluate(new ScriptSource("[eval code]", code));
        }

        public JavaScriptFunction Evaluate(ScriptSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsParseScript(source.SourceText, source.SourceContextId, source.SourceLocation, out handle));

            return CreateObjectFromHandle(handle) as JavaScriptFunction;
        }

        public JavaScriptFunction Evaluate(ScriptSource source, Stream compiledCode)
        {
            throw new NotSupportedException();
        }

        public unsafe void Compile(ScriptSource source, Stream compiledCodeDestination)
        {
            uint bufferSize = 0;
            Errors.ThrowIfIs(api_.JsSerializeScript(source.SourceText, IntPtr.Zero, ref bufferSize));
            if (bufferSize > int.MaxValue)
                throw new OutOfMemoryException();

            IntPtr mem = Marshal.AllocCoTaskMem(unchecked((int)bufferSize));
            var error = api_.JsSerializeScript(source.SourceText, mem, ref bufferSize);
            if (error != JsErrorCode.JsNoError)
            {
                Marshal.FreeCoTaskMem(mem);
                Errors.ThrowFor(error);
            }

            using (UnmanagedMemoryStream ums = new UnmanagedMemoryStream((byte*)mem.ToPointer(), bufferSize))
            {
                ums.CopyTo(compiledCodeDestination);
            }
            Marshal.FreeCoTaskMem(mem);
        }

        public JavaScriptValue Execute(ScriptSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            JavaScriptValueSafeHandle handle;
            Errors.CheckForScriptExceptionOrThrow(api_.JsRunScript(source.SourceText, source.SourceContextId, source.SourceLocation, out handle), this);
            if (handle.IsInvalid)
                return undefined_;

            return CreateValueFromHandle(handle);
        }

        public JavaScriptValue Execute(ScriptSource source, Stream compiledCode)
        {
            throw new NotSupportedException();
        }
        #endregion

        #region Main interaction functions
        public JavaScriptObject CreateObject(JavaScriptObject prototype = null)
        {
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateObject(out handle));

            if (prototype != null)
            {
                Errors.ThrowIfIs(api_.JsSetPrototype(handle, prototype.handle_));
            }

            return CreateObjectFromHandle(handle);
        }

        private static void FinalizerCallbackThunk(IntPtr externalData)
        {
            if (externalData == IntPtr.Zero)
                return;

            GCHandle handle = GCHandle.FromIntPtr(externalData);
            var thunkData = handle.Target as ExternalObjectThunkData;
            if (thunkData == null)
                return;

            var engine = thunkData.engine;
            var callback = thunkData.callback;
            object userData;
            userData = thunkData.userDataStrong;
            if (userData == null)
                thunkData.userData.TryGetTarget(out userData);
            
            if (callback != null)
                callback(userData);

            JavaScriptEngine eng;
            if (engine.TryGetTarget(out eng))
            {
                eng.externalObjects_.Remove(thunkData);
            }
        }

        private ConditionalWeakTable<object, JavaScriptObject> externalObjectsDict = new ConditionalWeakTable<object, JavaScriptObject>();

        public JavaScriptObject CreateExternalObject(object externalData, JavaScriptExternalObjectFinalizeCallback finalizeCallback)
        {
            if (externalObjectsDict.TryGetValue(externalData, out var obj))
            {
                return obj;
            }

            ExternalObjectThunkData thunk = new ExternalObjectThunkData() { callback = finalizeCallback, engine = new WeakReference<JavaScriptEngine>(this), userDataStrong = externalData/*, userData = new WeakReference<object>(externalData),*/ };
            GCHandle handle = GCHandle.Alloc(thunk);
            externalObjects_.Add(thunk);

            JavaScriptValueSafeHandle result;
            Errors.ThrowIfIs(api_.JsCreateExternalObject(GCHandle.ToIntPtr(handle), FinalizerCallbackPtr, out result));

            obj = CreateObjectFromHandle(result);
            externalObjectsDict.Add(externalData, obj);
            return obj;
        }

        internal object GetExternalObjectFrom(JavaScriptValue value)
        {
            Debug.Assert(value != null);
            IntPtr handlePtr;
            var r = api_.JsGetExternalData(value.handle_, out handlePtr);
            if (r == JsErrorCode.JsErrorInvalidArgument) return null;
            Errors.ThrowIfIs(r);
            GCHandle gcHandle = GCHandle.FromIntPtr(handlePtr);
            ExternalObjectThunkData thunk = gcHandle.Target as ExternalObjectThunkData;
            if (thunk == null)
                return null;
            if(thunk.userDataStrong != null)
                return thunk.userDataStrong;
            object result;
            thunk.userData.TryGetTarget(out result);
            return result;
        }

        public JavaScriptSymbol CreateSymbol(string description)
        {
            JavaScriptValueSafeHandle handle;
            using (var str = converter_.FromString(description))
            {
                Errors.ThrowIfIs(api_.JsCreateSymbol(str.handle_, out handle));
            }

            return CreateValueFromHandle(handle) as JavaScriptSymbol;
        }

        public TimeSpan RunIdleWork()
        {
            uint nextTick;
            Errors.ThrowIfIs(api_.JsIdle(out nextTick));

            return TimeSpan.FromTicks(nextTick);
        }

        public bool HasGlobalVariable(string name)
        {
            return GlobalObject.HasOwnProperty(name);
        }

        public JavaScriptValue GetGlobalVariable(string name)
        {
            return GlobalObject.GetPropertyByName(name);
        }

        public void SetGlobalVariable(string name, JavaScriptValue value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            GlobalObject.SetPropertyByName(name, value);
        }

        public JavaScriptValue CallGlobalFunction(string functionName)
        {
            return CallGlobalFunction(functionName, Enumerable.Empty<JavaScriptValue>());
        }

        public JavaScriptValue CallGlobalFunction(string functionName, IEnumerable<JavaScriptValue> args)
        {
            var global = GlobalObject;
            var fn = global.GetPropertyByName(functionName) as JavaScriptFunction;
            return fn.Call(global, args);
        }

        public void SetGlobalFunction(string functionName, JavaScriptCallableFunction hostFunction)
        {
            if (hostFunction == null)
                throw new ArgumentNullException(nameof(hostFunction));

            GlobalObject.SetPropertyByName(functionName, CreateFunction(hostFunction, functionName));
        }

        public JavaScriptArray CreateArray(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateArray(unchecked((uint)length), out handle));

            return CreateArrayFromHandle(handle);
        }

        private static IntPtr NativeCallbackThunk(
            IntPtr callee, 
            [MarshalAs(UnmanagedType.U1)] bool asConstructor, 
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] IntPtr[] args, 
            ushort argCount, 
            IntPtr data)
        {
            // callee and args[0] are the same
            if (data == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(data);
                var nativeThunk = handle.Target as NativeFunctionThunkData;
                JavaScriptEngine engine;
                if (!nativeThunk.engine.TryGetTarget(out engine))
                    return IntPtr.Zero;

                JavaScriptValue thisValue = null;
                if (argCount > 0)
                {
                    thisValue = engine.CreateValueFromHandle(new JavaScriptValueSafeHandle(args[0]));
                }

                var arr = engine.BorrowArrayOfJavaScriptValue(args.Length - 1);
                for (int i = 1; i < args.Length; i++)
                {
                    arr[i - 1] = engine.CreateValueFromHandle(new JavaScriptValueSafeHandle(args[i]));
                }
                try
                {
                    var result = nativeThunk.callback(engine, asConstructor, thisValue, arr);
                    engine.ReleaseArrayOfJavaScriptValue(arr);
                    return result.handle_.DangerousGetHandle();
                }
                catch (Exception ex)
                {
                    var error = engine.CreateError(ex.Message);
                    engine.SetException(error, ex);
                    return engine.UndefinedValue.handle_.DangerousGetHandle();
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public JavaScriptFunction CreateFunction(JavaScriptCallableFunction hostFunction)
        {
            if (hostFunction == null)
                throw new ArgumentNullException(nameof(hostFunction));

            NativeFunctionThunkData td = new NativeFunctionThunkData() { callback = hostFunction, engine = new WeakReference<JavaScriptEngine>(this) };
            GCHandle handle = GCHandle.Alloc(td, GCHandleType.Weak);
            nativeFunctionThunks_.Add(td);

            JavaScriptValueSafeHandle resultHandle;
            Errors.ThrowIfIs(api_.JsCreateFunction(NativeCallbackPtr, GCHandle.ToIntPtr(handle), out resultHandle));

            return CreateObjectFromHandle(resultHandle) as JavaScriptFunction;
        }

        public JavaScriptFunction CreateFunction(JavaScriptCallableFunction hostFunction, string name)
        {
            if (hostFunction == null)
                throw new ArgumentNullException(nameof(hostFunction));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            var nameVal = Converter.FromString(name);

            NativeFunctionThunkData td = new NativeFunctionThunkData() { callback = hostFunction, engine = new WeakReference<JavaScriptEngine>(this) };
            GCHandle handle = GCHandle.Alloc(td, GCHandleType.Weak);
            nativeFunctionThunks_.Add(td);

            JavaScriptValueSafeHandle resultHandle;
            Errors.ThrowIfIs(api_.JsCreateNamedFunction(nameVal.handle_, NativeCallbackPtr, GCHandle.ToIntPtr(handle), out resultHandle));

            return CreateObjectFromHandle(resultHandle) as JavaScriptFunction;
        }

        public JavaScriptValue GetAndClearException()
        {
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsGetAndClearException(out handle));

            return CreateValueFromHandle(handle);
        }

        public void SetException(JavaScriptValue exception, Exception clrException = null)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            while (clrException is TargetInvocationException && clrException.InnerException != null)
                clrException = clrException.InnerException;

            if(HasException) GetAndClearException();
            var stack = Execute(new ScriptSource(string.Empty, "new Error('StackRetrieval').stack"));
            if (clrException == null) clrException = new Exception(exception.ToString());
            string javaScriptStack = null;
            if (stack.Type == JavaScriptValueType.String)
            {
                var val = stack.ToString();
                const string expectedStart = "Error: StackRetrieval\n   at Global code (:1:1)\n";
                if (val.StartsWith(expectedStart)) val = val.Substring(expectedStart.Length);
                javaScriptStack = val;
            }
            if (HasException) GetAndClearException();

            if (javaScriptStack != null)
            {
                var last = clrException;
                var stackTraceField = typeof(Exception).GetField("_stackTraceString", BindingFlags.NonPublic | BindingFlags.Instance);
                var stackTrace = clrException.StackTrace + "\n" + javaScriptStack;
                stackTraceField.SetValue(clrException, stackTrace);
            }
            Errors.ThrowIfIs(api_.JsSetException(exception.handle_));
            this.lastException = clrException;
        }
        #endregion

        #region Errors
        public JavaScriptObject CreateError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }

        public JavaScriptObject CreateRangeError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateRangeError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }

        public JavaScriptObject CreateReferenceError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateReferenceError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }

        public JavaScriptObject CreateSyntaxError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateSyntaxError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }

        public JavaScriptObject CreateTypeError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateTypeError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }

        public JavaScriptObject CreateUriError(string message = "")
        {
            var str = Converter.FromString(message ?? "");
            JavaScriptValueSafeHandle handle;
            Errors.ThrowIfIs(api_.JsCreateURIError(str.handle_, out handle));

            return CreateObjectFromHandle(handle);
        }
        #endregion

        public bool CanEnableDebugging
        {
            get
            {
                return api_.JsStartDebugging != null;
            }
        }

        public void EnableDebugging()
        {
            if (api_.JsStartDebugging == null)
                throw new NotSupportedException("Debugging is not supported with ChakraCore.  Check the CanEnableDebugging property before attempting to enable debugging.");

            Errors.ThrowIfIs(api_.JsStartDebugging());
        }

        public void AddTypeToGlobal<T>(string name = null)
        {
            Type t = typeof(T);
            if (null == name)
            {
                name = t.Name;
            }

            var proj = Converter.GetProjectionPrototypeForType(t);
            SetGlobalVariable(name, proj.GetPropertyByName("constructor"));
        }

        #region IDisposable implementation
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (handle_ != null)
                {
                    handle_.Dispose();
                    handle_ = null;
                }
            }
        }

        ~JavaScriptEngine()
        {
            Dispose(false);
        }
        #endregion
    }
}
