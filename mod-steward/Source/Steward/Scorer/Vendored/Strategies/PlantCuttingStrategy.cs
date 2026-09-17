// Derived from Free Will Strategies/PlantCuttingStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for plant cutting work type priority calculation.
    /// Plant cutting involves harvesting trees for wood and clearing plant growth.
    /// Priority increases during food shortages as some plants provide food or materials.
    /// Considers Gauranlen tree pruning needs and plant blight emergency situations.
    /// </summary>
    public class PlantCuttingStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("PlantCutting");

        public override Priority CalculatePriority(Priority priority)
        {
            return priority
                .ConsiderRelevantSkills()
                .ConsiderIsAnyoneElseDoing()
                .ConsiderBestAtDoing()
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration()
                .ConsiderGauranlenPruning() // Special consideration for Gauranlen tree maintenance
                .ConsiderLowFood(0.3f) // Some plants provide food or materials needed for survival
                .ConsiderHealth()
                .ConsiderPlantsBlighted() // Emergency priority when plants are diseased
                .ConsiderBored()
                .ConsiderFire()
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderColonyPolicy();
        }
    }
}