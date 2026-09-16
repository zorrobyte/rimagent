using System;
using System.Collections.Generic;
using System.Linq;
using RimBridge.Server;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimBridge.Engine
{
    /// <summary>Resolve game objects from the string handles the agent uses (ThingID, pawn name, defName, zone label...).</summary>
    public static class Lookup
    {
        public static Map Map()
        {
            var m = Find.CurrentMap;
            if (m == null) throw new RpcError("no current map");
            return m;
        }

        public static Thing? ThingOrNull(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("Thing:")) id = id.Substring(6);
            bool numeric = int.TryParse(id, out int num);
            foreach (var map in Find.Maps)
            {
                foreach (var t in map.listerThings.AllThings)
                    if (t.ThingID == id || (numeric && t.thingIDNumber == num)) return t;
                foreach (var p in map.mapPawns.AllPawns)
                    if (p.ThingID == id || (numeric && p.thingIDNumber == num)) return p;
            }
            if (Find.WorldPawns != null)
                foreach (var p in Find.WorldPawns.AllPawnsAliveOrDead)
                    if (p.ThingID == id || (numeric && p.thingIDNumber == num)) return p;
            // things carried/held inside other things
            foreach (var map in Find.Maps)
                foreach (var t in map.listerThings.AllThings)
                {
                    if (t is IThingHolder h)
                    {
                        var held = ThingOwnerUtility.GetAllThingsRecursively(h);
                        foreach (var x in held) if (x.ThingID == id) return x;
                    }
                }
            return null;
        }

        public static Thing Thing(string id) => ThingOrNull(id) ?? throw new RpcError($"no thing with id '{id}'");

        public static Pawn? PawnOrNull(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            if (idOrName.StartsWith("Pawn:")) idOrName = idOrName.Substring(5);
            var map = Find.CurrentMap;
            if (map != null)
            {
                foreach (var p in map.mapPawns.AllPawns)
                    if (p.ThingID == idOrName) return p;
                foreach (var p in map.mapPawns.FreeColonists)
                    if (NameMatches(p, idOrName)) return p;
                foreach (var p in map.mapPawns.AllPawns)
                    if (NameMatches(p, idOrName)) return p;
            }
            return ThingOrNull(idOrName) as Pawn;
        }

        static bool NameMatches(Pawn p, string s)
        {
            if (p.Name == null) return string.Equals(p.LabelShort, s, StringComparison.OrdinalIgnoreCase);
            return string.Equals(p.Name.ToStringShort, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name.ToStringFull, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.LabelShort, s, StringComparison.OrdinalIgnoreCase);
        }

        public static Pawn Pawn(string idOrName) => PawnOrNull(idOrName) ?? throw new RpcError($"no pawn '{idOrName}' (use ThingID like Human1234 or a colonist's short name)");

        public static Pawn Colonist(string idOrName)
        {
            var p = Pawn(idOrName);
            if (!p.IsColonistPlayerControlled && !p.IsColonist) throw new RpcError($"{p.LabelShort} is not a colonist");
            return p;
        }

        public static Def? DefOrNull(Type defType, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var d = GenDefDatabase.GetDefSilentFail(defType, name, false);
            if (d != null) return d;
            // case-insensitive / label match
            foreach (var x in GenDefDatabase.GetAllDefsInDatabaseForDef(defType))
                if (string.Equals(x.defName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(x.label, name, StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }

        public static T Def<T>(string name) where T : Def => (T?)DefOrNull(typeof(T), name) ?? throw new RpcError($"no {typeof(T).Name} named '{name}'");

        public static Type DefType(string name)
        {
            var t = GenTypes.GetTypeInAnyAssembly(name) ?? GenTypes.GetTypeInAnyAssembly("RimWorld." + name) ?? GenTypes.GetTypeInAnyAssembly("Verse." + name);
            if (t == null || !typeof(Def).IsAssignableFrom(t)) throw new RpcError($"'{name}' is not a Def type");
            return t;
        }

        public static Type? TypeOrNull(string name)
        {
            return GenTypes.GetTypeInAnyAssembly(name)
                ?? GenTypes.GetTypeInAnyAssembly("RimWorld." + name)
                ?? GenTypes.GetTypeInAnyAssembly("Verse." + name)
                ?? GenTypes.GetTypeInAnyAssembly("Verse.AI." + name)
                ?? GenTypes.GetTypeInAnyAssembly("RimWorld.Planet." + name)
                ?? GenTypes.GetTypeInAnyAssembly("UnityEngine." + name)
                ?? GenTypes.GetTypeInAnyAssembly("System." + name);
        }

        public static Zone? ZoneOrNull(string label)
        {
            var map = Find.CurrentMap; if (map == null) return null;
            return map.zoneManager.AllZones.FirstOrDefault(z => string.Equals(z.label, label, StringComparison.OrdinalIgnoreCase))
                ?? map.zoneManager.AllZones.FirstOrDefault(z => z.ID.ToString() == label);
        }

        public static Area? AreaOrNull(string label)
        {
            var map = Find.CurrentMap; if (map == null) return null;
            return map.areaManager.AllAreas.FirstOrDefault(a => string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase));
        }

        public static Faction? FactionOrNull(string name)
        {
            if (string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "OfPlayer", StringComparison.OrdinalIgnoreCase)) return Faction.OfPlayer;
            return Find.FactionManager.AllFactions.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? Find.FactionManager.AllFactions.FirstOrDefault(f => string.Equals(f.def.defName, name, StringComparison.OrdinalIgnoreCase))
                ?? Find.FactionManager.AllFactions.FirstOrDefault(f => f.loadID.ToString() == name);
        }

        public static IntVec3 Cell(Newtonsoft.Json.Linq.JToken? t, string what = "cell") => Locate.Cell(t, Find.CurrentMap ?? throw new RpcError("no map"), what);

        public static Room? RoomOrNull(string id)
        {
            var map = Find.CurrentMap; if (map == null) return null;
            return map.regionGrid.AllRooms.FirstOrDefault(r => r.ID.ToString() == id);
        }
    }
}
