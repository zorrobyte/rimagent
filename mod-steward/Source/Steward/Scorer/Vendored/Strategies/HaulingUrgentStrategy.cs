// Derived from Free Will Strategies/HaulingUrgentStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for urgent hauling work type priority calculation.
    /// Urgent hauling handles time-critical transportation needs like medical supplies or food.
    /// Priority increases more than regular hauling during food shortages due to urgency.
    /// Considers carrying capacity, movement speed, and deteriorating items that need immediate attention.
    /// Special attention to refueling emergencies and critical supply movements.
    /// This is typically a modded work type for high-priority hauling tasks.
    /// </summary>
    public class HaulingUrgentStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("HaulingUrgent");

        public override Priority CalculatePriority(Priority priority)
        {
            // Base priority for urgent hauling - higher than regular hauling
            priority.Set(0.5f, "FreeWillPriorityUrgentHaulingDefault".TranslateSimple);

            return priority
                .ConsiderBeautyExpectations()
                .ConsiderCarryingCapacity() // Critical for efficient urgent transport
                .ConsiderIsAnyoneElseDoing()
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration()
                .ConsiderRefueling() // Emergency refueling has highest priority
                .ConsiderLowFood(0.3f) // Urgent hauling has higher priority increase than regular hauling during food crisis
                .ConsiderColonistLeftUnburied()
                .ConsiderMovementSpeed() // Speed is critical for urgent tasks
                .ConsiderHealth()
                .ConsiderAteRawFood()
                .ConsiderThingsDeteriorating() // Immediate priority for items about to spoil
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