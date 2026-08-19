using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SwissForge.Esprit
{
    /// <summary>
    /// A forgiving late-bound wrapper around a COM object.
    /// <para>
    /// SwissForge talks to ESPRIT through IDispatch reflection rather than a generated
    /// interop assembly. That is a deliberate trade, and worth understanding before changing it:
    /// </para>
    /// <list type="bullet">
    /// <item><b>It builds without the type libraries.</b> A generated interop assembly pins the
    /// build to one ESPRIT version's exact type names. Late binding compiles against nothing,
    /// so the same binary loads on the version you have.</item>
    /// <item><b>It survives renames.</b> <see cref="GetAny"/> takes a list of candidate member
    /// names and uses the first that exists, which absorbs the small naming differences between
    /// ESPRIT generations without a code change.</item>
    /// <item><b>It fails legibly.</b> A missing member produces "ESPRIT has no member 'Foo' on
    /// type X; it does have: ..." rather than a TypeLoadException at assembly load.</item>
    /// </list>
    /// <para>
    /// The cost is no compile-time checking and slower calls. Neither matters at the volume of
    /// calls this add-in makes, and the version tolerance is worth considerably more.
    /// </para>
    /// </summary>
    public sealed class Com
    {
        private readonly object _target;

        public object Raw => _target;
        public bool IsNull => _target == null;

        public Com(object target) { _target = target; }

        public static Com Wrap(object o) => new Com(o);

        /// <summary>Attaches to a running COM server by ProgID, or returns null if none is running.</summary>
        public static Com GetRunningInstance(string progId, out string error)
        {
            error = null;
            try
            {
                var instance = Marshal.GetActiveObject(progId);
                return new Com(instance);
            }
            catch (COMException ex)
            {
                error = $"No running '{progId}' found in the Running Object Table " +
                        $"(0x{ex.ErrorCode:X8}). Start ESPRIT first, and make sure it is running " +
                        "as the same Windows user as this process.";
                return null;
            }
            catch (Exception ex)
            {
                error = $"Could not attach to '{progId}': {ex.Message}";
                return null;
            }
        }

        // ------------------------------------------------------------------ member access

        /// <summary>Reads a property. Throws a legible exception when the member does not exist.</summary>
        public Com Get(string name) => new Com(Invoke(name, BindingFlags.GetProperty, null));

        /// <summary>Reads a property, returning a null wrapper instead of throwing when absent.</summary>
        public Com TryGet(string name)
        {
            try { return Get(name); }
            catch { return new Com(null); }
        }

        /// <summary>
        /// Reads the first of several candidate property names that actually exists.
        /// This is what absorbs naming differences between ESPRIT versions.
        /// </summary>
        public Com GetAny(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGet(name);
                if (!value.IsNull) return value;
            }
            return new Com(null);
        }

        /// <summary>Sets a property.</summary>
        public void Set(string name, object value) =>
            Invoke(name, BindingFlags.SetProperty, new[] { value });

        /// <summary>Sets the first candidate property that exists. Returns the name used, or null.</summary>
        public string SetAny(object value, params string[] names)
        {
            foreach (var name in names)
            {
                try { Set(name, value); return name; }
                catch { /* try the next candidate */ }
            }
            return null;
        }

        /// <summary>Calls a method.</summary>
        public Com Call(string name, params object[] args) =>
            new Com(Invoke(name, BindingFlags.InvokeMethod, args));

        public Com TryCall(string name, params object[] args)
        {
            try { return Call(name, args); }
            catch { return new Com(null); }
        }

        /// <summary>Indexer access, for collections exposed as Item(i).</summary>
        public Com Item(object index)
        {
            var direct = TryCall("Item", index);
            if (!direct.IsNull) return direct;
            return new Com(Invoke("Item", BindingFlags.GetProperty, new[] { index }));
        }

        /// <summary>True when the object exposes the named member at all.</summary>
        public bool Has(string name)
        {
            if (_target == null) return false;
            try
            {
                var type = _target.GetType();
                if (!type.IsCOMObject) return type.GetMember(name).Length > 0;
                // For a COM object the only reliable test is to ask.
                Invoke(name, BindingFlags.GetProperty, null);
                return true;
            }
            catch { return false; }
        }

        private object Invoke(string name, BindingFlags flags, object[] args)
        {
            if (_target == null)
                throw new InvalidOperationException(
                    $"Tried to access '{name}' on a null COM object. The parent lookup returned nothing — " +
                    "usually that means no ESPRIT document is open, or the member above this one has a " +
                    "different name in your version. Run the SwissForge probe to see the real object model.");

            try
            {
                return _target.GetType().InvokeMember(
                    name, flags, null, _target, args, CultureInfo.InvariantCulture);
            }
            catch (MissingMemberException)
            {
                throw new MissingMemberException(Describe(name));
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // Unwrap so the caller sees ESPRIT's own complaint, not a reflection wrapper.
                throw new InvalidOperationException(
                    $"ESPRIT rejected '{name}': {ex.InnerException.Message}", ex.InnerException);
            }
            catch (COMException ex) when ((uint)ex.ErrorCode == 0x80020006)   // DISP_E_UNKNOWNNAME
            {
                throw new MissingMemberException(Describe(name));
            }
        }

        private string Describe(string name)
        {
            var typeName = _target?.GetType().FullName ?? "unknown";
            var known = KnownMemberNames().Take(40).ToList();
            return $"ESPRIT has no member '{name}' on {typeName}." +
                   (known.Count > 0
                       ? " Members it does expose include: " + string.Join(", ", known) + "."
                       : " The object exposes no discoverable type information; run the SwissForge probe.");
        }

        /// <summary>Member names discovered from the object's type info, when it provides any.</summary>
        public IEnumerable<string> KnownMemberNames()
        {
            if (_target == null) yield break;

            IEnumerable<string> names;
            try
            {
                names = _target.GetType()
                    .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Select(m => m.Name)
                    .Distinct()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { yield break; }

            foreach (var n in names) yield return n;
        }

        // ------------------------------------------------------------------ conversions

        public string AsString(string fallback = "")
        {
            if (_target == null) return fallback;
            try { return Convert.ToString(_target, CultureInfo.InvariantCulture) ?? fallback; }
            catch { return fallback; }
        }

        public double AsDouble(double fallback = 0)
        {
            if (_target == null) return fallback;
            try { return Convert.ToDouble(_target, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public int AsInt(int fallback = 0)
        {
            if (_target == null) return fallback;
            try { return Convert.ToInt32(_target, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public bool AsBool(bool fallback = false)
        {
            if (_target == null) return fallback;
            try { return Convert.ToBoolean(_target, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        /// <summary>
        /// Enumerates a COM collection. Tries IEnumerable first, then falls back to
        /// Count plus Item(i), and tries both 0-based and 1-based indexing because COM
        /// collections disagree about this more often than they should.
        /// </summary>
        public IEnumerable<Com> Enumerate()
        {
            if (_target == null) yield break;

            if (_target is IEnumerable enumerable)
            {
                foreach (var item in enumerable) yield return new Com(item);
                yield break;
            }

            int count;
            try { count = GetAny("Count", "Length").AsInt(-1); }
            catch { yield break; }

            if (count <= 0) yield break;

            // Probe the base index once rather than guessing per item.
            int baseIndex = 0;
            bool zeroWorks;
            try { zeroWorks = !Item(0).IsNull; }
            catch { zeroWorks = false; }
            if (!zeroWorks) baseIndex = 1;

            for (int i = 0; i < count; i++)
            {
                Com item;
                try { item = Item(i + baseIndex); }
                catch { continue; }
                if (!item.IsNull) yield return item;
            }
        }

        /// <summary>Releases the underlying RCW. Safe to call on a non-COM object.</summary>
        public void Release()
        {
            if (_target != null && Marshal.IsComObject(_target))
            {
                try { Marshal.ReleaseComObject(_target); } catch { /* already released */ }
            }
        }
    }
}
