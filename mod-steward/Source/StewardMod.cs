using HarmonyLib;
using RimBridge.Server;
using UnityEngine;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>Top-level settings for this mod (was nested under RimBridge's own BridgeSettings before the
    /// Steward layer moved out to its own mod). Same shape as before: scorer + stock config.</summary>
    public class StewardModSettings : ModSettings
    {
        public StewardSettings steward = new StewardSettings();

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref steward, "steward");
            steward ??= new StewardSettings();
        }
    }

    /// <summary>Optional add-on for RimBridge: a work-priority scorer (Free Will) and synchronous stock-job
    /// manager (Colony Manager) that run every tick without an LLM in the loop, plus standing orders. Registers
    /// its own steward.* RPCs onto RimBridge's dispatcher via Rpc.RegisterAssembly, and hooks into RimBridge's
    /// ui.*/ledger/state RPCs via RimBridge.Server.Hooks instead of RimBridge knowing this mod exists.</summary>
    public class StewardMod : Mod
    {
        public static StewardSettings Settings = null!;
        private static StewardMod _instance = null!;
        public static void Save() => _instance.WriteSettings();

        public StewardMod(ModContentPack content) : base(content)
        {
            _instance = this;
            Settings = GetSettings<StewardModSettings>().steward;
            new Harmony("zorrobyte.rimagent-steward").PatchAll();
            Rpc.RegisterAssembly(typeof(StewardMod).Assembly);

            Hooks.ManualTouch += (thing, reason) => Orders.StandingOrders.Touch(thing, reason);
            Hooks.PawnWorkSetManually += pawn => ScorerGate.SetManaged(pawn, false);
            Hooks.ResearchProjectFinished += StewardResearch.Notify_ProjectFinished;
            Hooks.SummaryContributors.Add(map => ("steward", StewardRpc.SummaryBlock(map)));

            BridgeLog.Message("steward add-on loaded (scorer + stock + standing orders)");
        }

        public override string SettingsCategory() => "RimBridge: Steward";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            l.CheckboxLabeled("Steward: score work priorities for managed colonists (default on)", ref Settings.scorer.Enabled);
            l.CheckboxLabeled("Steward: run stock jobs (forestry, foraging, hunting, mining, production)", ref Settings.stock.Enabled);
            l.End();
        }
    }
}
