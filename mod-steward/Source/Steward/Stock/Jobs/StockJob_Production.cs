// Written for RimBridge (2026): a stock job that keeps one Bill_Production (repeat mode TargetCount) for a recipe on a
// suitable work table, its targetCount following the job's effective stock target. Products are counted through the
// same ProductCounter/Trigger_Threshold the designation jobs use.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Stock
{
    public class StockJob_Production : StockJob
    {
        public sealed class WorkData
        {
            public Building? Table;
            public Bill_Production? Existing;
            public List<Bill_Production> Duplicates = new List<Bill_Production>();
            public int Target;
        }

        public RecipeDef? Recipe;
        private Building? _table;        // preferred table (may be null: any table that can do the recipe)
        private Bill_Production? _bill;  // the bill this job owns

        public StockJob_Production() { }

        public StockJob_Production(Map map, RecipeDef recipe, Building? table = null) : base(map)
        {
            Recipe = recipe;
            _table = table;
            Label = $"Production ({recipe.ProducedThingDef?.label ?? recipe.label})";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            ConfigureFilters();
        }

        public override string Kind => "Production";
        public override WorkTypeDef? WorkType => Recipe?.requiredGiverWorkType;
        public override DesignationDef? DesignationDef => null;
        public override IEnumerable<string> Targets => Products.Select(d => d.label);
        public override bool IsOutdoorWork => false;

        public Building? Table => _table;
        public Bill_Production? Bill => _bill;
        public void SetTable(Building? table) => _table = table;

        public IEnumerable<ThingDef> Products =>
            Recipe?.products?.Where(p => p?.thingDef != null).Select(p => p.thingDef) ?? Enumerable.Empty<ThingDef>();

        private void ConfigureFilters()
        {
            var products = Products.ToList();
            Trigger.SetParentFilter(f => { foreach (var d in products) f.SetAllow(d, true); });
            foreach (var d in products) Trigger.ThresholdFilter.SetAllow(d, true);
        }

        /// <summary>A spawned player building with a bill stack whose def lists this recipe.</summary>
        public bool CanUse(Thing? t) =>
            Recipe != null && t is Building b && b.Spawned && !b.Destroyed && b.Faction == Faction.OfPlayer
            && t is IBillGiver && t.def.AllRecipes != null && t.def.AllRecipes.Contains(Recipe);

        public IEnumerable<Building> CandidateTables => Map.listerBuildings.allBuildingsColonist.Where(CanUse);

        public override object? Gather()
        {
            if (Recipe == null) { Note("no recipe"); return null; }
            var data = new WorkData { Target = EffectiveTarget };
            foreach (var table in CandidateTables)
            {
                foreach (var bill in ((IBillGiver)table).BillStack.Bills)
                {
                    if (!(bill is Bill_Production bp) || bp.recipe != Recipe) continue;
                    if (data.Existing == null) data.Existing = bp;
                    else if (bp == _bill) { data.Duplicates.Add(data.Existing); data.Existing = bp; }
                    else data.Duplicates.Add(bp);
                }
            }
            data.Table = _table != null && CanUse(_table) ? _table
                : data.Existing?.billStack?.billGiver as Building
                ?? CandidateTables.OrderBy(b => ((IBillGiver)b).BillStack.Count).FirstOrDefault();
            if (data.Existing == null && data.Table == null)
            {
                RunsWithoutTargets++;
                Note($"no work table can make {Recipe.label}");
                return null;
            }
            RunsWithoutTargets = 0;
            return data;
        }

        public override bool Execute(object dataObj)
        {
            if (!(dataObj is WorkData data) || Recipe == null) return false;
            bool changed = false;
            var bill = data.Existing;
            if (bill == null)
            {
                bill = Recipe.MakeNewBill() as Bill_Production;
                if (bill == null) { Note($"{Recipe.defName} does not make a production bill"); return false; }
                var giver = (IBillGiver)data.Table!;
                if (giver.BillStack.Count >= BillStack.MaxCount) { Note($"{data.Table!.LabelCap} already has {BillStack.MaxCount} bills"); return false; }
                giver.BillStack.AddBill(bill);
                changed = true;
                Note($"added bill {Recipe.label} on {data.Table!.LabelCap} (target {data.Target})");
            }
            foreach (var dup in data.Duplicates)
            {
                try { dup.billStack?.Delete(dup); changed = true; }
                catch (Exception ex) { StewardLog.Warning($"production: could not remove duplicate bill: {ex.Message}"); }
            }
            if (data.Duplicates.Count > 0) Note($"removed {data.Duplicates.Count} duplicate {Recipe.label} bills");
            if (bill.repeatMode != BillRepeatModeDefOf.TargetCount) { bill.repeatMode = BillRepeatModeDefOf.TargetCount; changed = true; }
            if (bill.targetCount != data.Target) { bill.targetCount = data.Target; changed = true; }
            if (bill.suspended) { bill.suspended = false; changed = true; }
            if (bill.pauseWhenSatisfied) { bill.pauseWhenSatisfied = false; changed = true; }
            _bill = bill;
            _table = bill.billStack?.billGiver as Building ?? _table;
            if (!changed) LastRunSummary = $"bill ok ({Trigger.GetCurrentCount()} / {data.Target})";
            return changed;
        }

        public override void CleanUp()
        {
            base.CleanUp();
            if (_bill == null) return;
            try { _bill.billStack?.Delete(_bill); }
            catch (Exception ex) { StewardLog.Warning($"production: could not delete bill: {ex.Message}"); }
            _bill = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref Recipe, "recipe");
            Scribe_References.Look(ref _table, "table");
            Scribe_References.Look(ref _bill, "bill");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Recipe != null && Trigger != null && Trigger.ThresholdFilter.AllowedDefCount == 0) ConfigureFilters();
                if (Recipe != null && string.IsNullOrEmpty(Label)) Label = $"Production ({Recipe.ProducedThingDef?.label ?? Recipe.label})";
            }
        }
    }
}
