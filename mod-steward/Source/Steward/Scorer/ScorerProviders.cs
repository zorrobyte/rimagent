// Derived from Free Will DependencyProviders.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;

using RimBridge.Steward;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Default implementation of IWorldStateProvider that uses RimWorld's world component system.
    /// </summary>
    public class WorldStateProvider : IWorldStateProvider
    {
        private readonly ScorerSettings settings;

        public WorldStateProvider(ScorerSettings settings)
        {
            this.settings = settings ?? throw new System.ArgumentNullException(nameof(settings));
        }

        public ScorerSettings Settings => StewardTuning.EffectiveSettings(settings);
    }    /// <summary>
         /// Default implementation of IMapStateProvider that uses RimWorld's map component system.
         /// </summary>
    public class MapStateProvider : IMapStateProvider
    {
        private readonly ScorerComponent mapComponent;

        public MapStateProvider(ScorerComponent mapComponent)
        {
            this.mapComponent = mapComponent ?? throw new System.ArgumentNullException(nameof(mapComponent));
        }

        public List<WorkTypeDef> DisabledWorkTypes => mapComponent.DisabledWorkTypes;
        public List<Thought> AllThoughts => mapComponent.AllThoughts;
        public bool AlertNeedWarmClothes => mapComponent.AlertNeedWarmClothes;
        public bool AlertAnimalRoaming => mapComponent.AlertAnimalRoaming;
        public float SuppressionNeed => mapComponent.SuppressionNeed;
        public bool AlertColonistLeftUnburied => mapComponent.AlertColonistLeftUnburied;
        public float PercentPawnsDowned => mapComponent.PercentPawnsDowned;
        public bool RefuelNeededNow => mapComponent.RefuelNeededNow;
        public bool RefuelNeeded => mapComponent.RefuelNeeded;
        public bool HomeFire => mapComponent.HomeFire;
        public int MapFires => mapComponent.MapFires;
        public float PercentPawnsNeedingTreatment => mapComponent.PercentPawnsNeedingTreatment;
        public float NumPetsNeedingTreatment => mapComponent.NumPetsNeedingTreatment;
        public float NumPrisonersNeedingTreatment => mapComponent.NumPrisonersNeedingTreatment;
        public int NumPawns => mapComponent.NumPawns;
        public float PercentPawnsMechHaulers => mapComponent.PercentPawnsMechHaulers;
        public bool AlertAnimalPenNeeded => mapComponent.AlertAnimalPenNeeded;
        public bool AlertAnimalPenNotEnclosed => mapComponent.AlertAnimalPenNotEnclosed;
        public bool AlertMechDamaged => mapComponent.AlertMechDamaged;
        public bool AlertLowFood => mapComponent.AlertLowFood;
        public Thing ThingsDeteriorating => mapComponent.ThingsDeteriorating;
        public bool PlantsBlighted => mapComponent.PlantsBlighted;
        public bool AreaHasHaulables => mapComponent.AreaHasHaulables;
        public bool AreaHasFilth => mapComponent.AreaHasFilth;

        public int? GetLastBored(Pawn pawn)
        {
            return mapComponent.GetLastBored(pawn);
        }

        public void UpdateLastBored(Pawn pawn)
        {
            mapComponent.UpdateLastBored(pawn);
        }
    }

    /// <summary>
    /// Default implementation of IWorkTypeStrategyProvider that uses the existing registry.
    /// </summary>
    public class WorkTypeStrategyProvider : IWorkTypeStrategyProvider
    {
        public IWorkTypeStrategy GetStrategy(WorkTypeDef workTypeDef)
        {
            return WorkTypeStrategyRegistry.GetStrategy(workTypeDef);
        }
    }

    /// <summary>
    /// Default implementation of IPriorityDependencyProvider that uses RimWorld's component system.
    /// This is the production implementation that maintains compatibility with existing code.
    /// </summary>
    public class DefaultPriorityDependencyProvider : IPriorityDependencyProvider
    {
        public DefaultPriorityDependencyProvider()
        {
            StrategyProvider = new WorkTypeStrategyProvider();
        }

        public IWorldStateProvider WorldStateProvider
        {
            get
            {
                var settings = StewardMod.Settings?.scorer;
                return settings != null ? new WorldStateProvider(settings) : null;
            }
        }

        public IMapStateProvider GetMapStateProvider(Pawn pawn)
        {
            if (pawn?.Map == null)
            {
                return null;
            }

            ScorerComponent mapComponent = pawn.Map.GetComponent<ScorerComponent>();
            return mapComponent != null ? new MapStateProvider(mapComponent) : null;
        }

        public IWorkTypeStrategyProvider StrategyProvider { get; }
    }

    /// <summary>
    /// Static dependency provider that can be replaced for testing or alternative implementations.
    /// This follows the Service Locator pattern for easy integration with existing code.
    /// </summary>
    public static class PriorityDependencyProviderFactory
    {
        /// <summary>
        /// Gets the current dependency provider instance.
        /// </summary>
        public static IPriorityDependencyProvider Current { get; private set; } = new DefaultPriorityDependencyProvider();

        /// <summary>
        /// Sets the dependency provider instance. Used primarily for testing.
        /// </summary>
        /// <param name="provider">The provider to use.</param>
        public static void SetProvider(IPriorityDependencyProvider provider)
        {
            Current = provider ?? throw new System.ArgumentNullException(nameof(provider));
        }

        /// <summary>
        /// Resets to the default dependency provider. Used primarily for testing cleanup.
        /// </summary>
        public static void Reset()
        {
            Current = new DefaultPriorityDependencyProvider();
        }
    }
}
