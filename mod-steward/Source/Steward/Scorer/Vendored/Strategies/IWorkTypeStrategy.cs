// Derived from Free Will Strategies/IWorkTypeStrategy.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
#nullable disable
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Interface for work type specific priority calculation strategies.
    /// Each work type has its own unique combination of considerations.
    /// </summary>
    public interface IWorkTypeStrategy
    {
        /// <summary>
        /// The work type this strategy handles.
        /// </summary>
        WorkTypeDef WorkType { get; }

        /// <summary>
        /// Calculates the priority for the given work type using the provided priority calculator.
        /// </summary>
        /// <param name="priority">The priority calculator instance to use.</param>
        /// <returns>The priority instance after applying all considerations.</returns>
        Priority CalculatePriority(Priority priority);
    }
}
