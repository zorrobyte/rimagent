// Written for RimBridge (2026): the steward.research queue (ordered ResearchProjectDef defNames persisted in StewardGame).
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>
    /// Ordered research queue. When the current project finishes (ResearchManager.FinishProject postfix in
    /// Ledger/Patches.cs) or nothing is being researched (StewardGame tick), the first queued project that can
    /// start now is set as the current project and a research_advanced ledger event is written.
    /// </summary>
    public static class StewardResearch
    {
        private static readonly List<string> Empty = new List<string>();

        public static List<string> Queue => StewardGame.Current?.researchQueue ?? Empty;

        public static ResearchProjectDef? Def(string name) => DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);

        /// <summary>Replace (or append to) the queue. Returns the project started right away, if any.</summary>
        public static ResearchProjectDef? Set(IEnumerable<string> defNames, bool append)
        {
            var g = StewardGame.Current ?? throw new InvalidOperationException("no game");
            g.researchQueue = ResearchQueueLogic.Merge(g.researchQueue, defNames, append);
            return Find.ResearchManager.GetProject() == null ? TryAdvance("queue set") : null;
        }

        public static void Clear()
        {
            var g = StewardGame.Current;
            if (g != null) g.researchQueue = new List<string>();
        }

        /// <summary>FinishProject postfix. Starts the next queued project once nothing is being researched.</summary>
        public static void Notify_ProjectFinished(ResearchProjectDef proj)
        {
            var g = StewardGame.Current;
            if (g == null || g.researchQueue.Count == 0) return;
            if (Find.ResearchManager.GetProject() != null) return; // a prerequisite finished inside a bigger FinishProject
            TryAdvance(proj?.label);
        }

        /// <summary>Periodic safety net: nothing researched, queue non-empty.</summary>
        public static void TickCheck()
        {
            var g = StewardGame.Current;
            if (g == null || g.researchQueue.Count == 0) return;
            if (Find.ResearchManager == null || Find.ResearchManager.GetProject() != null) return;
            TryAdvance(null);
        }

        public static ResearchProjectDef? TryAdvance(string? why)
        {
            var g = StewardGame.Current;
            if (g == null) return null;
            string? next = ResearchQueueLogic.PickNext(g.researchQueue,
                n => Def(n)?.IsFinished ?? true,      // unknown defs are dropped like finished ones
                n => Def(n)?.CanStartNow ?? false);
            if (next == null) return null;
            var def = Def(next)!;
            Find.ResearchManager.SetCurrentProject(def);
            StewardLog.Message($"research queue: started {def.defName} ({g.researchQueue.Count} left)");
            StewardLedger.ResearchAdvanced(def, g.researchQueue.Count, why);
            return def;
        }
    }
}
