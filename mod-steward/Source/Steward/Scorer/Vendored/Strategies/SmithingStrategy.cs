// Derived from Free Will Strategies/SmithingStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for smithing work type priority calculation.
    /// Smithing involves creating metal weapons, tools, and mechanical components.
    /// Priority includes mechanitor considerations like gestator finishing and mech repair.
    /// Priority decreases during food shortages as smithing is less critical than survival needs.
    /// Considers beauty expectations for high-quality crafted items.
    /// </summary>
    public class SmithingStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Smithing");

        public override Priority CalculatePriority(Priority priority)
        {
            return priority
                .ConsiderRelevantSkills()
                .ConsiderCarryingCapacity()
                .ConsiderFinishedMechGestators() // Mechanitor work - completed gestators need attention
                .ConsiderRepairingMech() // Mechanitor work - mech repair priority
                .ConsiderIsAnyoneElseDoing()
                .ConsiderBestAtDoing()
                .ConsiderBeautyExpectations() // High-quality crafted items improve colony beauty
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration()
                .ConsiderLowFood(-0.3f) // Smithing becomes less important when food is scarce
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