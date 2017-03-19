using Microsoft.Scripting.JavaScript.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Dynamic;

namespace Microsoft.Scripting.JavaScript
{
    public sealed class JavaScriptFunction : JavaScriptObject
    {
        internal JavaScriptFunction(JavaScriptValueSafeHandle handle, JavaScriptValueType type, JavaScriptEngine engine):
            base(handle, type, engine)
        {

        }

        public JavaScriptValue Invoke()
        {
            return Apply(this, Array.Empty<JavaScriptValue>());
        }
        public JavaScriptValue Invoke(JavaScriptValue arg1)
        {
            var engine = GetEngine();
            var arr = engine.BorrowArrayOfJavaScriptValue(1);
            arr[0] = arg1;
            var result = Apply(this, arr);
            engine.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }
        public JavaScriptValue Invoke(JavaScriptValue arg1, JavaScriptValue arg2)
        {
            var engine = GetEngine();
            var arr = engine.BorrowArrayOfJavaScriptValue(2);
            arr[0] = arg1;
            arr[1] = arg2;
            var result = Apply(this, arr);
            engine.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }
        public JavaScriptValue Invoke(JavaScriptValue arg1, JavaScriptValue arg2, JavaScriptValue arg3)
        {
            var engine = GetEngine();
            var arr = engine.BorrowArrayOfJavaScriptValue(3);
            arr[0] = arg1;
            arr[1] = arg2;
            arr[2] = arg3;
            var result = Apply(this, arr);
            engine.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }
        public JavaScriptValue Invoke(JavaScriptValue arg1, JavaScriptValue arg2, JavaScriptValue arg3, JavaScriptValue arg4)
        {
            var engine = GetEngine();
            var arr = engine.BorrowArrayOfJavaScriptValue(4);
            arr[0] = arg1;
            arr[1] = arg2;
            arr[2] = arg3;
            arr[3] = arg4;
            var result = Apply(this, arr);
            engine.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }

        public JavaScriptValue Invoke(params JavaScriptValue[] args)
        {
            return Apply(this, args);
        }

        public JavaScriptValue Apply(JavaScriptValue @this, JavaScriptValue[] args)
        {
            var argsArray = args.PrependWith(@this).Select(val => val.handle_.DangerousGetHandle()).ToArray();
            if (argsArray.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(args));

            var eng = GetEngine();
            JavaScriptValueSafeHandle resultHandle;
            Errors.CheckForScriptExceptionOrThrow(api_.JsCallFunction(handle_, argsArray, (ushort)argsArray.Length, out resultHandle), eng);
            if (resultHandle.IsInvalid)
                return eng.UndefinedValue;

            return eng.CreateValueFromHandle(resultHandle);
        }

        public JavaScriptObject Construct()
        {
            return Construct(Array.Empty<JavaScriptValue>());
        }
        public JavaScriptObject Construct(JavaScriptValue arg1)
        {
            var engine = GetEngine();
            var array = engine.BorrowArrayOfJavaScriptValue(1);
            array[0] = arg1;
            var result = Construct(array);
            engine.ReleaseArrayOfJavaScriptValue(array);
            return result;
        }

        public JavaScriptObject Construct(JavaScriptValue arg1, JavaScriptValue arg2)
        {
            var engine = GetEngine();
            var array = engine.BorrowArrayOfJavaScriptValue(2);
            array[0] = arg1;
            array[1] = arg2;
            var result = Construct(array);
            engine.ReleaseArrayOfJavaScriptValue(array);
            return result;
        }

        public JavaScriptObject Construct(params JavaScriptValue[] args)
        {
            var eng = GetEngine();
            var argsArray = eng.BorrowArrayOfIntPtr(args.Length + 1);
            argsArray[0] = this.handle_.DangerousGetHandle();
            for (int i = 0; i < args.Length; i++)
            {
                argsArray[i + 1] = args[i].handle_.DangerousGetHandle();
            }

            if (argsArray.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(args));

            
            JavaScriptValueSafeHandle resultHandle;
            Errors.CheckForScriptExceptionOrThrow(api_.JsConstructObject(handle_, argsArray, (ushort)argsArray.Length, out resultHandle), eng);
            eng.ReleaseArrayOfIntPtr(argsArray);
            if (resultHandle.IsInvalid)
                return eng.NullValue;

            return eng.CreateObjectFromHandle(resultHandle);
        }

        public JavaScriptFunction Bind(JavaScriptObject thisObject, params JavaScriptValue[] args)
        {
            var eng = GetEngine();

            if (thisObject == null)
                thisObject = eng.NullValue;

            var arr = eng.BorrowArrayOfJavaScriptValue(args.Length + 1);
            arr[0] = thisObject;
            for (int i = 0; i < args.Length; i++)
            {
                arr[i + 1] = args[i];
            }

            var bindFn = GetBuiltinFunctionProperty("bind", "Function.prototype.bind");
            var result = bindFn.Invoke(arr) as JavaScriptFunction;
            eng.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }

        public JavaScriptValue Apply(JavaScriptObject thisObject, JavaScriptArray args = null)
        {
            var eng = GetEngine();
            if (thisObject == null)
                thisObject = eng.NullValue;

            var applyFn = GetBuiltinFunctionProperty("apply", "Function.prototype.apply");

            var resultList = eng.BorrowArrayOfJavaScriptValue(args != null ? 2 : 1);
            
            resultList[0] = thisObject;
            if (args != null)
                resultList[1] = args;

            var r = applyFn.Invoke(resultList);
            eng.ReleaseArrayOfJavaScriptValue(resultList);
            return r;
        }

        public JavaScriptValue Call(JavaScriptObject thisObject, IEnumerable<JavaScriptValue> args)
        {
            var eng = GetEngine();
            if (thisObject == null)
                thisObject = eng.NullValue;

            if (args == null)
                args = Enumerable.Empty<JavaScriptValue>();

            var argsArray = args.PrependWith(thisObject).Select(v => v.handle_.DangerousGetHandle()).ToArray();
            JavaScriptValueSafeHandle result;
            Errors.CheckForScriptExceptionOrThrow(api_.JsCallFunction(handle_, argsArray, unchecked((ushort)argsArray.Length), out result), eng);
            if (GetEngine().HasException)
            {
                GetEngine().SetException(GetEngine().GetAndClearException());
                return null;
            }
            return eng.CreateValueFromHandle(result);
        }

        #region DynamicObject overrides
        public override bool TryInvoke(InvokeBinder binder, object[] args, out object result)
        {
            var e = GetEngine();
            var c = e.Converter;
            var arr = e.BorrowArrayOfJavaScriptValue(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                arr[i] = c.FromObject(args[i]);
            }
            result = Invoke(arr);
            e.ReleaseArrayOfJavaScriptValue(arr);
            return true;
        }

        public override bool TryCreateInstance(CreateInstanceBinder binder, object[] args, out object result)
        {
            var e = GetEngine();
            var c = e.Converter;
            var argsArray = e.BorrowArrayOfJavaScriptValue(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                argsArray[i] = c.FromObject(args[i]);
            }
            result = Construct(argsArray);
            e.ReleaseArrayOfJavaScriptValue(argsArray);
            return true;
        }
        #endregion
    }
}
