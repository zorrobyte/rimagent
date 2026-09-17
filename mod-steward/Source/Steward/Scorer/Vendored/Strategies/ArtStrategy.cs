// Derived from Free Will Strategies/ArtStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Strategy for art work type priority calculation.
    /// Art involves creating sculptures, paintings, and decorative items to improve colony beauty.
    /// Priority increases when colonists have high beauty expectations.
    /// Priority decreases during food shortages as art is a luxury compared to survival needs.
    /// Considers artistic skills and inspiration for creating masterwork pieces.
    /// </summary>
    public class ArtStrategy : BaseWorkTypeStrategy
    {
        public override WorkTypeDef WorkType => GetWorkTypeDef("Art");

        public override Priority CalculatePriority(Priority priority)
        {
            return priority
                .ConsiderRelevantSkills()
                .ConsiderCarryingCapacity()
                .ConsiderIsAnyoneElseDoing()
                .ConsiderBestAtDoing()
                .ConsiderBeautyExpectations() // Art is more important when colonists expect beauty
                .ConsiderPassion()
                .ConsiderThoughts()
                .ConsiderInspiration() // Inspiration can create masterwork art
                .ConsiderLowFood(-0.3f) // Art becomes less important when food is scarce
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