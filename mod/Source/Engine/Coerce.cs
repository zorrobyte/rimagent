using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimBridge.Server;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBridge.Engine
{
    /// <summary>JSON → CLR value conversion aware of RimWorld handle conventions.</summary>
    public static class Coerce
    {
        public static bool CanCoerce(JToken? t, Type type)
        {
            try { To(t, type); return true; } catch { return false; }
        }

        public static object? To(JToken? t, Type type)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new RpcError($"null is not valid for {type.Name}");
                return null;
            }
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) type = nullable;

            if (t.Type == JTokenType.String && ((string)t!).StartsWith("Handle:"))
            {
                var h = Handles.Get((string)t!) ?? throw new RpcError($"unknown handle {t}");
                if (type.IsInstanceOfType(h)) return h;
            }
            if (type == typeof(object)) return Guess(t);
            if (type == typeof(string)) return t.Type == JTokenType.String ? (string)t! : t.ToString();
            if (type == typeof(TaggedString)) return new TaggedString(t.ToString());
            if (type == typeof(bool)) return t.Type == JTokenType.Boolean ? (bool)t : bool.Parse(t.ToString());
            if (type.IsEnum)
            {
                if (t.Type == JTokenType.Integer) return Enum.ToObject(type, (long)t);
                try { return Enum.Parse(type, t.ToString(), true); }
                catch { throw new RpcError($"'{t}' is not a {type.Name}; valid: {string.Join(", ", Enum.GetNames(type))}"); }
            }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (t.Type == JTokenType.String && type == typeof(int)) return int.Parse((string)t!);
                if (t.Type == JTokenType.String && type == typeof(float)) return float.Parse((string)t!);
                return Convert.ChangeType(((JValue)t).Value, type);
            }
            if (type == typeof(IntVec3)) return Lookup.Cell(t);
            if (type == typeof(IntVec2)) { var c = Lookup.Cell(t); return new IntVec2(c.x, c.z); }
            if (type == typeof(Vector3))
            {
                if (t is JArray a3 && a3.Count == 3) return new Vector3((float)a3[0]!, (float)a3[1]!, (float)a3[2]!);
                return Lookup.Cell(t).ToVector3Shifted();
            }
            if (type == typeof(CellRect)) return Locate.Rect(t, Lookup.Map());
            if (type == typeof(IntRange)) { var a = (JArray)t; return new IntRange((int)a[0]!, (int)a[1]!); }
            if (type == typeof(FloatRange)) { var a = (JArray)t; return new FloatRange((float)a[0]!, (float)a[1]!); }
            if (type == typeof(Rot4))
            {
                if (t.Type == JTokenType.Integer) return new Rot4((int)t);
                var s = t.ToString().ToLowerInvariant();
                return s switch { "n" or "north" => Rot4.North, "e" or "east" => Rot4.East, "s" or "south" => Rot4.South, "w" or "west" => Rot4.West, _ => throw new RpcError("rotation must be N/E/S/W or 0..3") };
            }
            if (type == typeof(Map)) return t.Type == JTokenType.Integer ? Find.Maps[(int)t] : Lookup.Map();
            if (type == typeof(Type)) return Lookup.TypeOrNull(t.ToString()) ?? throw new RpcError($"unknown type '{t}'");
            if (typeof(Def).IsAssignableFrom(type))
            {
                string name = t.ToString();
                if (name.StartsWith("Def:")) name = name.Split(':').Last();
                return Lookup.DefOrNull(type, name) ?? throw new RpcError($"no {type.Name} named '{name}'");
            }
            if (typeof(Pawn).IsAssignableFrom(type)) return Lookup.Pawn(t.ToString());
            if (typeof(Thing).IsAssignableFrom(type))
            {
                var th = Lookup.ThingOrNull(t.ToString()) ?? Lookup.PawnOrNull(t.ToString());
                if (th == null) throw new RpcError($"no thing '{t}'");
                if (!type.IsInstanceOfType(th)) throw new RpcError($"{th.ThingID} is a {th.GetType().Name}, not a {type.Name}");
                return th;
            }
            if (type == typeof(Faction)) return Lookup.FactionOrNull(t.ToString()) ?? throw new RpcError($"no faction '{t}'");
            if (type == typeof(Zone) || type.IsSubclassOf(typeof(Zone))) return Lookup.ZoneOrNull(t.ToString()) ?? throw new RpcError($"no zone '{t}'");
            if (typeof(Area).IsAssignableFrom(type)) return Lookup.AreaOrNull(t.ToString()) ?? throw new RpcError($"no area '{t}'");
            if (type == typeof(Room)) return Lookup.RoomOrNull(t.ToString()) ?? throw new RpcError($"no room '{t}'");
            if (type == typeof(LocalTargetInfo))
            {
                if (t is JArray) return new LocalTargetInfo(Lookup.Cell(t));
                var th = Lookup.ThingOrNull(t.ToString()) ?? Lookup.PawnOrNull(t.ToString());
                if (th != null) return new LocalTargetInfo(th);
                return new LocalTargetInfo(Lookup.Cell(t));
            }
            if (type == typeof(TargetInfo))
            {
                if (t is JArray) return new TargetInfo(Lookup.Cell(t), Lookup.Map());
                var th = Lookup.ThingOrNull(t.ToString()) ?? Lookup.PawnOrNull(t.ToString());
                if (th != null) return new TargetInfo(th);
                return new TargetInfo(Lookup.Cell(t), Lookup.Map());
            }
            if (type == typeof(ThingDefCount) || type == typeof(ThingDefCountClass))
            {
                var o = (JObject)t;
                var def = (ThingDef)To(o["def"], typeof(ThingDef))!;
                int count = (int?)o["count"] ?? 1;
                return type == typeof(ThingDefCount) ? (object)new ThingDefCount(def, count) : new ThingDefCountClass(def, count);
            }
            // Collections
            if (type.IsArray)
            {
                var et = type.GetElementType()!;
                var arr = (JArray)(t is JArray ? t : new JArray(t));
                var res = Array.CreateInstance(et, arr.Count);
                for (int i = 0; i < arr.Count; i++) res.SetValue(To(arr[i], et), i);
                return res;
            }
            if (type.IsGenericType)
            {
                var gd = type.GetGenericTypeDefinition();
                if (gd == typeof(List<>) || gd == typeof(IList<>) || gd == typeof(IEnumerable<>) || gd == typeof(ICollection<>) || gd == typeof(IReadOnlyList<>))
                {
                    var et = type.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(et))!;
                    var arr = (JArray)(t is JArray ? t : new JArray(t));
                    foreach (var x in arr) list.Add(To(x, et));
                    return list;
                }
                if (gd == typeof(Dictionary<,>))
                {
                    var kt = type.GetGenericArguments()[0]; var vt = type.GetGenericArguments()[1];
                    var dict = (IDictionary)Activator.CreateInstance(type)!;
                    foreach (var kv in (JObject)t) dict.Add(To(kv.Key, kt)!, To(kv.Value, vt));
                    return dict;
                }
            }
            // Delegates cannot be coerced.
            if (typeof(Delegate).IsAssignableFrom(type)) throw new RpcError($"cannot pass a delegate ({type.Name}) over the bridge");
            // Generic object: construct and fill fields/properties from a JSON object.
            if (t is JObject jo)
            {
                object inst;
                try { inst = Activator.CreateInstance(type, nonPublic: true) ?? throw new RpcError($"cannot construct {type.Name}"); }
                catch (MissingMethodException) { throw new RpcError($"{type.Name} has no parameterless constructor; pass a handle instead"); }
                foreach (var kv in jo)
                {
                    var f = type.GetField(kv.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) { f.SetValue(inst, To(kv.Value, f.FieldType)); continue; }
                    var pr = type.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pr != null && pr.CanWrite) { pr.SetValue(inst, To(kv.Value, pr.PropertyType)); continue; }
                    throw new RpcError($"{type.Name} has no member '{kv.Key}'");
                }
                return inst;
            }
            if (t.Type == JTokenType.String)
            {
                // Last resort: an engine path.
                return Reflector.Resolve(t.ToString());
            }
            throw new RpcError($"cannot convert {t.Type} to {type.Name}");
        }

        public static object? Guess(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: return (int)(long)t;
                case JTokenType.Float: return (float)(double)t;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.String:
                {
                    string s = (string)t!;
                    if (s.StartsWith("Thing:") || s.StartsWith("Pawn:") || s.StartsWith("Def:") || s.StartsWith("Type:") || s.StartsWith("Find.") || s.StartsWith("Map.")) return Reflector.Resolve(s);
                    return s;
                }
                case JTokenType.Array:
                {
                    var a = (JArray)t;
                    if (a.Count == 2 && a.All(x => x.Type == JTokenType.Integer)) return Lookup.Cell(a);
                    return a.Select(Guess).ToList();
                }
                case JTokenType.Null: return null;
                default: return t.ToString();
            }
        }
    }
}
