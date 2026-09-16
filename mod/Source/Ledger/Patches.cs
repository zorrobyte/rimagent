using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimBridge.Ledger
{
    // Every patch is a postfix wrapped in try/catch: the ledger must never break the game.

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
    static class Patch_Letter
    {
        static void Postfix(Letter let)
        {
            try
            {
                string text = let is ChoiceLetter cl ? cl.Text.ToString().StripTags() : "";
                var data = new JObject { ["label"] = let.Label.ToString().StripTags(), ["def"] = let.def?.defName, ["text"] = text, ["id"] = let.ID };
                if (let is ChoiceLetter cl2 && cl2.quest != null) data["quest"] = cl2.quest.id;
                IntVec3? cell = null; string? tid = null;
                if (let.lookTargets != null && let.lookTargets.IsValid())
                {
                    var t = let.lookTargets.PrimaryTarget;
                    if (t.HasThing) { tid = t.Thing.ThingID; cell = t.Thing.PositionHeld; }
                    else if (t.Cell.IsValid) cell = t.Cell;
                }
                EventLedger.Add("letter", let.Label.ToString().StripTags(), data, cell, tid);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger letter: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
    static class Patch_Message
    {
        static void Postfix(Message msg)
        {
            try
            {
                if (msg.def == MessageTypeDefOf.SilentInput || msg.def == MessageTypeDefOf.RejectInput) return;
                IntVec3? cell = null; string? tid = null;
                if (msg.lookTargets != null && msg.lookTargets.IsValid())
                {
                    var t = msg.lookTargets.PrimaryTarget;
                    if (t.HasThing) { tid = t.Thing.ThingID; cell = t.Thing.PositionHeld; }
                    else if (t.Cell.IsValid) cell = t.Cell;
                }
                EventLedger.Add("message", msg.text.StripTags(), new JObject { ["type"] = msg.def?.defName }, cell, tid);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger message: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    static class Patch_Incident
    {
        static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            try
            {
                if (!__result) return;
                var d = new JObject
                {
                    ["def"] = __instance.def?.defName,
                    ["category"] = __instance.def?.category?.defName,
                    ["points"] = parms?.points,
                    ["faction"] = parms?.faction?.Name,
                    ["strategy"] = parms?.raidStrategy?.defName,
                    ["arrival"] = parms?.raidArrivalMode?.defName,
                };
                EventLedger.Add("incident", __instance.def?.label ?? __instance.def?.defName ?? "incident", d);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger incident: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    static class Patch_Kill
    {
        static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            try
            {
                if (__instance.Faction != Faction.OfPlayer && !(__instance.HostileTo(Faction.OfPlayer))) return;
                bool colonist = __instance.IsColonist;
                var d = new JObject
                {
                    ["colonist"] = colonist,
                    ["faction"] = __instance.Faction?.Name,
                    ["kind"] = __instance.kindDef?.defName,
                    ["cause"] = dinfo?.Def?.defName,
                    ["by"] = (dinfo?.Instigator as Thing)?.ThingID,
                };
                EventLedger.Add(colonist ? "colonist_died" : "pawn_died", $"{__instance.LabelShortCap} died", d, __instance.PositionHeld, __instance.ThingID);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger kill: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    static class Patch_Downed
    {
        static void Postfix(Pawn ___pawn, DamageInfo? dinfo)
        {
            try
            {
                if (___pawn == null || !___pawn.IsColonist) return;
                EventLedger.Add("colonist_downed", $"{___pawn.LabelShortCap} downed", new JObject { ["cause"] = dinfo?.Def?.defName }, ___pawn.PositionHeld, ___pawn.ThingID);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger downed: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    static class Patch_MentalState
    {
        static void Postfix(Pawn ___pawn, MentalStateDef stateDef, string reason, bool __result)
        {
            try
            {
                if (!__result || ___pawn == null || !___pawn.IsColonist) return;
                EventLedger.Add("mental_break", $"{___pawn.LabelShortCap}: {stateDef.label}", new JObject { ["def"] = stateDef.defName, ["reason"] = reason }, ___pawn.PositionHeld, ___pawn.ThingID);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger mental: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    static class Patch_Research
    {
        static void Postfix(ResearchProjectDef proj)
        {
            try { EventLedger.Add("research_finished", proj.label, new JObject { ["def"] = proj.defName }); }
            catch (Exception ex) { BridgeLog.Warning("ledger research: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    static class Patch_Construction
    {
        static void Postfix(Frame __instance, Pawn worker)
        {
            try
            {
                if (__instance.Faction != Faction.OfPlayer) return;
                var def = __instance.def.entityDefToBuild;
                EventLedger.Add("built", $"{def?.label} built by {worker?.LabelShort}", new JObject { ["def"] = def?.defName, ["by"] = worker?.ThingID }, __instance.Position);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger built: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.Destroy))]
    static class Patch_BuildingDestroyed
    {
        static void Prefix(Building __instance, DestroyMode mode)
        {
            try
            {
                if (__instance.Faction != Faction.OfPlayer || mode == DestroyMode.Deconstruct || mode == DestroyMode.Vanish || mode == DestroyMode.Cancel) return;
                if (__instance is Frame || __instance is Blueprint)
                {
                    if (mode == DestroyMode.FailConstruction)
                        EventLedger.Add("construction_failed", $"{__instance.LabelCap} failed (materials lost)", new JObject { ["def"] = __instance.def.defName }, __instance.Position);
                    return;
                }
                if (!__instance.def.building.isNaturalRock && __instance.def.category == ThingCategory.Building)
                    EventLedger.Add("building_lost", $"{__instance.LabelCap} destroyed ({mode})", new JObject { ["def"] = __instance.def.defName, ["mode"] = mode.ToString() }, __instance.Position, __instance.ThingID);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger destroyed: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Add))]
    static class Patch_Quest
    {
        static void Postfix(Quest quest)
        {
            try
            {
                if (quest.hidden) return;
                EventLedger.Add("quest", quest.name, new JObject { ["id"] = quest.id, ["state"] = quest.State.ToString() });
            }
            catch (Exception ex) { BridgeLog.Warning("ledger quest: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(LordManager), nameof(LordManager.AddLord))]
    static class Patch_LordAdd
    {
        static void Postfix(Lord newLord)
        {
            try
            {
                if (newLord.faction == null || !newLord.faction.HostileTo(Faction.OfPlayer)) return;
                EventLedger.Add("hostile_group", $"hostile group: {newLord.faction.Name} x{newLord.ownedPawns.Count} ({newLord.LordJob?.GetType().Name})",
                    new JObject { ["faction"] = newLord.faction.Name, ["count"] = newLord.ownedPawns.Count, ["job"] = newLord.LordJob?.GetType().Name, ["lord"] = newLord.loadID });
            }
            catch (Exception ex) { BridgeLog.Warning("ledger lord: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(LordManager), nameof(LordManager.RemoveLord))]
    static class Patch_LordRemove
    {
        static void Postfix(Lord oldLord)
        {
            try
            {
                if (oldLord.faction == null || !oldLord.faction.HostileTo(Faction.OfPlayer)) return;
                EventLedger.Add("hostile_group_gone", $"hostile group gone: {oldLord.faction.Name} ({oldLord.LordJob?.GetType().Name})",
                    new JObject { ["faction"] = oldLord.faction.Name, ["job"] = oldLord.LordJob?.GetType().Name, ["lord"] = oldLord.loadID });
            }
            catch (Exception ex) { BridgeLog.Warning("ledger lord: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    static class Patch_SetFaction
    {
        static void Prefix(Pawn __instance, Faction newFaction, out bool __state)
        {
            __state = __instance.Faction == Faction.OfPlayer;
        }
        static void Postfix(Pawn __instance, Faction newFaction, bool __state)
        {
            try
            {
                if (!__instance.RaceProps.Humanlike) return;
                bool now = newFaction == Faction.OfPlayer;
                if (now && !__state) EventLedger.Add("colonist_joined", $"{__instance.LabelShortCap} joined the colony", null, __instance.PositionHeld, __instance.ThingID);
                else if (!now && __state) EventLedger.Add("colonist_left", $"{__instance.LabelShortCap} left the colony", new JObject { ["to"] = newFaction?.Name }, __instance.PositionHeld, __instance.ThingID);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger faction: " + ex.Message); }
        }
    }

    /// <summary>Daily snapshot + new-game marker.</summary>
    public class LedgerComponent : GameComponent
    {
        private int _lastDay = -1;
        public LedgerComponent(Game game) { }

        public override void FinalizeInit()
        {
            EventLedger.Add("game", "game loaded/started", new JObject { ["day"] = GenDate.DaysPassed });
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            try
            {
                int day = GenDate.DaysPassed;
                if (day == _lastDay) return;
                _lastDay = day;
                var map = Find.CurrentMap;
                if (map == null) return;
                var snap = State.Snapshot.Daily(map);
                EventLedger.Add("day", $"day {day}", snap);
            }
            catch (Exception ex) { BridgeLog.Warning("daily snapshot: " + ex.Message); }
        }
    }
}
