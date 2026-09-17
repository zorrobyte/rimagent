// Written for RimBridge (2026): the policies standing order (spec order 6): food-policy auto-switch, medical-care
// defaults and seasonal heater/cooler targets. Nothing about drugs or apparel.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Meals below colonists×2 → every colonist eats by the "steward-raw" policy (raw + meals, no corpses/insect meat/
    /// kibble) until meals reach colonists×4, then each pawn's previous policy is restored. Medical care defaults
    /// (colonists NormalOrWorse, prisoners/animals HerbalOrWorse) unless the director set one (hands-off 2 days). Heaters/
    /// coolers first seen at the game default target 21°C in winter and 24°C in summer; freezers and devices changed
    /// outside the order are never touched.
    /// </summary>
    public sealed class Order_Policies : Order
    {
        public override string Id => "policies";
        public override string Label => "Policies: food switch, medical care, temperatures";
        public override string Doc =>
            "Every 1200 ticks. Food: unforbidden things whose def name contains 'Meal' are counted; below colonists×2 every free colonist's food " +
            "policy becomes 'steward-raw' (created once: everything edible except corpses, insect meat and kibble) and the previous policy is " +
            "remembered per pawn; at colonists×4 or more it is restored (pawns whose policy was changed meanwhile keep theirs). Medical care: " +
            "colonists NormalOrWorse, prisoners and animals HerbalOrWorse, unless the director set the pawn's care (ui.set_policies) or it changed " +
            "outside this order — then hands-off for 2 days (steward.orders.release hands it back early). Temperature: heaters/coolers are " +
            "adopted only when first seen at the game default target (21°C); adopted devices target 21°C in winter and 24°C in summer " +
            "(spring/fall: heaters 21, coolers 24); a cooler set below 0°C (a freezer) is never touched; a device whose target changed " +
            "outside this order is hands-off permanently. Nothing about drugs, apparel, schedules or areas.";
        public override int IntervalTicks => 1200;
        static readonly string[] Scopes = { "ui.set_policies:food", "ui.set_policies:medical" };
        public override IReadOnlyList<string>? TouchScopes => Scopes;
        static readonly string[] Prefixes = { "food:", "med:", "temp:" };
        public override IReadOnlyList<string> OwnedPrefixes => Prefixes;

        public const string RawPolicyLabel = "steward-raw";
        static readonly string[] FoodScope = { "ui.set_policies:food" };
        static readonly string[] MedScope = { "ui.set_policies:medical" };

        public override IEnumerable<string> Explain()
        {
            var g = StewardGame.Current;
            yield return $"food: meals (defName contains 'Meal', unforbidden, spawned) < colonists×{FoodSwitch.OnPerColonist} → '{RawPolicyLabel}' on every free colonist; restored when meals ≥ colonists×{FoodSwitch.OffPerColonist}";
            yield return $"'{RawPolicyLabel}': all nutrition > 0 defs except the Corpses category, insect meat (Insectoid flesh source) and Kibble";
            yield return "food hands-off: ui.set_policies:food in the last hour, or a pawn whose policy differs from what this order set (its previous policy is forgotten)";
            yield return $"medical: colonists {MedicalDefaults.NormalOrWorse}, prisoners/slaves/animals {MedicalDefaults.HerbalOrWorse}; a pawn whose care the director set (ui.set_policies:medical) or that changed outside this order is hands-off 2 days (owned key med:<pawn>; steward.orders.release hands it back early)";
            yield return $"temperature: adopt a heater/cooler only when first seen at the game default ({TempRules.AdoptTolerance:0.00} tolerance); never a cooler below {TempRules.FreezerBelow:0}°C; winter {TempTargets.Winter:0}°C, summer {TempTargets.Summer:0}°C, spring/fall heaters {TempTargets.Winter:0}°C coolers {TempTargets.Summer:0}°C, clamped to the device's range; changed outside this order → hands-off permanently (owned key temp:<thing>)";
            if (g != null) yield return g.foodSwitchActive ? $"food switch ACTIVE ({g.foodPrevPolicy.Count} pawn(s) remembered)" : "food switch inactive";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            var report = new OrderReport();
            var parts = new List<string>();
            RunFood(map, g, tick, report, parts);
            RunMedical(map, g, tick, report, parts);
            RunTemperature(map, g, tick, report, parts);
            report.Summary = parts.Count == 0 ? "policies in order" : string.Join(", ", parts);
            return report;
        }

        // ── food ──

        void RunFood(Map map, StewardGame g, int tick, OrderReport report, List<string> parts)
        {
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => p.IsFreeColonist && !p.IsSlaveOfColony && !p.Dead && p.foodRestriction != null).ToList();
            int meals = CountMeals(map);
            bool active = FoodSwitch.Decide(meals, colonists.Count, g.foodSwitchActive);
            var db = Current.Game.foodRestrictionDatabase;
            if (active)
            {
                var raw = EnsureRawPolicy(g, db);
                int switched = 0, kept = 0;
                foreach (var p in colonists)
                {
                    string key = "food:" + p.ThingID;
                    var cur = p.foodRestriction.CurrentFoodPolicy;
                    if (cur == raw) { g.owned.Record(key, raw.id.ToString()); kept++; continue; }
                    if (StandingOrders.IsTouched(p.ThingID, FoodScope)) continue;
                    if (g.foodPrevPolicy.ContainsKey(p.ThingID))
                    {
                        // we switched this pawn before and someone changed it back: theirs to keep
                        g.foodPrevPolicy.Remove(p.ThingID); g.owned.Forget(key); g.owned.MarkManual(key, tick, OwnedValues.TwoDays);
                        continue;
                    }
                    if (g.owned.IsManual(key, tick)) continue;
                    g.foodPrevPolicy[p.ThingID] = cur?.id ?? -1;
                    p.foodRestriction.CurrentFoodPolicy = raw;
                    g.owned.Record(key, raw.id.ToString());
                    switched++; report.Act(p.ThingID);
                }
                if (!g.foodSwitchActive) StewardLog.Message($"orders: policies food switch ON ({meals} meals for {colonists.Count} colonists)");
                g.foodSwitchActive = true;
                parts.Add($"food: {meals} meals < {colonists.Count}×{FoodSwitch.OffPerColonist}, '{RawPolicyLabel}' on {kept + switched}" + (switched > 0 ? $" ({switched} switched now)" : ""));
            }
            else if (g.foodSwitchActive || g.foodPrevPolicy.Count > 0)
            {
                var raw = FindRawPolicy(g, db);
                int restored = 0, theirs = 0;
                var all = map.mapPawns.AllPawns.Where(p => p.foodRestriction != null).ToDictionary(p => p.ThingID, p => p);
                foreach (var kv in g.foodPrevPolicy.ToList())
                {
                    if (!all.TryGetValue(kv.Key, out var p)) continue; // away (caravan, other map): restored when back
                    g.foodPrevPolicy.Remove(kv.Key);
                    if (raw != null && p.foodRestriction.CurrentFoodPolicy != raw) { theirs++; continue; } // changed meanwhile: theirs
                    var prev = db.AllFoodRestrictions.FirstOrDefault(f => f.id == kv.Value) ?? db.DefaultFoodRestriction();
                    p.foodRestriction.CurrentFoodPolicy = prev;
                    g.owned.Record("food:" + p.ThingID, prev.id.ToString());
                    restored++; report.Act(p.ThingID);
                }
                bool wasActive = g.foodSwitchActive;
                g.foodSwitchActive = false;
                if (wasActive || restored > 0)
                    parts.Add($"food: {meals} meals, restored {restored} policy(ies)" + (theirs > 0 ? $" ({theirs} kept the director's)" : "") + (g.foodPrevPolicy.Count > 0 ? $", {g.foodPrevPolicy.Count} away" : ""));
            }
        }

        static int CountMeals(Map map)
        {
            int n = 0;
            var list = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (!t.Spawned || t.def.defName.IndexOf("Meal", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                n += t.stackCount;
            }
            return n;
        }

        static FoodPolicy? FindRawPolicy(StewardGame g, FoodRestrictionDatabase db)
        {
            FoodPolicy? p = null;
            if (g.stewardRawPolicyId >= 0) p = db.AllFoodRestrictions.FirstOrDefault(f => f.id == g.stewardRawPolicyId);
            p ??= db.AllFoodRestrictions.FirstOrDefault(f => string.Equals(f.label, RawPolicyLabel, StringComparison.OrdinalIgnoreCase));
            if (p != null) g.stewardRawPolicyId = p.id;
            return p;
        }

        public static FoodPolicy EnsureRawPolicy(StewardGame g, FoodRestrictionDatabase db)
        {
            var existing = FindRawPolicy(g, db);
            if (existing != null) return existing;
            var pol = db.MakeNewFoodRestriction();
            pol.label = RawPolicyLabel;
            var filter = pol.filter;
            if (ThingCategoryDefOf.Corpses != null) filter.SetAllow(ThingCategoryDefOf.Corpses, false);
            if (ThingDefOf.Kibble != null) filter.SetAllow(ThingDefOf.Kibble, false);
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.ingestible?.sourceDef?.race?.FleshType != null && def.ingestible.sourceDef.race.FleshType == FleshTypeDefOf.Insectoid)
                    filter.SetAllow(def, false);
                else if (def.IsCorpse) filter.SetAllow(def, false);
            }
            if (ModsConfig.IdeologyActive && SpecialThingFilterDefOf.AllowInsectMeat != null) filter.SetAllow(SpecialThingFilterDefOf.AllowInsectMeat, false);
            g.stewardRawPolicyId = pol.id;
            StewardLog.Message($"orders: policies created food policy '{RawPolicyLabel}' (id {pol.id})");
            return pol;
        }

        // ── medical ──

        void RunMedical(Map map, StewardGame g, int tick, OrderReport report, List<string> parts)
        {
            int set = 0, manual = 0;
            void Apply(Pawn p, CareKind kind)
            {
                if (p.Dead || p.playerSettings == null) return;
                string key = "med:" + p.ThingID;
                if (StandingOrders.IsTouched(p.ThingID, MedScope)) { g.owned.MarkManual(key, tick, OwnedValues.TwoDays); manual++; return; }
                if (g.owned.IsManual(key, tick)) { manual++; return; }
                string cur = p.playerSettings.medCare.ToString();
                if (g.owned.Observe(key, cur, tick, OwnedValues.TwoDays)) { manual++; return; }
                string want = MedicalDefaults.For(kind);
                if (cur != want)
                {
                    p.playerSettings.medCare = (MedicalCareCategory)Enum.Parse(typeof(MedicalCareCategory), want);
                    set++; report.Act(p.ThingID);
                }
                g.owned.Record(key, want);
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList()) Apply(p, p.IsSlaveOfColony ? CareKind.Slave : CareKind.Colonist);
            foreach (var p in map.mapPawns.PrisonersOfColonySpawned.ToList()) Apply(p, CareKind.Prisoner);
            foreach (var p in map.mapPawns.SpawnedColonyAnimals.ToList()) Apply(p, CareKind.Animal);
            if (set > 0) parts.Add($"medical care set on {set}" + (manual > 0 ? $" ({manual} manual)" : ""));
        }

        // ── temperature ──

        void RunTemperature(Map map, StewardGame g, int tick, OrderReport report, List<string> parts)
        {
            var season = (SeasonKind)(byte)GenLocalDate.Season(map);
            int set = 0, manual = 0, total = 0;
            var alive = new HashSet<string>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var comp = b.TryGetComp<CompTempControl>();
                if (comp == null || comp.Props == null) continue;
                total++;
                string key = "temp:" + b.ThingID;
                alive.Add(key);
                bool heater = comp.Props.energyPerSecond > 0f;
                float want = TempTargets.Clamp(TempTargets.Target(season, heater), comp.Props.minTargetTemperature, comp.Props.maxTargetTemperature);
                string cur = comp.TargetTemperature.ToString("0.0");
                if (g.owned.IsManual(key, tick)) { manual++; continue; }
                // adopt only devices first seen at the game default; a freezer cooler is never ours; any outside change is final
                if (TempRules.WhyNot(g.owned.HasRecord(key), comp.TargetTemperature, comp.Props.defaultTargetTemperature, !heater) != null)
                {
                    g.owned.Record(key, cur);
                    g.owned.MarkManual(key, tick, OwnedValues.Forever);
                    manual++; continue;
                }
                if (g.owned.Observe(key, cur, tick, OwnedValues.Forever)) { manual++; continue; }
                if (Math.Abs(comp.TargetTemperature - want) > 0.05f)
                {
                    comp.TargetTemperature = want;
                    set++; report.Act(b.ThingID);
                }
                g.owned.Record(key, comp.TargetTemperature.ToString("0.0"));
            }
            // forget devices that are gone
            foreach (var k in g.owned.Set.Keys.Where(k => k.StartsWith("temp:") && !alive.Contains(k)).ToList()) g.owned.Forget(k);
            if (set > 0) parts.Add($"{set}/{total} heater/cooler target(s) → {season}" + (manual > 0 ? $" ({manual} manual)" : ""));
        }
    }
}
