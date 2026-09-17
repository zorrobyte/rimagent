// Derived from Colony Manager Redux Helpers/Utilities/Utilities.cs CountProducts (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBridge.Steward.Stock
{
    public static class ProductCounter
    {
        /// <summary>
        /// Counts things matching the filter. Resources use the map resource counter (stored only);
        /// countAllOnMap adds matching things outside storage; a stockpile restricts to that stockpile.
        /// </summary>
        public static int CountProducts(Map map, ThingFilter filter, Zone_Stockpile? stockpile = null, bool countAllOnMap = false)
        {
            int count = 0;
            foreach (var thingDef in filter.AllowedThingDefs)
            {
                if (thingDef == null) continue;

                bool usedResourceCounter = thingDef.CountAsResource && stockpile == null;
                if (usedResourceCounter)
                {
                    count += map.resourceCounter.GetCount(thingDef);
                    if (!countAllOnMap) continue;
                }

                var things = map.listerThings.ThingsOfDef(thingDef);
                SlotGroup? slotGroup = stockpile?.slotGroup;
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (slotGroup != null && t.Position.GetSlotGroup(map) != slotGroup) continue;
                    if (t.IsForbidden(Faction.OfPlayer) || t.Position.Fogged(map)) continue;
                    if (ShouldSkipDueToStorageState(usedResourceCounter, countAllOnMap, t.IsInAnyStorage())) continue;

                    bool hasQuality = t.TryGetQuality(out var quality);
                    float hpPct = t.MaxHitPoints > 0 ? (float)t.HitPoints / t.MaxHitPoints : 1f;
                    if (!ShouldCountThing(hasQuality, quality, thingDef.useHitPoints, hpPct, filter)) continue;

                    count += t.stackCount;
                }
            }
            return count;
        }

        internal static bool ShouldCountThing(bool hasQuality, QualityCategory quality, bool useHitPoints, float hitPointsPercent, ThingFilter filter)
        {
            bool qualityOk = !hasQuality || filter.AllowedQualityLevels.Includes(quality);
            bool hpOk = !useHitPoints || filter.AllowedHitPointsPercents.IncludesEpsilon(Mathf.Clamp01(hitPointsPercent));
            return qualityOk && hpOk;
        }

        internal static bool ShouldSkipDueToStorageState(bool usedResourceCounter, bool countAllOnMap, bool isInAnyStorage)
            => usedResourceCounter ? isInAnyStorage : !countAllOnMap && !isInAnyStorage;

        public static bool IsInAllowedArea(Area? area, IntVec3 position, bool invert)
            => area == null || (area[position] != invert);

        /// <summary>Centre of the Home area (fallback: colonist buildings, then map centre).</summary>
        public static IntVec3 GetBaseCenter(Map map)
        {
            var home = map.areaManager.Home;
            var cells = home?.ActiveCells.ToList();
            IntVec3 pos;
            if (cells != null && cells.Count > 0) pos = Average(cells);
            else
            {
                var b = map.listerBuildings.allBuildingsColonist;
                pos = b.Count > 0 ? Average(b.Select(x => x.Position).ToList()) : map.Center;
            }
            if (!pos.InBounds(map)) return map.Center;
            if (!pos.Standable(map))
            {
                var near = GenRadial.RadialCellsAround(pos, 12f, true).FirstOrDefault(c => c.InBounds(map) && c.Standable(map));
                if (near.IsValid) pos = near;
            }
            return pos;
        }

        private static IntVec3 Average(IReadOnlyList<IntVec3> cells)
        {
            long x = 0, z = 0;
            foreach (var c in cells) { x += c.x; z += c.z; }
            return new IntVec3((int)(x / cells.Count), 0, (int)(z / cells.Count));
        }
    }
}
