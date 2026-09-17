// Derived from Colony Manager Redux Helpers/Utilities/Utilities_Plants.cs (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Stock
{
    public static class PlantUtility
    {
        /// <summary>Plant defs on this map (biome wild plants, cave plants, ambrosia, and anything growing outside zones).</summary>
        public static IEnumerable<ThingDef> GetAllPlants(Map? map)
        {
            if (map == null) return DefDatabase<ThingDef>.AllDefsListForReading.Where(td => td.IsPlant);
            var set = new HashSet<ThingDef>(map.Biome.AllWildPlants);
            foreach (var td in DefDatabase<ThingDef>.AllDefsListForReading)
                if (td.plant?.cavePlant ?? false) set.Add(td);
            if (ThingDefOf.Plant_Ambrosia != null) set.Add(ThingDefOf.Plant_Ambrosia);
            foreach (var p in map.listerThings.AllThings.OfType<Plant>())
            {
                if (!p.Spawned) continue;
                if (map.zoneManager.ZoneAt(p.Position) is IPlantToGrowSettable) continue;
                if (map.thingGrid.ThingsListAt(p.Position).Any(t => t is Building_PlantGrower)) continue;
                set.Add(p.def);
            }
            return set;
        }

        public static bool IsValidForestryPlant(ThingDef td, bool clearArea)
        {
            var p = td.plant;
            if (p == null) return false;
            return clearArea || ((p.harvestTag == "Wood" || p.harvestedThingDef == ThingDefOf.WoodLog) && p.harvestedThingDef != null && p.harvestYield > 0);
        }

        public static IEnumerable<ThingDef> GetForestryPlants(Map? map, bool clearArea)
            => GetAllPlants(map).Where(td => IsValidForestryPlant(td, clearArea)).Distinct().OrderBy(td => td.label);

        public static IEnumerable<ThingDef> GetForagingPlants(Map? map)
            => GetAllPlants(map)
                .Where(td => td.plant != null && td.plant.harvestYield > 0 && td.plant.harvestedThingDef != null && td.plant.harvestTag != "Wood")
                .Distinct().OrderBy(td => td.label);

        public static bool InGrowingZoneOrPot(Map map, IntVec3 cell)
            => map.zoneManager.ZoneAt(cell) is IPlantToGrowSettable
               || map.thingGrid.ThingsListAt(cell).Any(t => t is Building_PlantGrower);
    }
}
