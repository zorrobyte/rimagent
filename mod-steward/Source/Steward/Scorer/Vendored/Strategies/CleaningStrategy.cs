// Derived from Free Will Strategies/CleaningStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for cleaning work type priority calculation.
    /// Cleaning involves maintaining hygiene and cleanliness to prevent disease and improve mood.
    /// Priority increases when colonists have high beauty expectations.
    /// Priority decreases during food shortages as cleaning is less critical than survival.
    /// Only performs cleaning within the home area for efficiency.
    /// Special consideration for food poisoning risk areas like kitchens.
    /// </summary>
    public class CleaningStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Cleaning");

        public override Priority CalculatePriority(Priority priority)
        {
            // Base priority for cleaning work - moderate importance for colony maintenance
            priority.Set(0.5f, "FreeWillPriorityCleaningDefault".TranslateSimple);

            return priority
                .ConsiderBeautyExpectations() // Cleaning is more important when colonists expect beauty
                .ConsiderIsAnyoneElseDoing()
                .ConsiderThoughts()
                .ConsiderOwnRoom() // Higher priority for cleaning own bedroom
                .ConsiderLowFood(-0.2f) // Cleaning becomes less important when food is scarce
                .ConsiderFoodPoisoningRisk() // Critical in food preparation areas
                .ConsiderHealth()
                .ConsiderBored()
                .NeverDoIf(priority.NotInHomeArea(priority.pawn), "FreeWillPriorityNotInHomeArea".TranslateSimple) // Only clean in home area
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderColonyPolicy();
        }
    }
}