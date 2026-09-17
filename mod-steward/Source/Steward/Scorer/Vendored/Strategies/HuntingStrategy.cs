// Derived from Free Will Strategies/HuntingStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for hunting work type priority calculation.
    /// Hunting involves tracking and killing wild animals for food and resources, as well as fishing.
    /// Priority increases during food shortages as hunting provides essential meat.
    /// Considers combat abilities and movement speed but no longer requires ranged weapons
    /// due to fishing being part of hunting in RimWorld 1.6.
    /// </summary>
    public class HuntingStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Hunting");

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
                .ConsiderLowFood(0.3f) // Hunting becomes more important when food is scarce
                .ConsiderWeaponRange()
                .ConsiderColonistLeftUnburied()
                .ConsiderMovementSpeed() // Important for tracking animals
                .ConsiderHealth()
                .ConsiderAteRawFood()
                .ConsiderBored()
                .ConsiderBrawlersNotHunting() // Brawlers are less effective hunters
                .ConsiderFire()
                .ConsiderBuildingImmunity()
                .ConsiderCompletingTask()
                .ConsiderColonistsNeedingTreatment()
                .ConsiderDownedColonists()
                .ConsiderColonyPolicy();
        }
    }
}