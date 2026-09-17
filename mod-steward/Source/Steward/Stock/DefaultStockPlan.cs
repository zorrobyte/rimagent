// Written for Autopilot (2026, MIT) as part of the synchronous Colony Manager Redux rewrite; modified for RimBridge (2026).
using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>Default stock targets so the stock layer does useful work with no LLM at all.</summary>
    public static class DefaultStockPlan
    {
        public static int WoodTarget(int colonists) => 500 + 100 * Math.Max(0, colonists - 3);
        public static int ForagingTarget(int colonists) => 150 + 25 * colonists;
        public static int MeatTarget(int colonists) => 300 + 75 * colonists;
        public static int LeatherTarget(int colonists) => 100;
        public static int SteelTarget(int colonists) => 300 + 50 * Math.Max(0, colonists - 3);
        public static int MealsTarget(int colonists) => 10 + 4 * colonists;
        public const string DefaultMealRecipe = "CookMealSimple";

        public static void Apply(StockComponent comp)
        {
            var map = comp.map;
            int n = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            try
            {
                var forestry = comp.Add(new StockJob_Forestry(map));
                forestry.Trigger.TargetCount = WoodTarget(n);
                forestry.AllowSaplings = false;
            }
            catch (Exception ex) { StewardLog.Warning($"stock: default forestry job failed: {ex}"); }
            StockJobFactories.AddDefaults(comp, n);
            StewardLog.Message($"stock: default stock plan applied for {n} colonists on {map}");
        }

        /// <summary>
        /// Adds the "simple meals" production job (target 10 + 4*colonists) once any stove/campfire that can cook the
        /// recipe exists. Returns true when the job exists (already or now) so the caller stops checking.
        /// </summary>
        public static bool EnsureProduction(StockComponent comp, int colonists)
        {
            var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(DefaultMealRecipe);
            if (recipe == null) return true;
            if (comp.JobsOfType<StockJob_Production>().Any(j => j.Recipe == recipe)) return true;
            var map = comp.map;
            bool anyStove = map.listerBuildings.allBuildingsColonist.Any(b => b is IBillGiver && b.def.AllRecipes != null && b.def.AllRecipes.Contains(recipe));
            if (!anyStove) return false;
            var job = comp.Add(new StockJob_Production(map, recipe));
            job.Label = "Production (simple meals)";
            job.Trigger.TargetCount = MealsTarget(Math.Max(1, colonists));
            job.UpdateIntervalTicks = 2500;
            StewardLog.Message($"stock: default simple-meals production job added (target {job.Trigger.TargetCount})");
            return true;
        }

        /// <summary>Re-derive targets for jobs that still carry the auto-scaled default.</summary>
        public static void Rescale(StockComponent comp, int colonists)
        {
            int n = Math.Max(1, colonists);
            foreach (var job in comp.Jobs)
            {
                if (!job.AutoScaled) continue;
                switch (job.Kind)
                {
                    case "Forestry": job.Trigger.TargetCount = WoodTarget(n); break;
                    case "Foraging": job.Trigger.TargetCount = ForagingTarget(n); break;
                    case "Hunting": job.Trigger.TargetCount = MeatTarget(n); break;
                    case "HuntingLeather": job.Trigger.TargetCount = LeatherTarget(n); break;
                    case "Mining": job.Trigger.TargetCount = SteelTarget(n); break;
                    case "Production":
                        if (job is StockJob_Production pj && pj.Recipe?.defName == DefaultMealRecipe) job.Trigger.TargetCount = MealsTarget(n);
                        break;
                }
            }
        }
    }

    /// <summary>Hooks for job types added in later files (foraging, hunting, mining) without touching DefaultStockPlan.</summary>
    public static partial class StockJobFactories
    {
        public static void AddDefaults(StockComponent comp, int colonists)
        {
            AddForaging(comp, colonists);
            AddHunting(comp, colonists);
            AddMining(comp, colonists);
        }

        static partial void AddForagingImpl(StockComponent comp, int colonists);
        static partial void AddHuntingImpl(StockComponent comp, int colonists);
        static partial void AddMiningImpl(StockComponent comp, int colonists);

        private static void AddForaging(StockComponent comp, int colonists) { try { AddForagingImpl(comp, colonists); } catch (Exception ex) { StewardLog.Warning($"stock: default foraging job failed: {ex}"); } }
        private static void AddHunting(StockComponent comp, int colonists) { try { AddHuntingImpl(comp, colonists); } catch (Exception ex) { StewardLog.Warning($"stock: default hunting job failed: {ex}"); } }
        private static void AddMining(StockComponent comp, int colonists) { try { AddMiningImpl(comp, colonists); } catch (Exception ex) { StewardLog.Warning($"stock: default mining job failed: {ex}"); } }
    }
}
