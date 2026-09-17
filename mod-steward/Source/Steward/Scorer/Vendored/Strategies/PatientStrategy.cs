// Derived from Free Will Strategies/PatientStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for patient work type priority calculation.
    /// Patient work is always high priority when health is poor.
    /// </summary>
    public class PatientStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Patient");

        public override Priority CalculatePriority(Priority priority)
        {
            priority.Set(0.0f, "FreeWillPriorityPatientDefault".TranslateSimple);
            return priority
                .AlwaysDo("FreeWillPriorityPatientAlways".TranslateSimple)
                .ConsiderHealth()
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderOperation()
                .ConsiderColonyPolicy();
        }
    }
}
