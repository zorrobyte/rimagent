// Written for RimBridge (2026). Verse-free rules behind the unforbid / corpses / beds / policies / blueprints standing
// orders (manual-change tracking, temperature targets, the food-policy switch, corpse routing, bed pairing, blueprint
// staleness); shared with the unit tests in mod/Tests.
using System;
using System.Collections.Generic;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Values an order set on a thing (a bed's owners, a heater's target, a pawn's medical care). When the live value
    /// differs from what the order last set, somebody else changed it and the thing is "manual" for a cooldown.
    /// Keys are "kind:thingId".
    /// </summary>
    public sealed class OwnedValues
    {
        public const int TwoDays = 2 * 60000;
        public const int Forever = int.MaxValue;

        public Dictionary<string, string> Set = new Dictionary<string, string>();
        public Dictionary<string, int> ManualUntil = new Dictionary<string, int>();

        public bool HasRecord(string key) => Set.ContainsKey(key);

        public bool IsManual(string key, int now) => ManualUntil.TryGetValue(key, out int until) && until > now;

        public void MarkManual(string key, int now, int cooldownTicks = TwoDays)
        {
            int until = cooldownTicks == Forever || now > Forever - cooldownTicks ? Forever : now + cooldownTicks;
            if (ManualUntil.TryGetValue(key, out int old) && old >= until) return;
            ManualUntil[key] = until;
        }

        public void ClearManual(string key) => ManualUntil.Remove(key);

        /// <summary>Remembers `value` as the one the order set (or accepted).</summary>
        public void Record(string key, string value) => Set[key] = value ?? "";

        public void Forget(string key) { Set.Remove(key); ManualUntil.Remove(key); }

        /// <summary>
        /// Compares the live value with the recorded one. A difference means an outside change: the key is marked
        /// manual for `cooldownTicks`, the record is updated and true is returned. No record → nothing (false).
        /// </summary>
        public bool Observe(string key, string current, int now, int cooldownTicks = TwoDays)
        {
            current ??= "";
            if (!Set.TryGetValue(key, out var recorded)) return false;
            if (recorded == current) return false;
            MarkManual(key, now, cooldownTicks);
            Set[key] = current;
            return true;
        }

        /// <summary>Drops expired manual marks and records whose keys are not in `alive` (things gone from the map).</summary>
        public int Prune(int now, ICollection<string>? alive = null)
        {
            var dead = new List<string>();
            foreach (var kv in ManualUntil) if (kv.Value <= now) dead.Add(kv.Key);
            foreach (var k in dead) ManualUntil.Remove(k);
            int n = dead.Count;
            if (alive != null)
            {
                dead.Clear();
                foreach (var k in Set.Keys) if (!alive.Contains(k)) dead.Add(k);
                foreach (var k in dead) Forget(k);
                n += dead.Count;
            }
            return n;
        }
    }

    /// <summary>Mirror of RimWorld.Season (same numeric values) so the target rule stays Verse-free.</summary>
    public enum SeasonKind : byte { Undefined, Spring, Summer, Fall, Winter, PermanentSummer, PermanentWinter }

    /// <summary>Heater/cooler target temperature by season (spec: 21°C in winter, 24°C in summer).</summary>
    public static class TempTargets
    {
        public const float Winter = 21f;
        public const float Summer = 24f;

        /// <summary>Winter → 21, summer → 24; spring/fall/undefined → a heater keeps 21 and a cooler 24 (each device's own season).</summary>
        public static float Target(SeasonKind season, bool heater)
        {
            switch (season)
            {
                case SeasonKind.Winter:
                case SeasonKind.PermanentWinter: return Winter;
                case SeasonKind.Summer:
                case SeasonKind.PermanentSummer: return Summer;
                default: return heater ? Winter : Summer;
            }
        }

        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// Which heaters/coolers the policies order manages: a device is adopted on first sight only when its target is
    /// the game default (21°C); a cooler already set below 0°C (a freezer) is never touched; once a managed device's
    /// target changes outside the order it is hands-off for good (OwnedValues.Forever).
    /// </summary>
    public static class TempRules
    {
        public const float AdoptTolerance = 0.05f;
        public const float FreezerBelow = 0f;

        /// <summary>null = the order may manage it now; else why it is left alone.</summary>
        public static string? WhyNot(bool hasRecord, float current, float defaultTarget, bool cooler)
        {
            if (cooler && current < FreezerBelow) return "freezer";
            if (!hasRecord && Math.Abs(current - defaultTarget) > AdoptTolerance) return "not at the default target";
            return null;
        }
    }

    /// <summary>Food-policy auto-switch with hysteresis: on below colonists×2 meals, off at colonists×4.</summary>
    public static class FoodSwitch
    {
        public const int OnPerColonist = 2;
        public const int OffPerColonist = 4;

        /// <summary>Returns the new "active" state given the meal count, colonist count and the current state.</summary>
        public static bool Decide(int meals, int colonists, bool active)
        {
            if (colonists <= 0) return false;
            return active ? meals < colonists * OffPerColonist : meals < colonists * OnPerColonist;
        }
    }

    /// <summary>What the policies order sets when the director has not: colonists NormalOrWorse, prisoners/animals HerbalOrWorse.</summary>
    public enum CareKind { Colonist, Prisoner, Animal, Slave }

    public static class MedicalDefaults
    {
        public const string NormalOrWorse = "NormalOrWorse";
        public const string HerbalOrWorse = "HerbalOrWorse";

        public static string For(CareKind kind) => kind == CareKind.Colonist ? NormalOrWorse : HerbalOrWorse;
    }

    /// <summary>Facts about one corpse, filled by the corpses order from the live thing.</summary>
    public sealed class CorpseFacts
    {
        public bool Humanlike;
        public bool Animal;
        public bool Rotten;          // RotStage Rotting or Dessicated
        public bool CombatActive;
        public bool HostileCamp;     // lies inside a room owned by a hostile faction
        public bool Touched;
        public bool FreeGrave;       // a grave/sarcophagus accepts it and is empty
        public bool StockpileAccepts;// some stockpile/shelf accepts it (fresh) — for rotten ones: a dumping stockpile accepts it
        public bool Crematorium;     // a work table with the cremation recipe exists
    }

    public enum CorpseAction { None, Bury, StripAndHaul, Cremate, Dump, Butcher }

    /// <summary>Corpse routing decision table (spec order 4).</summary>
    public static class CorpseRouting
    {
        public static CorpseAction Decide(CorpseFacts f)
        {
            if (f == null || f.CombatActive || f.HostileCamp || f.Touched) return CorpseAction.None;
            if (f.Rotten)
            {
                if (f.StockpileAccepts) return CorpseAction.Dump;
                return f.Humanlike && f.Crematorium ? CorpseAction.Cremate : CorpseAction.None;
            }
            if (f.Humanlike)
            {
                if (f.FreeGrave) return CorpseAction.Bury;
                if (f.StockpileAccepts) return CorpseAction.StripAndHaul;
                if (f.Crematorium) return CorpseAction.Cremate;
                return CorpseAction.None;
            }
            if (f.Animal) return CorpseAction.Butcher;
            return CorpseAction.None;
        }
    }

    /// <summary>One bed a colonist could take, as seen by the beds order.</summary>
    public struct BedCandidate
    {
        public string Id;
        public int Slots;
        public int Owners;
        public float DistSq;
        public bool PartnerOwns;   // the pawn's love partner owns this bed
        public bool Manual;        // touched/changed by hand recently
        public bool Medical;
        public bool Usable;        // CanUseBedEver + CanAssignTo + ideology ok
        public BedCandidate(string id, int slots, int owners, float distSq, bool partnerOwns = false, bool manual = false, bool medical = false, bool usable = true)
        { Id = id; Slots = slots; Owners = owners; DistSq = distSq; PartnerOwns = partnerOwns; Manual = manual; Medical = medical; Usable = usable; }
        public bool Free => Owners == 0;
        public bool Double => Slots >= 2;
    }

    /// <summary>Bed choice for a colonist without a bed: partner's bed with a free slot, else an empty double bed for a couple, else the nearest empty bed.</summary>
    public static class BedPairing
    {
        public static string? Choose(IReadOnlyList<BedCandidate> beds, bool partnerNeedsBed)
        {
            if (beds == null || beds.Count == 0) return null;
            string? best = null; float bd = float.MaxValue;
            // 1. share the partner's bed when it has room
            foreach (var b in beds)
            {
                if (!b.PartnerOwns || b.Manual || b.Medical || !b.Usable) continue;
                if (b.Owners < b.Slots && (b.DistSq < bd || best == null)) { best = b.Id; bd = b.DistSq; }
            }
            if (best != null) return best;
            // 2. a couple with no bed at all: the nearest empty double bed
            if (partnerNeedsBed)
            {
                foreach (var b in beds)
                {
                    if (b.Manual || b.Medical || !b.Usable || !b.Free || !b.Double) continue;
                    if (b.DistSq < bd) { best = b.Id; bd = b.DistSq; }
                }
                if (best != null) return best;
            }
            // 3. the nearest empty bed (never squeeze into a stranger's double bed)
            foreach (var b in beds)
            {
                if (b.Manual || b.Medical || !b.Usable || !b.Free) continue;
                if (b.DistSq < bd) { best = b.Id; bd = b.DistSq; }
            }
            return best;
        }
    }

    /// <summary>Blueprint hygiene rules (spec order 7).</summary>
    public static class BlueprintRules
    {
        public const int StaleAfterTicks = 3 * 60000;
        /// <summary>A blueprint is cancelled for unreachability only after staying unreachable this long (two passes).</summary>
        public const int UnreachableGraceTicks = 2 * 2500;

        public static bool IsStale(int spawnedTick, int now) => spawnedTick >= 0 && now - spawnedTick >= StaleAfterTicks;

        /// <summary>True when the thing has been unreachable since `since` for at least the grace period.</summary>
        public static bool CancelUnreachable(int since, int now) => since >= 0 && now - since >= UnreachableGraceTicks;

        /// <summary>(def, still needed) for every cost entry the colony's counted stock cannot cover.</summary>
        public static List<(string def, int missing)> Missing(IEnumerable<(string def, int need)> cost, Func<string, int> have)
        {
            var list = new List<(string, int)>();
            if (cost == null) return list;
            foreach (var (def, need) in cost)
            {
                if (need <= 0) continue;
                int h = have?.Invoke(def) ?? 0;
                if (h < need) list.Add((def, need - h));
            }
            return list;
        }
    }

    /// <summary>Which forbidden things the unforbid order may touch (spec order 3).</summary>
    public sealed class ForbiddenFacts
    {
        public bool InHomeArea;
        public float DistToBase;
        public bool ColonistCorpse;
        public bool HostileStructure;
        public bool Touched;
        public bool CaravanItem;
        public bool Reachable = true;
        public bool RecentItem;      // an item (not a chunk/plant/corpse) that spawned within the last 3 days: drop-pod contents
    }

    public static class UnforbidRules
    {
        public const float BaseRadius = 20f;
        public const float PodRadius = 40f;
        public const int RecentItemTicks = 3 * 60000;

        /// <summary>null = unforbid it, else the reason it stays forbidden ("outside" = not in scope).</summary>
        public static string? WhyNot(ForbiddenFacts f)
        {
            if (f == null) return "no thing";
            bool near = f.InHomeArea || f.DistToBase <= BaseRadius;
            bool pod = f.RecentItem && f.DistToBase <= PodRadius;
            if (!near && !pod) return "outside";
            if (f.ColonistCorpse) return "colonist corpse";
            if (f.Touched) return "hands-off";
            if (f.HostileStructure) return "hostile structure";
            if (f.CaravanItem) return "caravan";
            if (!f.Reachable) return "unreachable";
            return null;
        }
    }
}
