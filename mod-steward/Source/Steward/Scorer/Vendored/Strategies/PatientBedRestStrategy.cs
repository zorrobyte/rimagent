// Derived from Free Will Strategies/PatientBedRestStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for patient bed rest work type priority calculation.
    /// Bedrest is used when pawns need to rest for recovery.
    /// </summary>
    public class PatientBedRestStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("PatientBedRest");

        public override Priority CalculatePriority(Priority priority)
        {
            priority.Set(0.0f, "FreeWillPriorityBedrestDefault".TranslateSimple);
            priority.AlwaysDo("FreeWillPriorityBedrestAlways".TranslateSimple)
                .ConsiderHealth()
                .ConsiderBuildingImmunity()
                .ConsiderLowFood(-0.2f)
                .ConsiderCompletingTask()
                .ConsiderBored()
                .ConsiderHavingFoodPoisoning();
            _ = priority.ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderOperation()
                .ConsiderColonyPolicy();
            return priority;
        }
    }
}
