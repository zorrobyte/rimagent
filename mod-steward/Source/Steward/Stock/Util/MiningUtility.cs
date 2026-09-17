// Derived from Colony Manager Redux Helpers/Utilities/Utilities_Mining.cs and parts of ManagerJobs/ManagerJob_Mining.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous rewrite.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>Pure helpers for the mining job: chunk products, mineral/building materials, roof and danger checks.</summary>
    public static class MiningUtility
    {
        private static List<ThingCategoryDef>? _chunkCategoryDefs;
        private static List<ThingCategoryDef> ChunkCategoryDefs =>
            _chunkCategoryDefs ??= ThingCategoryDefOf.Chunks.ThisAndChildCategoryDefs.ToList();

        /// <summary>Spacing of the pillar grid left standing by the basic roof-support check.</summary>
        public const int RoofSupportGridSpacing = 5;

        public static bool IsChunk(ThingDef? def) =>
            def?.thingCategories != null && def.thingCategories.Any(c => ChunkCategoryDefs.Contains(c));

        /// <summary>Everything a chunk turns into: stonecutting (butcherProducts) plus smelting (smeltProducts).</summary>
        public static IEnumerable<ThingDefCountClass> GetChunkProducts(ThingDef chunk) =>
            (chunk.butcherProducts ?? Enumerable.Empty<ThingDefCountClass>())
                .Concat(chunk.smeltProducts ?? Enumerable.Empty<ThingDefCountClass>());

        /// <summary>All natural-rock defs that drop something when mined (ore, compacted machinery, plain rock → chunks).</summary>
        public static IEnumerable<ThingDef> GetMinerals() =>
            DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.building != null && d.building.isNaturalRock && d.building.mineableThing != null);

        /// <summary>All building defs that can be deconstructed and refund materials.</summary>
        public static IEnumerable<ThingDef> GetDeconstructibleBuildings() =>
            DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => (!d.costList.NullOrEmpty() || d.MadeFromStuff)
                            && d.building != null
                            && d.building.IsDeconstructible
                            && d.resourcesFractionWhenDeconstructed > 0);

        /// <summary>Base cost list defs plus every stuff the building may be made of.</summary>
        public static IEnumerable<ThingDef> GetMaterialsInBuilding(ThingDef? building)
        {
            if (building == null) return Enumerable.Empty<ThingDef>();
            var baseCosts = building.costList.NullOrEmpty()
                ? Enumerable.Empty<ThingDef>()
                : building.costList.Select(tc => tc.thingDef);
            return baseCosts.Concat(GenStuff.AllowedStuffsFor(building));
        }

        public static IEnumerable<ThingDef> GetMaterialsInChunk(ThingDef chunk) => GetChunkProducts(chunk).Select(tc => tc.thingDef);

        /// <summary>What mining one cell of this rock ultimately yields (ore directly, or chunk products for plain rock).</summary>
        public static List<ThingDef> GetMaterialsInMineral(ThingDef mineral)
        {
            var resource = mineral.building?.mineableThing;
            if (resource == null) return new List<ThingDef>();
            if (IsChunk(resource)) return GetMaterialsInChunk(resource).ToList();
            return new List<ThingDef> { resource };
        }

        /// <summary>Counted products in one chunk.</summary>
        public static int GetCountInChunk(ThingDef chunk, Func<ThingDef, bool> counted)
        {
            if (chunk.butcherProducts.NullOrEmpty() && chunk.smeltProducts.NullOrEmpty()) return 0;
            int sum = 0;
            foreach (var tc in GetChunkProducts(chunk))
                if (counted(tc.thingDef)) sum += tc.count;
            return sum;
        }

        /// <summary>Expected counted yield of one mineable cell of <paramref name="rock"/>.</summary>
        public static int GetCountInMineral(ThingDef? rock, Func<ThingDef, bool> counted)
        {
            var resource = rock?.building?.mineableThing;
            if (rock == null || resource == null) return 0;
            var b = rock.building;
            if (IsChunk(resource))
                return (int)(GetCountInChunk(resource, counted) * b.mineableDropChance);
            if (!counted(resource)) return 0;
            // EffectiveMineableYield already applies the storyteller mineYieldFactor.
            return (int)(b.EffectiveMineableYield * b.mineableDropChance);
        }

        /// <summary>Counted materials refunded by deconstructing this building (resourcesFractionWhenDeconstructed applied).</summary>
        public static int GetCountInBuilding(Building? building, Func<ThingDef, bool> counted)
        {
            var def = building?.def;
            if (building == null || def == null) return 0;
            float sum = 0f;
            foreach (var tc in def.CostListAdjusted(building.Stuff, false))
                if (counted(tc.thingDef)) sum += tc.count * def.resourcesFractionWhenDeconstructed;
            return Mathf.RoundToInt(sum);
        }

        // ── Safety checks ─────────────────────────────────────────────────────

        /// <summary>Basic roof-support rule: leave a pillar grid standing.</summary>
        public static bool IsARoofSupport_Basic(IntVec3 cell) =>
            cell.x % RoofSupportGridSpacing == 0 && cell.z % RoofSupportGridSpacing == 0;

        public static bool IsDesignatedForRemoval(Building building, Map map)
        {
            var des = map.designationManager.DesignationOn(building)?.def;
            if (des == null) des = map.designationManager.DesignationAt(building.Position, DesignationDefOf.Mine)?.def;
            return des != null && (des == DesignationDefOf.Mine || des == DesignationDefOf.Deconstruct);
        }

        /// <summary>
        /// True when the roofed cell at <paramref name="position"/> would have no remaining roof holder within
        /// support range once the edifice at <paramref name="support"/> is removed. Mirrors
        /// RoofCollapseUtility.WithinRangeOfRoofHolder but excludes one support and ignores holders already
        /// designated for removal.
        /// </summary>
        public static bool WouldCollapseIfSupportDestroyed(IntVec3 position, IntVec3 support, Map map)
        {
            if (!position.InBounds(map) || !position.Roofed(map)) return false;
            var edifices = map.edificeGrid;
            int n = RoofCollapseUtility.RoofSupportRadialCellsCount;
            for (int i = 0; i < n; i++)
            {
                var candidate = position + GenRadial.RadialPattern[i];
                if (candidate == support || !candidate.InBounds(map)) continue;
                var building = edifices[candidate];
                if (building != null && building.def.holdsRoof && !IsDesignatedForRemoval(building, map))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Simplified room-divider test (CMR's path-cost half is dropped): the cell divides rooms when its
        /// passable, unfogged 8-way neighbours belong to two or more distinct rooms.
        /// </summary>
        public static bool IsARoomDivider(IntVec3 position, Map map)
        {
            Room? first = null;
            foreach (var c in GenAdjFast.AdjacentCells8Way(position))
            {
                if (!c.InBounds(map) || c.Fogged(map) || c.Impassable(map)) continue;
                var room = c.GetRoom(map);
                if (room == null) continue;
                if (first == null) first = room;
                else if (room != first) return true;
            }
            return false;
        }

        /// <summary>Unopened ancient-danger rects: their RectTrigger still exists until a pawn approaches.</summary>
        public static List<CellRect> GetAncientDangerRects(Map map)
        {
            var rects = new List<CellRect>();
            foreach (var trigger in map.listerThings.GetThingsOfType<RectTrigger>())
            {
                if (trigger.signalTag != null && trigger.signalTag.StartsWith("ancientTempleApproached", StringComparison.Ordinal))
                    rects.Add(trigger.Rect);
            }
            return rects;
        }

        public static bool InAnyRect(List<CellRect> rects, IntVec3 cell)
        {
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].Contains(cell)) return true;
            return false;
        }
    }
}
