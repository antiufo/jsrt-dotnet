using Microsoft.Scripting.JavaScript.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;

namespace Microsoft.Scripting.JavaScript
{
    public sealed class JavaScriptArray : JavaScriptObject, IEnumerable<JavaScriptValue>
    {
        internal JavaScriptArray(JavaScriptValueSafeHandle handle, JavaScriptValueType type, JavaScriptEngine engine):
            base(handle, type, engine)
        {

        }

        public int Length
        {
            get
            {
                var eng = GetEngine();
                return eng.Converter.ToInt32(GetPropertyByName("length"));
            }
        }

        public JavaScriptValue this[int index]
        {
            get { return GetAt(index); }
            set { SetAt(index, value); }
        }

        public JavaScriptValue GetAt(int index)
        {
            var eng = GetEngine();

            JavaScriptValueSafeHandle resultHandle;
            using (var temp = eng.Converter.FromInt32(index))
            {
                Errors.ThrowIfIs(api_.JsGetIndexedProperty(handle_, temp.handle_, out resultHandle));
            }
            return eng.CreateValueFromHandle(resultHandle);
        }

        public void SetAt(int index, JavaScriptValue value)
        {
            var eng = GetEngine();

            using (var temp = eng.Converter.FromInt32(index))
            {
                Errors.ThrowIfIs(api_.JsSetIndexedProperty(handle_, temp.handle_, value.handle_));
            }
        }

        private JavaScriptFunction GetArrayBuiltin(string name)
        {
            var eng = GetEngine();
            var arrayCtor = eng.GlobalObject.GetPropertyByName("Array") as JavaScriptFunction;
            if (arrayCtor == null)
                Errors.ThrowIOEFmt(Errors.DefaultFnOverwritten, "Array");
            var arrayPrototype = arrayCtor.Prototype;
            if (arrayPrototype == null)
                Errors.ThrowIOEFmt(Errors.DefaultFnOverwritten, "Array.prototype");
            var fn = arrayPrototype.GetPropertyByName(name) as JavaScriptFunction;
            if (fn == null)
                Errors.ThrowIOEFmt(Errors.DefaultFnOverwritten, "Array.prototype." + name);

            return fn;
        }

        public JavaScriptValue Pop()
        {
            var fn = GetArrayBuiltin("pop");
            return fn.Invoke(this);
        }
        public void Push(JavaScriptValue value)
        {
            var fn = GetArrayBuiltin("pop");
            fn.Invoke(this, value);
        }
        public void Reverse()
        {
            var fn = GetArrayBuiltin("reverse");
            fn.Invoke(this);
        }

        public JavaScriptValue Shift()
        {
            var fn = GetArrayBuiltin("shift");
            return fn.Invoke(this);
        }
        public int Unshift(IReadOnlyList<JavaScriptValue> valuesToInsert)
        {
            var eng = GetEngine();
            var fn = GetArrayBuiltin("unshift");
            var arr = eng.BorrowArrayOfJavaScriptValue(valuesToInsert.Count + 1);
            arr[0] = this;
            var i = 1;
            foreach (var item in valuesToInsert)
            {
                arr[i++] = item;
            }
            var result = eng.Converter.ToInt32(fn.Invoke(arr));
            eng.ReleaseArrayOfJavaScriptValue(arr);
            return result;
        }
        public void Sort(JavaScriptFunction compareFunction = null)
        {
            var fn = GetArrayBuiltin("sort");
            if (compareFunction != null) fn.Invoke(this, compareFunction);
            else fn.Invoke(this);
        }
        public JavaScriptArray Splice(uint index, uint numberToRemove, IReadOnlyList<JavaScriptValue> valuesToInsert)
        {
            if (valuesToInsert == null)
                valuesToInsert = Array.Empty<JavaScriptValue>();

            var eng = GetEngine();

            var engine = GetEngine();
            var args = engine.BorrowArrayOfJavaScriptValue(3 + valuesToInsert.Count);
            args[0] = this;
            args[1] = eng.Converter.FromDouble(index);
            args[2] = eng.Converter.FromDouble(numberToRemove);
            for (int i = 0; i < valuesToInsert.Count; i++)
            {
                args[3 + i] = valuesToInsert[i];
            }

            var fn = GetArrayBuiltin("splice");
            var r = fn.Invoke(args) as JavaScriptArray;
            engine.ReleaseArrayOfJavaScriptValue(args);
            return r;
        }
        public JavaScriptArray Concat(JavaScriptArray itemsToConcatenate)
        {
            var fn = GetArrayBuiltin("concat");
            return fn.Invoke(this, itemsToConcatenate) as JavaScriptArray;
        }
        public JavaScriptArray Concat(IReadOnlyList<JavaScriptValue> itemsToConcatenate)
        {
            JavaScriptValue[] args;
            var engine = GetEngine();
         
            args = engine.BorrowArrayOfJavaScriptValue(itemsToConcatenate.Count + 1);
            var i = 1;
            foreach (var item in itemsToConcatenate)
                args[i++] = item;
            
            args[0] = this;

            var fn = GetArrayBuiltin("concat");
            var result = fn.Invoke(args) as JavaScriptArray;
            engine.ReleaseArrayOfJavaScriptValue(args);
            return result;
        }
        public string Join(string separator = "")
        {
            var eng = GetEngine();
            
            var fn = GetArrayBuiltin("join");

            if (!string.IsNullOrEmpty(separator))
                eng.Converter.ToString(fn.Invoke(this, eng.Converter.FromString(separator)));
            
            return eng.Converter.ToString(fn.Invoke(this));
        }
        public JavaScriptArray Slice(int beginning)
        {
            return GetArrayBuiltin("slice").Invoke(this, GetEngine().Converter.FromInt32(beginning)) as JavaScriptArray;
        }
        public JavaScriptArray Slice(int beginning, int end)
        {
            return GetArrayBuiltin("slice").Invoke(this, GetEngine().Converter.FromInt32(beginning), GetEngine().Converter.FromInt32(end)) as JavaScriptArray;
        }
        public int IndexOf(JavaScriptValue valueToFind)
        {
            return GetEngine().Converter.ToInt32(GetArrayBuiltin("indexOf").Invoke(this, valueToFind));
        }
        public int IndexOf(JavaScriptValue valueToFind, int startIndex)
        {
            var eng = GetEngine();
            

            return eng.Converter.ToInt32(GetArrayBuiltin("indexOf").Invoke(this, valueToFind, eng.Converter.FromInt32(startIndex)));
        }
        public int LastIndexOf(JavaScriptValue valueToFind)
        {
            var eng = GetEngine();

            return eng.Converter.ToInt32(GetArrayBuiltin("lastIndexOf").Invoke(this, valueToFind));
        }
        public int LastIndexOf(JavaScriptValue valueToFind, int lastIndex)
        {
            var eng = GetEngine();


            return eng.Converter.ToInt32(GetArrayBuiltin("lastIndexOf").Invoke(this, valueToFind, eng.Converter.FromInt32(lastIndex)));
        }

        public void ForEach(JavaScriptFunction callee)
        {
            if (callee == null)
                throw new ArgumentNullException(nameof(callee));

            GetArrayBuiltin("forEach").Invoke(this, callee);
        }
        public bool Every(JavaScriptFunction predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));
            
            return GetEngine().Converter.ToBoolean(GetArrayBuiltin("every").Invoke(this, predicate));
        }
        public bool Some(JavaScriptFunction predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            return GetEngine().Converter.ToBoolean(GetArrayBuiltin("some").Invoke(this, predicate));
        }
        public JavaScriptArray Filter(JavaScriptFunction predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));
            return GetArrayBuiltin("filter").Invoke(this, predicate) as JavaScriptArray;
        }
        public JavaScriptArray Map(JavaScriptFunction converter)
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));


            return GetArrayBuiltin("map").Invoke(this, converter) as JavaScriptArray;
        }
        public JavaScriptValue Reduce(JavaScriptFunction aggregator)
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return GetArrayBuiltin("reduce").Invoke(this, aggregator);
        }
        public JavaScriptValue Reduce(JavaScriptFunction aggregator, JavaScriptValue initialValue)
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));


            return GetArrayBuiltin("reduce").Invoke(this, aggregator, initialValue);
        }
        public JavaScriptValue ReduceRight(JavaScriptFunction aggregator)
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return GetArrayBuiltin("reduceRight").Invoke(this, aggregator);
        }
        public JavaScriptValue ReduceRight(JavaScriptFunction aggregator, JavaScriptValue initialValue)
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return GetArrayBuiltin("reduceRight").Invoke(this, aggregator, initialValue);
        }

        public IEnumerator<JavaScriptValue> GetEnumerator()
        {
            var len = this.Length;
            for (int i = 0; i < len; i++)
            {
                yield return GetAt(i);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
