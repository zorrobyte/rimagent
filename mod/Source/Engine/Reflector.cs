using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimBridge.Server;
using RimWorld;
using Verse;

namespace RimBridge.Engine
{
    /// <summary>Walks engine paths over the live object graph via reflection.</summary>
    public static class Reflector
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        static readonly string[] DeniedNamespaces = { "System.IO", "System.Diagnostics", "System.Net", "System.Threading", "System.Runtime", "System.Security", "Microsoft." };
        static readonly HashSet<string> DeniedTypes = new HashSet<string> { "System.Environment", "System.AppDomain", "System.Activator", "System.GC", "UnityEngine.Application", "Verse.Root", "Verse.Root_Play", "Verse.Root_Entry", "Verse.GenFilePaths", "Verse.GenCommandLine", "Verse.Prefs", "Verse.ModLister", "Verse.LoadedModManager", "Verse.DirectXmlLoader", "Verse.ScribeSaver", "Verse.ScribeLoader" };
        static readonly HashSet<string> DeniedMethods = new HashSet<string> { "Quit", "Exit", "Kill", "Shutdown", "Restart", "DeleteFile", "Delete", "WriteAllText", "WriteAllBytes" };

        public static void CheckAllowed(Type t, string? member = null)
        {
            string full = t.FullName ?? t.Name;
            if (DeniedTypes.Contains(full)) throw new RpcError($"access to {full} is not allowed over the bridge");
            foreach (var ns in DeniedNamespaces) if (full.StartsWith(ns)) throw new RpcError($"access to {full} is not allowed over the bridge");
            if (member != null && DeniedMethods.Contains(member) && !typeof(Thing).IsAssignableFrom(t) && !typeof(Pawn).IsAssignableFrom(t))
                throw new RpcError($"{full}.{member} is not allowed over the bridge");
        }

        /// <summary>Resolve a path to a value (instance object) or a Type (static context).</summary>
        public static object? Resolve(string path)
        {
            var pp = PathParser.Parse(path);
            var (cur, isType, extraSegments) = ResolveRoot(pp);
            var segs = new List<PathSegment>();
            segs.AddRange(extraSegments);
            segs.AddRange(pp.Segments);
            foreach (var seg in segs)
            {
                (cur, isType) = Step(cur, isType, seg);
            }
            return cur;
        }

        /// <summary>Resolve everything but the last segment; return (parent, isType, lastSegment).</summary>
        public static (object? parent, bool isType, PathSegment last) ResolveParent(string path)
        {
            var pp = PathParser.Parse(path);
            var (cur, isType, extraSegments) = ResolveRoot(pp);
            var segs = new List<PathSegment>();
            segs.AddRange(extraSegments);
            segs.AddRange(pp.Segments);
            if (segs.Count == 0) throw new RpcError("path must end in a member name");
            for (int i = 0; i < segs.Count - 1; i++) (cur, isType) = Step(cur, isType, segs[i]);
            return (cur, isType, segs[segs.Count - 1]);
        }

        static (object? value, bool isType, List<PathSegment> extra) ResolveRoot(ParsedPath pp)
        {
            object? cur;
            bool isType = false;
            var extra = new List<PathSegment>();
            string a0 = pp.RootArgs.Count > 0 ? pp.RootArgs[0] : "";
            switch (pp.RootTag)
            {
                case "":
                    switch (a0)
                    {
                        case "Find": cur = typeof(Find); isType = true; break;
                        case "Current": cur = typeof(Current); isType = true; break;
                        case "Map": cur = Lookup.Map(); break;
                        case "World": cur = Find.World; break;
                        case "Game": cur = Current.Game; break;
                        case "Player": cur = Faction.OfPlayer; break;
                        default:
                            var t = Lookup.TypeOrNull(a0) ?? throw new RpcError($"unknown root '{a0}' (use Find, Current, Map, World, Game, Player, Thing:<id>, Pawn:<name>, Def:<DefType>:<defName>, Type:<Full.Name>)");
                            cur = t; isType = true; break;
                    }
                    break;
                case "Thing": cur = Lookup.Thing(a0); break;
                case "Pawn": cur = Lookup.Pawn(a0); break;
                case "Zone": cur = Lookup.ZoneOrNull(a0) ?? throw new RpcError($"no zone '{a0}'"); break;
                case "Area": cur = Lookup.AreaOrNull(a0) ?? throw new RpcError($"no area '{a0}'"); break;
                case "Faction": cur = Lookup.FactionOrNull(a0) ?? throw new RpcError($"no faction '{a0}'"); break;
                case "Room": cur = Lookup.RoomOrNull(a0) ?? throw new RpcError($"no room '{a0}'"); break;
                case "Def":
                {
                    if (pp.RootArgs.Count < 2) throw new RpcError("Def root needs Def:<DefType>:<defName>");
                    var dt = Lookup.DefType(pp.RootArgs[0]);
                    cur = Lookup.DefOrNull(dt, pp.RootArgs[1]) ?? throw new RpcError($"no {dt.Name} named '{pp.RootArgs[1]}'");
                    break;
                }
                case "Type":
                {
                    // Longest prefix that is a type; remainder becomes member segments.
                    var parts = a0.Split('.');
                    Type? found = null; int used = 0;
                    for (int n = parts.Length; n >= 1; n--)
                    {
                        var candidate = string.Join(".", parts.Take(n));
                        found = Lookup.TypeOrNull(candidate);
                        if (found != null) { used = n; break; }
                    }
                    if (found == null) throw new RpcError($"unknown type '{a0}'");
                    cur = found; isType = true;
                    for (int i = used; i < parts.Length; i++) extra.Add(new PathSegment { Name = parts[i] });
                    break;
                }
                default: throw new RpcError($"unknown root tag '{pp.RootTag}'");
            }
            // Root call / indexers apply to the last synthesized segment if any, else to the root value.
            if (extra.Count > 0)
            {
                extra[extra.Count - 1].IsCall = pp.RootIsCall;
                extra[extra.Count - 1].Indexers.AddRange(pp.RootIndexers);
            }
            else
            {
                if (pp.RootIsCall) throw new RpcError("cannot call the root");
                foreach (var ix in pp.RootIndexers) cur = Index(cur, ix);
            }
            return (cur, isType, extra);
        }

        static (object? value, bool isType) Step(object? cur, bool isType, PathSegment seg)
        {
            if (cur == null) throw new RpcError($"cannot access '{seg.Name}' on null");
            Type type = isType ? (Type)cur : cur.GetType();
            CheckAllowed(type, seg.Name);
            object? target = isType ? null : cur;
            object? next;
            bool nextIsType = false;
            if (seg.IsCall)
            {
                var m = FindMethod(type, seg.Name, new JArray(), out var args) ?? throw new RpcError($"{type.Name} has no parameterless method '{seg.Name}'");
                next = m.Invoke(m.IsStatic ? null : target, args);
            }
            else
            {
                var pr = type.GetProperty(seg.Name, All);
                if (pr != null && pr.GetIndexParameters().Length == 0)
                {
                    if (pr.GetGetMethod(true)?.IsStatic == false && target == null) throw new RpcError($"'{seg.Name}' is an instance property; need an instance");
                    next = pr.GetValue(target);
                }
                else
                {
                    var f = type.GetField(seg.Name, All);
                    if (f != null)
                    {
                        if (!f.IsStatic && target == null) throw new RpcError($"'{seg.Name}' is an instance field; need an instance");
                        next = f.GetValue(target);
                    }
                    else
                    {
                        // nested type?
                        var nt = type.GetNestedType(seg.Name, BindingFlags.Public | BindingFlags.NonPublic);
                        if (nt != null) { next = nt; nextIsType = true; }
                        else
                        {
                            // Method group without call syntax: allow when it has no parameters (convenience).
                            var m = FindMethod(type, seg.Name, new JArray(), out var args);
                            if (m != null) next = m.Invoke(m.IsStatic ? null : target, args);
                            else throw new RpcError($"{type.Name} has no member '{seg.Name}'. {Suggest(type, seg.Name)}");
                        }
                    }
                }
            }
            foreach (var ix in seg.Indexers) next = Index(next, ix);
            return (next, nextIsType);
        }

        static object? Index(object? o, string key)
        {
            if (o == null) throw new RpcError("cannot index null");
            if (o is IDictionary d)
            {
                foreach (DictionaryEntry kv in d)
                {
                    string ks = kv.Key switch { Def def => def.defName, Thing t => t.ThingID, _ => kv.Key?.ToString() ?? "" };
                    if (string.Equals(ks, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                }
                throw new RpcError($"key '{key}' not found");
            }
            if (int.TryParse(key, out int i))
            {
                if (o is IList list)
                {
                    if (i < 0) i += list.Count;
                    if (i < 0 || i >= list.Count) throw new RpcError($"index {key} out of range (count {list.Count})");
                    return list[i];
                }
                if (o is IEnumerable en && !(o is string))
                {
                    int n = 0;
                    foreach (var x in en) { if (n++ == i) return x; }
                    throw new RpcError($"index {key} out of range (count {n})");
                }
            }
            // Indexer property
            var type = o.GetType();
            foreach (var pr in type.GetProperties(All))
            {
                var ps = pr.GetIndexParameters();
                if (ps.Length != 1) continue;
                try { return pr.GetValue(o, new[] { Coerce.To(key, ps[0].ParameterType) }); } catch { }
            }
            // Enumerable: match by defName / ThingID / label
            if (o is IEnumerable en2 && !(o is string))
            {
                foreach (var x in en2)
                {
                    if (x == null) continue;
                    string? s = x switch { Def def => def.defName, Pawn p => p.LabelShort, Thing t => t.ThingID, Zone z => z.label, _ => null };
                    if (s != null && string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return x;
                    if (x is Pawn p2 && string.Equals(p2.ThingID, key, StringComparison.OrdinalIgnoreCase)) return x;
                    // items with a .def member (SkillRecord, Hediff, Need, Trait, Bill, Thought...)
                    var xt = x.GetType();
                    var defMember = (MemberInfo?)xt.GetField("def", All) ?? xt.GetProperty("def", All);
                    object? dv = defMember is FieldInfo fi ? fi.GetValue(x) : (defMember as PropertyInfo)?.GetValue(x);
                    if (dv is Def dd && (string.Equals(dd.defName, key, StringComparison.OrdinalIgnoreCase) || string.Equals(dd.label, key, StringComparison.OrdinalIgnoreCase))) return x;
                }
            }
            throw new RpcError($"cannot index {type.Name} with '{key}'");
        }

        static Dictionary<string, List<MethodInfo>>? _extensions;

        /// <summary>Extension methods from the game assembly (and Harmony/our own), indexed by name.</summary>
        static Dictionary<string, List<MethodInfo>> Extensions()
        {
            if (_extensions != null) return _extensions;
            var d = new Dictionary<string, List<MethodInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in new[] { typeof(Thing).Assembly })
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsSealed || !t.IsAbstract || !t.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    if (!d.TryGetValue(m.Name, out var list)) d[m.Name] = list = new List<MethodInfo>();
                    list.Add(m);
                }
            }
            return _extensions = d;
        }

        public static MethodInfo? FindMethod(Type type, string name, JArray jargs, out object?[] args)
        {
            var m = FindMethodCore(type, name, jargs, out args, null);
            return m;
        }

        /// <summary>Find an instance/static method, or an extension method taking the target as its first argument.</summary>
        public static MethodInfo? FindMethodOrExtension(Type type, string name, JArray jargs, object? target, out object?[] args, out bool isExtension)
        {
            isExtension = false;
            var m = FindMethodCore(type, name, jargs, out args, null);
            if (m != null) return m;
            if (target == null || !Extensions().TryGetValue(name, out var exts)) return null;
            foreach (var e in exts)
            {
                var ps = e.GetParameters();
                if (!ps[0].ParameterType.IsInstanceOfType(target)) continue;
                var withTarget = new JArray(jargs);
                var a = new object?[ps.Length];
                a[0] = target;
                bool ok = true;
                for (int i = 1; i < ps.Length; i++)
                {
                    int j = i - 1;
                    if (j < jargs.Count) { try { a[i] = Coerce.To(jargs[j], ps[i].ParameterType); } catch { ok = false; break; } }
                    else if (ps[i].HasDefaultValue) a[i] = ps[i].DefaultValue;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                args = a; isExtension = true;
                return e;
            }
            return null;
        }

        static MethodInfo? FindMethodCore(Type type, string name, JArray jargs, out object?[] args, object? _)
        {
            args = Array.Empty<object?>();
            var candidates = type.GetMethods(All).Where(m => string.Equals(m.Name, name, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) candidates = type.GetMethods(All).Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            // Prefer exact arg count, then methods with optional params.
            foreach (var m in candidates.OrderBy(m => Math.Abs(m.GetParameters().Length - jargs.Count)))
            {
                if (m.ContainsGenericParameters) continue;
                var ps = m.GetParameters();
                if (jargs.Count > ps.Length) continue;
                var a = new object?[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i < jargs.Count)
                    {
                        try { a[i] = Coerce.To(jargs[i], ps[i].ParameterType); }
                        catch { ok = false; break; }
                    }
                    else if (ps[i].HasDefaultValue) a[i] = ps[i].DefaultValue;
                    else if (ps[i].IsOut || ps[i].ParameterType.IsByRef) a[i] = null;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                args = a;
                return m;
            }
            return null;
        }

        // ---- RPC surface ----

        [Rpc("engine.get", "{path, depth?: 2} read any engine value. Roots: Find, Current, Map, World, Game, Player, Thing:<id>, Pawn:<name|id>, Def:<DefType>:<defName>, Type:<Full.Name>, Zone:<label>, Area:<label>, Faction:<name>. Segments: .member, .method(), [index|key|defName]")]
        public static JToken Get(JObject p)
        {
            var v = Resolve(P.Str(p, "path"));
            int depth = P.Int(p, "depth", 2);
            if (v is Type t) return new JObject { ["$static"] = t.FullName, ["members"] = MembersOf(t, true) };
            return Render.Value(v, depth, top: true);
        }

        [Rpc("engine.set", "{path, value} assign a field/property (value coerced to the member type)")]
        public static JToken Set(JObject p)
        {
            var (parent, isType, last) = ResolveParent(P.Str(p, "path"));
            if (last.IsCall || last.Indexers.Count > 0) throw new RpcError("engine.set target must be a plain member");
            if (parent == null) throw new RpcError("parent is null");
            var type = isType ? (Type)parent : parent.GetType();
            CheckAllowed(type, last.Name);
            var target = isType ? null : parent;
            var f = type.GetField(last.Name, All);
            if (f != null) { f.SetValue(target, Coerce.To(p["value"], f.FieldType)); return Render.Value(f.GetValue(target), 1); }
            var pr = type.GetProperty(last.Name, All);
            if (pr != null && pr.CanWrite) { pr.SetValue(target, Coerce.To(p["value"], pr.PropertyType)); return Render.Value(pr.GetValue(target), 1); }
            throw new RpcError($"{type.Name} has no writable member '{last.Name}'");
        }

        [Rpc("engine.call", "{path, args?: [...], depth?: 2} invoke a method; args are coerced to parameter types (cells as [x,z], things by id, defs by defName, enums by name)")]
        public static JToken Call(JObject p)
        {
            var (parent, isType, last) = ResolveParent(P.Str(p, "path"));
            if (parent == null) throw new RpcError("parent is null");
            var type = isType ? (Type)parent : parent.GetType();
            CheckAllowed(type, last.Name);
            var jargs = P.Arr(p, "args") ?? new JArray();
            var m = FindMethodOrExtension(type, last.Name, jargs, isType ? null : parent, out var args, out bool isExt);
            if (m == null)
            {
                var sigs = type.GetMethods(All).Where(x => x.Name.Equals(last.Name, StringComparison.OrdinalIgnoreCase)).Select(Signature).ToList();
                if (Extensions().TryGetValue(last.Name, out var exts)) sigs.AddRange(exts.Where(e => e.GetParameters()[0].ParameterType.IsAssignableFrom(type)).Select(e => "ext " + Signature(e)));
                if (sigs.Count == 0) throw new RpcError($"{type.Name} has no method '{last.Name}' (instance, static or extension). {Suggest(type, last.Name)}");
                throw new RpcError($"no overload of {type.Name}.{last.Name} accepts these {jargs.Count} args. Overloads: " + string.Join(" | ", sigs));
            }
            object? result;
            try { result = m.Invoke((m.IsStatic || isExt) ? null : parent, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw new RpcError($"{last.Name} threw {tie.InnerException.GetType().Name}: {tie.InnerException.Message}"); }
            var o = new JObject { ["result"] = m.ReturnType == typeof(void) ? "void" : Render.Value(result, P.Int(p, "depth", 2), top: true) };
            // out/ref params
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].IsOut || ps[i].ParameterType.IsByRef) o["out_" + ps[i].Name] = Render.Value(args[i], 1);
            foreach (var ix in last.Indexers) o["result"] = Render.Value(Index(result, ix), P.Int(p, "depth", 2), top: true);
            return o;
        }

        [Rpc("engine.new", "{type, args?: [...]} construct an object (e.g. IncidentParms) — or pass {type, fields:{...}} to construct and fill")]
        public static JToken New(JObject p)
        {
            var t = Lookup.TypeOrNull(P.Str(p, "type")) ?? throw new RpcError("unknown type");
            CheckAllowed(t);
            object inst;
            if (p["fields"] is JObject fields) inst = Coerce.To(fields, t)!;
            else
            {
                var jargs = P.Arr(p, "args") ?? new JArray();
                ConstructorInfo? chosen = null; object?[] cargs = Array.Empty<object?>();
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(c => Math.Abs(c.GetParameters().Length - jargs.Count)))
                {
                    var ps = c.GetParameters();
                    if (ps.Length < jargs.Count) continue;
                    var a = new object?[ps.Length]; bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i < jargs.Count) { try { a[i] = Coerce.To(jargs[i], ps[i].ParameterType); } catch { ok = false; break; } }
                        else if (ps[i].HasDefaultValue) a[i] = ps[i].DefaultValue; else { ok = false; break; }
                    }
                    if (ok) { chosen = c; cargs = a; break; }
                }
                if (chosen == null) throw new RpcError($"no constructor of {t.Name} accepts these args");
                inst = chosen.Invoke(cargs);
            }
            Handles.Store(inst, out string handle);
            return new JObject { ["handle"] = handle, ["value"] = Render.Value(inst, 1, top: true) };
        }

        [Rpc("engine.members", "{path|type} list fields, properties and method signatures of an object or type (for discovery)")]
        public static JToken Members(JObject p)
        {
            Type t; bool isStatic = false;
            if (p["type"] != null) { t = Lookup.TypeOrNull(P.Str(p, "type")) ?? throw new RpcError("unknown type"); isStatic = true; }
            else
            {
                var v = Resolve(P.Str(p, "path"));
                if (v is Type tt) { t = tt; isStatic = true; } else if (v != null) t = v.GetType(); else throw new RpcError("value is null");
            }
            return new JObject { ["type"] = t.FullName, ["base"] = t.BaseType?.FullName, ["members"] = MembersOf(t, isStatic) };
        }

        [Rpc("engine.types", "{query, limit?: 50} search type names in the game assembly (e.g. 'Designator_')")]
        public static JToken Types(JObject p)
        {
            string q = P.Str(p, "query");
            int limit = P.Int(p, "limit", 50);
            var asm = typeof(Thing).Assembly;
            var arr = new JArray();
            foreach (var t in asm.GetTypes())
            {
                if (t.FullName == null || t.FullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                arr.Add(t.FullName);
                if (arr.Count >= limit) break;
            }
            return arr;
        }

        static JObject MembersOf(Type t, bool staticOnly)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy | (staticOnly ? BindingFlags.Static : BindingFlags.Static | BindingFlags.Instance);
            var fields = new JArray(); var props = new JArray(); var methods = new JArray();
            foreach (var f in t.GetFields(flags).OrderBy(f => f.Name).Take(300)) fields.Add($"{(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}");
            foreach (var pr in t.GetProperties(flags).OrderBy(p => p.Name).Take(300)) props.Add($"{Short(pr.PropertyType)} {pr.Name}{(pr.CanWrite ? "" : " (ro)")}");
            foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name).Take(400)) methods.Add(Signature(m));
            return new JObject { ["fields"] = fields, ["properties"] = props, ["methods"] = methods };
        }

        /// <summary>Close-match member names for a typo/guess, so the model can retry without a members() call.</summary>
        public static string Suggest(Type type, string name)
        {
            var names = type.GetFields(All).Select(f => f.Name).Concat(type.GetProperties(All).Select(p => p.Name)).Concat(type.GetMethods(All).Where(m => !m.IsSpecialName).Select(m => m.Name + "()")).Distinct().ToList();
            string n = name.ToLowerInvariant();
            var close = names.Where(x => x.ToLowerInvariant().Contains(n) || n.Contains(x.ToLowerInvariant().TrimEnd('(', ')')) && x.Length > 3).Take(10).ToList();
            if (close.Count == 0) close = names.Where(x => char.IsLower(x[0]) && !x.EndsWith("()")).Take(20).ToList();
            return close.Count > 0 ? "Similar/available: " + string.Join(", ", close) + " (engine.members for the full list)" : "Try engine.members on the parent.";
        }

        public static string Signature(MethodInfo m) =>
            $"{(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? "?" : "")))})";

        static string Short(Type t)
        {
            if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
            return t.Name;
        }
    }

    /// <summary>Objects created via engine.new live here so later calls can reference them as "Handle:N".</summary>
    public static class Handles
    {
        static readonly Dictionary<string, object> Map = new Dictionary<string, object>();
        static int _n;
        public static void Store(object o, out string handle) { handle = "Handle:" + (++_n); Map[handle] = o; }
        public static object? Get(string h) => Map.TryGetValue(h, out var o) ? o : null;
    }
}
