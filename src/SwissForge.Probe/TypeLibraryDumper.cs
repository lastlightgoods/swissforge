using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SwissForge.Core.Json;

// .NET Framework declares INVOKEKIND in both System.Runtime.InteropServices and its
// .ComTypes child; the former is the obsolete pre-ComTypes copy, and having both
// namespaces imported above makes the bare name ambiguous (CS0104). Everything else
// here reads the ComTypes descriptors, so name that one explicitly.
using INVOKEKIND = System.Runtime.InteropServices.ComTypes.INVOKEKIND;

namespace SwissForge.Probe
{
    /// <summary>
    /// Minimal IDispatch declaration, used only to reach GetTypeInfo.
    /// <para>
    /// The later vtable entries (GetIDsOfNames, Invoke) are deliberately omitted. Because the
    /// interface is declared as IsIUnknown, slots are assigned in declaration order after the
    /// three IUnknown entries, so GetTypeInfoCount and GetTypeInfo land correctly at slots 3
    /// and 4. Omitting the tail is safe as long as nothing calls it, and nothing here does.
    /// </para>
    /// </summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDispatchLite
    {
        void GetTypeInfoCount(out int count);
        void GetTypeInfo(int index, int lcid, out ITypeInfo typeInfo);
    }

    /// <summary>
    /// Reads a COM object's type library and writes out every interface, method, property and
    /// parameter it declares.
    /// <para>
    /// This is what closes the loop on an API whose documentation is not publicly retrievable.
    /// Rather than guessing member names and shipping code that fails at runtime, the probe
    /// reports what your installation actually exposes, and the adapter's member map is then
    /// corrected against fact.
    /// </para>
    /// </summary>
    public static class TypeLibraryDumper
    {
        /// <summary>Dumps the type info attached to a live COM object.</summary>
        public static JsonValue DumpObject(object comObject, bool includeWholeLibrary, int maxTypes)
        {
            var root = JsonValue.Obj();
            root.Set("schema", "swissforge.probe/1");

            if (comObject == null)
            {
                root.Set("error", "No COM object supplied.");
                return root;
            }

            ITypeInfo typeInfo = null;
            try
            {
                var dispatch = comObject as IDispatchLite;
                if (dispatch == null)
                {
                    root.Set("error",
                        "The object does not implement IDispatch, so no type information can be read. " +
                        "This usually means it is a custom-interface-only object.");
                    return root;
                }

                dispatch.GetTypeInfo(0, 0, out typeInfo);
            }
            catch (Exception ex)
            {
                root.Set("error", "GetTypeInfo failed: " + ex.Message);
                return root;
            }

            if (typeInfo == null)
            {
                root.Set("error", "The object returned no ITypeInfo.");
                return root;
            }

            root["primaryType"] = DumpTypeInfo(typeInfo, includeMembers: true);

            if (includeWholeLibrary)
            {
                try
                {
                    typeInfo.GetContainingTypeLib(out var typeLib, out var indexInLib);
                    root.Set("indexInLibrary", indexInLib);
                    root["library"] = DumpLibrary(typeLib, maxTypes);
                }
                catch (Exception ex)
                {
                    root.Set("libraryError", "Could not read the containing type library: " + ex.Message);
                }
            }

            return root;
        }

        /// <summary>Dumps every type in a type library.</summary>
        public static JsonValue DumpLibrary(ITypeLib typeLib, int maxTypes)
        {
            var lib = JsonValue.Obj();
            if (typeLib == null) { lib.Set("error", "no type library"); return lib; }

            try
            {
                typeLib.GetDocumentation(-1, out var libName, out var libDoc, out _, out var helpFile);
                lib.Set("name", libName);
                lib.Set("documentation", libDoc);
                lib.Set("helpFile", helpFile);
            }
            catch { /* naming the library is a nicety */ }

            int count = 0;
            try { count = typeLib.GetTypeInfoCount(); }
            catch (Exception ex) { lib.Set("error", ex.Message); return lib; }

            lib.Set("typeCount", count);

            int limit = maxTypes > 0 ? Math.Min(count, maxTypes) : count;
            if (limit < count)
                lib.Set("note", $"Showing the first {limit} of {count} types. " +
                                "Re-run with --max 0 for the complete library.");

            var types = JsonValue.Arr();
            for (int i = 0; i < limit; i++)
            {
                try
                {
                    typeLib.GetTypeInfo(i, out var ti);
                    types.Add(DumpTypeInfo(ti, includeMembers: true));
                    if (ti != null) Marshal.ReleaseComObject(ti);
                }
                catch (Exception ex)
                {
                    types.Add(JsonValue.Obj().Set("index", i).Set("error", ex.Message));
                }
            }

            lib["types"] = types;
            return lib;
        }

        /// <summary>Dumps one interface, coclass, enum or alias.</summary>
        public static JsonValue DumpTypeInfo(ITypeInfo typeInfo, bool includeMembers)
        {
            var result = JsonValue.Obj();
            if (typeInfo == null) { result.Set("error", "null type info"); return result; }

            try
            {
                typeInfo.GetDocumentation(-1, out var name, out var doc, out _, out _);
                result.Set("name", name);
                if (!string.IsNullOrEmpty(doc)) result.Set("documentation", doc);
            }
            catch { /* keep going; the members matter more than the label */ }

            IntPtr pAttr = IntPtr.Zero;
            try
            {
                typeInfo.GetTypeAttr(out pAttr);
                if (pAttr == IntPtr.Zero) { result.Set("error", "no type attributes"); return result; }

                var attr = (TYPEATTR)Marshal.PtrToStructure(pAttr, typeof(TYPEATTR));

                result.Set("guid", attr.guid.ToString());
                result.Set("kind", attr.typekind.ToString());
                result.Set("version", $"{attr.wMajorVerNum}.{attr.wMinorVerNum}");
                result.Set("functionCount", attr.cFuncs);
                result.Set("variableCount", attr.cVars);
                result.Set("implementedTypeCount", attr.cImplTypes);

                if (!includeMembers) return result;

                // --- inherited / implemented interfaces
                var implemented = JsonValue.Arr();
                for (int i = 0; i < attr.cImplTypes; i++)
                {
                    try
                    {
                        typeInfo.GetRefTypeOfImplType(i, out var href);
                        typeInfo.GetRefTypeInfo(href, out var parent);
                        if (parent == null) continue;
                        parent.GetDocumentation(-1, out var parentName, out _, out _, out _);
                        implemented.Add(JsonValue.Str(parentName));
                        Marshal.ReleaseComObject(parent);
                    }
                    catch { /* an unresolvable parent is not fatal */ }
                }
                if (implemented.Count > 0) result["implements"] = implemented;

                // --- methods and properties
                var members = JsonValue.Arr();
                for (int i = 0; i < attr.cFuncs; i++)
                {
                    var member = DumpFunction(typeInfo, i);
                    if (member != null) members.Add(member);
                }
                result["members"] = members;

                // --- fields and enum constants
                var variables = JsonValue.Arr();
                for (int i = 0; i < attr.cVars; i++)
                {
                    var v = DumpVariable(typeInfo, i);
                    if (v != null) variables.Add(v);
                }
                if (variables.Count > 0) result["variables"] = variables;
            }
            catch (Exception ex)
            {
                result.Set("error", ex.Message);
            }
            finally
            {
                if (pAttr != IntPtr.Zero)
                {
                    try { typeInfo.ReleaseTypeAttr(pAttr); } catch { /* releasing is best effort */ }
                }
            }

            return result;
        }

        private static JsonValue DumpFunction(ITypeInfo typeInfo, int index)
        {
            IntPtr pFunc = IntPtr.Zero;
            try
            {
                typeInfo.GetFuncDesc(index, out pFunc);
                if (pFunc == IntPtr.Zero) return null;

                var fd = (FUNCDESC)Marshal.PtrToStructure(pFunc, typeof(FUNCDESC));

                var member = JsonValue.Obj();

                string name = "";
                try
                {
                    typeInfo.GetDocumentation(fd.memid, out name, out var doc, out _, out _);
                    member.Set("name", name);
                    if (!string.IsNullOrEmpty(doc)) member.Set("documentation", doc);
                }
                catch { member.Set("name", "memid_" + fd.memid); }

                member.Set("memberId", fd.memid);
                member.Set("kind", DescribeInvokeKind(fd.invkind));
                member.Set("returns", VarTypeName(fd.elemdescFunc.tdesc.vt));
                member.Set("parameterCount", fd.cParams);
                member.Set("optionalParameterCount", fd.cParamsOpt);

                // --- parameter names come back with the member name at index 0
                var paramNames = new string[fd.cParams + 1];
                int fetched = 0;
                try { typeInfo.GetNames(fd.memid, paramNames, paramNames.Length, out fetched); }
                catch { fetched = 0; }

                var parameters = JsonValue.Arr();
                for (int p = 0; p < fd.cParams; p++)
                {
                    var pj = JsonValue.Obj();
                    pj.Set("name", (p + 1) < fetched && paramNames[p + 1] != null
                        ? paramNames[p + 1]
                        : "arg" + (p + 1));

                    try
                    {
                        var elemSize = Marshal.SizeOf(typeof(ELEMDESC));
                        var pElem = new IntPtr(fd.lprgelemdescParam.ToInt64() + p * elemSize);
                        var elem = (ELEMDESC)Marshal.PtrToStructure(pElem, typeof(ELEMDESC));
                        pj.Set("type", VarTypeName(elem.tdesc.vt));
                    }
                    catch
                    {
                        pj.Set("type", "unknown");
                    }

                    parameters.Add(pj);
                }
                member["parameters"] = parameters;

                // A one-line signature makes the dump skimmable by a human.
                member.Set("signature", BuildSignature(member));
                return member;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pFunc != IntPtr.Zero)
                {
                    try { typeInfo.ReleaseFuncDesc(pFunc); } catch { /* best effort */ }
                }
            }
        }

        private static JsonValue DumpVariable(ITypeInfo typeInfo, int index)
        {
            IntPtr pVar = IntPtr.Zero;
            try
            {
                typeInfo.GetVarDesc(index, out pVar);
                if (pVar == IntPtr.Zero) return null;

                var vd = (VARDESC)Marshal.PtrToStructure(pVar, typeof(VARDESC));

                var v = JsonValue.Obj();
                try
                {
                    typeInfo.GetDocumentation(vd.memid, out var name, out var doc, out _, out _);
                    v.Set("name", name);
                    if (!string.IsNullOrEmpty(doc)) v.Set("documentation", doc);
                }
                catch { v.Set("name", "var_" + vd.memid); }

                v.Set("memberId", vd.memid);
                v.Set("type", VarTypeName(vd.elemdescVar.tdesc.vt));
                v.Set("kind", vd.varkind.ToString());
                return v;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pVar != IntPtr.Zero)
                {
                    try { typeInfo.ReleaseVarDesc(pVar); } catch { /* best effort */ }
                }
            }
        }

        private static string BuildSignature(JsonValue member)
        {
            var kind = member["kind"].AsString("");
            var name = member["name"].AsString("");
            var returns = member["returns"].AsString("");

            var args = string.Join(", ",
                member["parameters"].Select(p => p["type"].AsString("") + " " + p["name"].AsString("")));

            switch (kind)
            {
                case "get": return $"{returns} {name} {{ get; }}";
                case "put":
                case "putref": return $"{name} {{ set; }}   ({args})";
                default: return $"{returns} {name}({args})";
            }
        }

        private static string DescribeInvokeKind(INVOKEKIND kind)
        {
            switch (kind)
            {
                case INVOKEKIND.INVOKE_FUNC: return "method";
                case INVOKEKIND.INVOKE_PROPERTYGET: return "get";
                case INVOKEKIND.INVOKE_PROPERTYPUT: return "put";
                case INVOKEKIND.INVOKE_PROPERTYPUTREF: return "putref";
                default: return kind.ToString();
            }
        }

        /// <summary>Human-readable name for a VARTYPE.</summary>
        private static string VarTypeName(short vt)
        {
            switch ((VarEnum)(vt & 0x0FFF))
            {
                case VarEnum.VT_EMPTY: return "void";
                case VarEnum.VT_NULL: return "null";
                case VarEnum.VT_I2: return "short";
                case VarEnum.VT_I4: return "int";
                case VarEnum.VT_R4: return "float";
                case VarEnum.VT_R8: return "double";
                case VarEnum.VT_CY: return "currency";
                case VarEnum.VT_DATE: return "DateTime";
                case VarEnum.VT_BSTR: return "string";
                case VarEnum.VT_DISPATCH: return "IDispatch";
                case VarEnum.VT_ERROR: return "HRESULT";
                case VarEnum.VT_BOOL: return "bool";
                case VarEnum.VT_VARIANT: return "variant";
                case VarEnum.VT_UNKNOWN: return "IUnknown";
                case VarEnum.VT_DECIMAL: return "decimal";
                case VarEnum.VT_I1: return "sbyte";
                case VarEnum.VT_UI1: return "byte";
                case VarEnum.VT_UI2: return "ushort";
                case VarEnum.VT_UI4: return "uint";
                case VarEnum.VT_I8: return "long";
                case VarEnum.VT_UI8: return "ulong";
                case VarEnum.VT_INT: return "int";
                case VarEnum.VT_UINT: return "uint";
                case VarEnum.VT_VOID: return "void";
                case VarEnum.VT_HRESULT: return "HRESULT";
                case VarEnum.VT_PTR: return "ptr";
                case VarEnum.VT_SAFEARRAY: return "safearray";
                case VarEnum.VT_CARRAY: return "array";
                case VarEnum.VT_USERDEFINED: return "userdefined";
                case VarEnum.VT_LPSTR: return "lpstr";
                case VarEnum.VT_LPWSTR: return "lpwstr";
                default: return "vt" + vt.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
