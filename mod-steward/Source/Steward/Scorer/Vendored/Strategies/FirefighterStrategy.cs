// Derived from Free Will Strategies/FirefighterStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for firefighting work type priority calculation.
    /// Firefighting is always enabled and considers fire-related factors.
    /// </summary>
    public class FirefighterStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Firefighter");

        public override Priority CalculatePriority(Priority priority)
        {
            priority.Set(0.0f, "FreeWillPriorityFirefightingDefault".TranslateSimple);
            return priority
                .AlwaysDo("FreeWillPriorityFirefightingAlways".TranslateSimple)
                .NeverDoIf(priority.pawn.Downed, "FreeWillPriorityPawnDowned".TranslateSimple)
                .ConsiderFire()
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderColonyPolicy();
        }
    }
}
