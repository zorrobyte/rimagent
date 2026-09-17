// Derived from Free Will DependencyInterfaces.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;

using RimBridge.Steward;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Interface for providing world-level game state data to Priority calculations.
    /// This abstraction allows for better testability and decoupling from RimWorld's component system.
    /// </summary>
    public interface IWorldStateProvider
    {
        /// <summary>
        /// Gets the mod settings for Free Will.
        /// </summary>
        ScorerSettings Settings { get; }
    }    /// <summary>
         /// Interface for providing map-level game state data to Priority calculations.
         /// This abstraction allows for better testability and decoupling from RimWorld's component system.
         /// </summary>
    public interface IMapStateProvider
    {
        /// <summary>
        /// Gets the list of disabled work types for this map.
        /// </summary>
        List<WorkTypeDef> DisabledWorkTypes { get; }

        /// <summary>
        /// Gets all thoughts affecting pawns on this map.
        /// </summary>
        List<Thought> AllThoughts { get; }

        /// <summary>
        /// Gets whether the map needs warm clothes alert is active.
        /// </summary>
        bool AlertNeedWarmClothes { get; }

        /// <summary>
        /// Gets whether animals are roaming alert is active.
        /// </summary>
        bool AlertAnimalRoaming { get; }

        /// <summary>
        /// Gets the current suppression need value.
        /// </summary>
        float SuppressionNeed { get; }

        /// <summary>
        /// Gets whether colonist left unburied alert is active.
        /// </summary>
        bool AlertColonistLeftUnburied { get; }

        /// <summary>
        /// Gets the percentage of pawns currently downed.
        /// </summary>
        float PercentPawnsDowned { get; }

        /// <summary>
        /// Gets whether refuel is needed immediately.
        /// </summary>
        bool RefuelNeededNow { get; }

        /// <summary>
        /// Gets whether refuel is needed.
        /// </summary>
        bool RefuelNeeded { get; }

        /// <summary>
        /// Gets whether there's a fire in the home area.
        /// </summary>
        bool HomeFire { get; }

        /// <summary>
        /// Gets the number of fires on the map.
        /// </summary>
        int MapFires { get; }

        /// <summary>
        /// Gets the percentage of pawns needing treatment.
        /// </summary>
        float PercentPawnsNeedingTreatment { get; }

        /// <summary>
        /// Gets the number of pets needing treatment.
        /// </summary>
        float NumPetsNeedingTreatment { get; }

        /// <summary>
        /// Gets the number of prisoners needing treatment.
        /// </summary>
        float NumPrisonersNeedingTreatment { get; }

        /// <summary>
        /// Gets the number of pawns on the map.
        /// </summary>
        int NumPawns { get; }

        /// <summary>
        /// Gets the percentage of pawns that are mech haulers.
        /// </summary>
        float PercentPawnsMechHaulers { get; }

        /// <summary>
        /// Gets whether animal pen is needed alert is active.
        /// </summary>
        bool AlertAnimalPenNeeded { get; }

        /// <summary>
        /// Gets whether animal pen not enclosed alert is active.
        /// </summary>
        bool AlertAnimalPenNotEnclosed { get; }

        /// <summary>
        /// Gets whether mech damaged alert is active.
        /// </summary>
        bool AlertMechDamaged { get; }

        /// <summary>
        /// Gets whether low food alert is active.
        /// </summary>
        bool AlertLowFood { get; }

        /// <summary>
        /// Gets things that are deteriorating on the map.
        /// </summary>
        Thing ThingsDeteriorating { get; }

        /// <summary>
        /// Gets whether plants are blighted on the map.
        /// </summary>
        bool PlantsBlighted { get; }

        /// <summary>
        /// Gets whether the area has haulables.
        /// </summary>
        bool AreaHasHaulables { get; }

        /// <summary>
        /// Gets whether the area has filth.
        /// </summary>
        bool AreaHasFilth { get; }

        /// <summary>
        /// Gets the last tick when the specified pawn was bored.
        /// </summary>
        /// <param name="pawn">The pawn to check.</param>
        /// <returns>The last bored tick, or null if never bored.</returns>
        int? GetLastBored(Pawn pawn);

        /// <summary>
        /// Updates the last bored time for the specified pawn.
        /// </summary>
        /// <param name="pawn">The pawn to update.</param>
        void UpdateLastBored(Pawn pawn);
    }

    /// <summary>
    /// Interface for providing strategy resolution services.
    /// This abstraction allows for better testability and dependency injection.
    /// </summary>
    public interface IWorkTypeStrategyProvider
    {
        /// <summary>
        /// Gets the appropriate strategy for the given work type.
        /// </summary>
        /// <param name="workTypeDef">The work type definition.</param>
        /// <returns>The strategy instance, or null if no strategy is found.</returns>
        IWorkTypeStrategy GetStrategy(WorkTypeDef workTypeDef);
    }

    /// <summary>
    /// Interface for dependency provider that supplies all necessary dependencies to Priority calculations.
    /// This is the main abstraction for dependency injection in the Priority class.
    /// </summary>
    public interface IPriorityDependencyProvider
    {
        /// <summary>
        /// Gets the world state provider.
        /// </summary>
        IWorldStateProvider WorldStateProvider { get; }

        /// <summary>
        /// Gets the map state provider for the specified pawn.
        /// </summary>
        /// <param name="pawn">The pawn whose map state is needed.</param>
        /// <returns>The map state provider, or null if unavailable.</returns>
        IMapStateProvider GetMapStateProvider(Pawn pawn);

        /// <summary>
        /// Gets the work type strategy provider.
        /// </summary>
        IWorkTypeStrategyProvider StrategyProvider { get; }
    }
}
