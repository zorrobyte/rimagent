using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RimBridge.Server
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RpcAttribute : Attribute
    {
        public string Name;
        public string Doc;
        /// <summary>False for methods that are safe to run on the request thread (no Verse state).</summary>
        public bool MainThread = true;
        public RpcAttribute(string name, string doc) { Name = name; Doc = doc; }
    }

    public sealed class RpcError : Exception
    {
        public RpcError(string msg) : base(msg) { }
    }

    public static class Rpc
    {
        private sealed class Entry
        {
            public RpcAttribute Attr = null!;
            public Func<JObject, JToken?> Fn = null!;
            public string Params = "";
        }

        private static readonly Dictionary<string, Entry> Methods = new Dictionary<string, Entry>();
        public static int Count => Methods.Count;

        public static void RegisterAll() => RegisterAssembly(typeof(Rpc).Assembly);

        /// <summary>Scan one assembly for [Rpc]-attributed methods and register them. Lets another mod
        /// (loaded after RimBridge, referencing RimBridge.dll) add its own RPC methods to this same
        /// dispatcher/HTTP server without RimBridge knowing anything about it ahead of time — call this
        /// from that mod's own Mod constructor, e.g. Rpc.RegisterAssembly(typeof(MyMod).Assembly).</summary>
        public static void RegisterAssembly(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = m.GetCustomAttribute<RpcAttribute>();
                if (attr == null) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(JObject))
                {
                    BridgeLog.Error($"rpc {attr.Name}: must be (JObject) -> JToken");
                    continue;
                }
                var mm = m;
                Methods[attr.Name] = new Entry
                {
                    Attr = attr,
                    Fn = p => (JToken?)mm.Invoke(null, new object[] { p }),
                    Params = attr.Doc,
                };
            }
        }

        public static JToken Describe()
        {
            var arr = new JArray();
            foreach (var kv in Methods.OrderBy(k => k.Key))
                arr.Add(new JObject { ["method"] = kv.Key, ["doc"] = kv.Value.Attr.Doc });
            return arr;
        }

        /// <summary>Dispatch from a request thread. Returns the JSON envelope.</summary>
        public static JObject Dispatch(string method, JObject? p, int timeoutMs)
        {
            p ??= new JObject();
            if (!Methods.TryGetValue(method, out var e))
                return Fail($"unknown method '{method}'", null);
            try
            {
                JToken? result;
                if (e.Attr.MainThread)
                    result = MainThreadQueue.Run(() => e.Fn(p), timeoutMs);
                else
                    result = e.Fn(p);
                return new JObject { ["ok"] = true, ["result"] = result ?? JValue.CreateNull() };
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                return FromException(tie.InnerException);
            }
            catch (Exception ex)
            {
                return FromException(ex);
            }
        }

        private static JObject FromException(Exception ex)
        {
            if (ex is TargetInvocationException t && t.InnerException != null) ex = t.InnerException;
            if (ex is RpcError) return Fail(ex.Message, null);
            return Fail($"{ex.GetType().Name}: {ex.Message}", ex.StackTrace);
        }

        private static JObject Fail(string msg, string? trace)
        {
            var o = new JObject { ["ok"] = false, ["error"] = msg };
            if (trace != null) o["trace"] = trace;
            return o;
        }
    }

    /// <summary>Parameter helpers. Throw RpcError with a readable message on bad input.</summary>
    public static class P
    {
        public static string Str(JObject p, string key, string? def = null)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                if (def != null) return def;
                throw new RpcError($"missing param '{key}'");
            }
            return t.Type == JTokenType.String ? (string)t! : t.ToString();
        }

        public static string? OptStr(JObject p, string key) => p[key] is { Type: not JTokenType.Null } t ? (t.Type == JTokenType.String ? (string)t! : t.ToString()) : null;

        public static int Int(JObject p, string key, int? def = null)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                if (def.HasValue) return def.Value;
                throw new RpcError($"missing param '{key}'");
            }
            try { return (int)t; } catch { throw new RpcError($"param '{key}' must be an integer"); }
        }

        public static float Float(JObject p, string key, float? def = null)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                if (def.HasValue) return def.Value;
                throw new RpcError($"missing param '{key}'");
            }
            try { return (float)t; } catch { throw new RpcError($"param '{key}' must be a number"); }
        }

        public static bool Bool(JObject p, string key, bool def)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return (bool)t; } catch { throw new RpcError($"param '{key}' must be a boolean"); }
        }

        public static JArray? Arr(JObject p, string key) => p[key] as JArray;
        public static JObject? Obj(JObject p, string key) => p[key] as JObject;
    }
}
