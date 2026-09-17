// Derived from Free Will Strategies/BasicWorkerStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for basic worker work type priority calculation.
    /// Basic worker covers essential colony maintenance tasks.
    /// </summary>
    public class BasicWorkerStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("BasicWorker");

        public override Priority CalculatePriority(Priority priority)
        {
            priority.Set(0.5f, "FreeWillPriorityBasicWorkDefault".TranslateSimple);
            return priority
                .ConsiderThoughts()
                .ConsiderHealth()
                .ConsiderLowFood(-0.3f)
                .ConsiderBored()
                .NeverDoIf(priority.pawn.Downed, "FreeWillPriorityPawnDowned".TranslateSimple)
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderColonyPolicy();
        }
    }
}
