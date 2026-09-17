// Derived from Free Will Strategies/WorkTypeStrategyRegistry.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using System.Collections.Generic;
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Registry for managing work type priority calculation strategies.
    /// Provides centralized access to all strategy implementations.
    /// </summary>
    public static class WorkTypeStrategyRegistry
    {
        private static readonly Dictionary<string, IWorkTypeStrategy> strategies = new Dictionary<string, IWorkTypeStrategy>();
        private static IWorkTypeStrategy defaultStrategy;
        private static bool initialized = false;

        /// <summary>
        /// Initializes the registry with all available strategies.
        /// This is called automatically when first accessed.
        /// </summary>
        private static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            // Don't initialize until the game and defs are fully loaded
            if (Current.Game == null || DefDatabase<WorkTypeDef>.DefCount == 0)
            {
                return;
            }
            try
            {
                // Register all strategy implementations
                RegisterStrategy(new FirefighterStrategy());
                RegisterStrategy(new PatientStrategy());
                RegisterStrategy(new DoctorStrategy());
                RegisterStrategy(new PatientBedRestStrategy());
                RegisterStrategy(new ChildcareStrategy());
                RegisterStrategy(new BasicWorkerStrategy());
                RegisterStrategy(new WardenStrategy());
                RegisterStrategy(new HandlingStrategy());
                RegisterStrategy(new CookingStrategy());
                RegisterStrategy(new HuntingStrategy());
                RegisterStrategy(new ConstructionStrategy());
                RegisterStrategy(new GrowingStrategy());
                RegisterStrategy(new MiningStrategy());
                RegisterStrategy(new PlantCuttingStrategy());
                RegisterStrategy(new SmithingStrategy());
                RegisterStrategy(new TailoringStrategy());
                RegisterStrategy(new ArtStrategy());
                RegisterStrategy(new CraftingStrategy());
                RegisterStrategy(new HaulingStrategy());
                RegisterStrategy(new CleaningStrategy());
                RegisterStrategy(new ResearchingStrategy());

                // Only register modded work type strategies if the work type exists
                if (DefDatabase<WorkTypeDef>.GetNamedSilentFail("HaulingUrgent") != null)
                {
                    RegisterStrategy(new HaulingUrgentStrategy());
                }

                // Handle default strategy separately since it has null WorkType
                defaultStrategy = new DefaultWorkTypeStrategy();

                initialized = true;
            }
            catch (System.Exception ex)
            {
                if (Prefs.DevMode)
                {
                    StewardLog.Error($"scorer: Failed to initialize WorkTypeStrategyRegistry: {ex}");
                }
            }
        }

        /// <summary>
        /// Registers a strategy for a specific work type.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        private static void RegisterStrategy(IWorkTypeStrategy strategy)
        {
            if (strategy?.WorkType?.defName == null)
            {
                if (Prefs.DevMode)
                {
                    StewardLog.Warning($"scorer: Failed to register strategy {strategy?.GetType()?.Name ?? "unknown"} - WorkType is null");
                }
                return;
            }
            strategies[strategy.WorkType.defName] = strategy;
        }

        /// <summary>
        /// Gets the strategy for the specified work type.
        /// If no specific strategy exists, returns the default strategy.
        /// </summary>
        /// <param name="workTypeDef">The work type to get a strategy for.</param>
        /// <returns>The appropriate strategy instance.</returns>
        public static IWorkTypeStrategy GetStrategy(WorkTypeDef workTypeDef)
        {
            Initialize();

            if (strategies.TryGetValue(workTypeDef.defName, out IWorkTypeStrategy strategy))
            {
                return strategy;
            }

            // Fallback to default strategy
            return defaultStrategy;
        }

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        /// <returns>Collection of all registered strategies.</returns>
        public static IEnumerable<IWorkTypeStrategy> GetAllStrategies()
        {
            Initialize();
            List<IWorkTypeStrategy> allStrategies = new List<IWorkTypeStrategy>(strategies.Values);
            if (defaultStrategy != null)
            {
                allStrategies.Add(defaultStrategy);
            }
            return allStrategies;
        }
    }
}
