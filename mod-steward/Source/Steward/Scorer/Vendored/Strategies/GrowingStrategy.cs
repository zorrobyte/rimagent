// Derived from Free Will Strategies/GrowingStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for growing work type priority calculation.
    /// Growing involves planting, tending, and harvesting crops for food production.
    /// Priority significantly increases during food shortages as crops are essential for survival.
    /// Considers agricultural skills and long-term colony food security.
    /// </summary>
    public class GrowingStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Growing");

        public override Priority CalculatePriority(Priority priority)
        {
            return priority
                .ConsiderRelevantSkills()
                .ConsiderCarryingCapacity()
                .ConsiderIsAnyoneElseDoing()
                .ConsiderBestAtDoing()
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration()
                .ConsiderLowFood(0.3f) // Growing becomes critical when food is scarce
                .ConsiderColonistLeftUnburied()
                .ConsiderHealth()
                .ConsiderAteRawFood()
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