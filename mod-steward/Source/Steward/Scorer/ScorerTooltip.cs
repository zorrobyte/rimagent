// Derived from Free Will FreeWillUtility.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using RimWorld;
using System;
using System.Text;
using UnityEngine;
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>Human-readable breakdown of a computed priority (Free Will's tooltip, minus the UI patch).</summary>
    public static class ScorerTooltip
    {
        private static readonly int couldNotGetAdjustmentString = "FreewillCouldNotGetAdjustmentString".GetHashCode();
        private static readonly int couldNotGetTip = "FreewillCouldNotGetTip".GetHashCode();

        public static bool IsIncapableOfWholeWorkType(Pawn p, WorkTypeDef work)
        {
            for (int i = 0; i < work.workGiversByPriority.Count; i++)
            {
                bool flag = true;
                for (int j = 0; j < work.workGiversByPriority[i].requiredCapacities.Count; j++)
                {
                    PawnCapacityDef capacity = work.workGiversByPriority[i].requiredCapacities[j];
                    if (!p.health.capacities.CapableOf(capacity))
                    {
                        flag = false;
                        break;
                    }
                }
                if (flag)
                {
                    return false;
                }
            }
            return true;
        }

        public static string GetTip(Priority priority)
        {
            try
            {
                StringBuilder stringBuilder = new StringBuilder();
                TaggedString workTypeTitle = priority.WorkTypeDef.pawnLabel.CapitalizeFirst().AsTipTitle();
                stringBuilder = stringBuilder.AppendLineTagged(workTypeTitle)
                    .AppendLineTagged(priority.WorkTypeDef.description.Colorize(ColoredText.SubtleGrayColor)).AppendLine()
                    .AppendLineTagged(("FreeWillWorkPreference".Translate().CapitalizeFirst() + ": ").AsTipTitle() + priority.Value.ToStringPercent());
                foreach (Func<string> ProduceAdjustmentString in priority.AdjustmentStrings)
                {
                    try
                    {
                        string adjustmentString = ProduceAdjustmentString();
                        stringBuilder = stringBuilder.AppendLine(adjustmentString);
                    }
                    catch (Exception e)
                    {
                        StewardLog.ErrorOnce("scorer: could not get adjustment string: " + e.Message, couldNotGetAdjustmentString);
                        stringBuilder = Prefs.DevMode ? stringBuilder.AppendLine("error: " + e.Message) : stringBuilder.AppendLine("error");
                    }
                }
                stringBuilder = stringBuilder.AppendLine();
                if (!priority.Disabled)
                {
                    int p = priority.ToGamePriority();
                    string priorityDescriptionStr = string.Format("Priority{0}", p).TranslateSimple();
                    string priorityLevelStr = p + " - " + priorityDescriptionStr;
                    TaggedString colorizedPriorityLevelStr = priorityLevelStr.Colorize(WidgetsWork.ColorOfPriority(p));
                    TaggedString priorityTitle = ("Priority".Translate().CapitalizeFirst() + ": ").AsTipTitle();
                    stringBuilder = stringBuilder.AppendLineTagged(priorityTitle + colorizedPriorityLevelStr);
                }
                return stringBuilder.ToString();
            }
            catch (Exception e)
            {
                StewardLog.ErrorOnce("scorer: could not get tip: " + e.Message, couldNotGetTip);
                return "could not get tip";
            }
        }
    }
}
