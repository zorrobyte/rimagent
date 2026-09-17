// Derived from Free Will Strategies/HaulingStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for hauling work type priority calculation.
    /// Hauling involves moving items, resources, and materials around the colony.
    /// Priority increases during food shortages as hauling food supplies becomes critical.
    /// Considers carrying capacity, movement speed, and the need to move deteriorating items.
    /// Special attention to refueling needs and mech hauler availability.
    /// </summary>
    public class HaulingStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Hauling");

        public override Priority CalculatePriority(Priority priority)
        {
            // Base priority for hauling work - lower than essential work
            priority.Set(0.3f, "FreeWillPriorityHaulingDefault".TranslateSimple);

            return priority
                .ConsiderBeautyExpectations()
                .ConsiderCarryingCapacity() // Higher carrying capacity makes hauling more efficient
                .ConsiderIsAnyoneElseDoing()
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration()
                .ConsiderRefueling() // Refueling becomes priority when needed
                .ConsiderLowFood(0.2f) // Hauling food becomes more important when food is scarce
                .ConsiderColonistLeftUnburied()
                .ConsiderMovementSpeed() // Faster movement makes hauling more efficient
                .ConsiderHealth()
                .ConsiderAteRawFood()
                .ConsiderThingsDeteriorating() // Emergency priority for items about to spoil
                .ConsiderMechHaulers() // Consider if mech haulers can handle the work
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