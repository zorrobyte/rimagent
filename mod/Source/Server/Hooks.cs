using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Server
{
    /// <summary>Extension points an add-on mod (loaded after RimBridge, referencing RimBridge.dll) can subscribe
    /// to, so RimBridge's own ui.*/designate/ledger/state RPCs can notify or be extended by it without RimBridge
    /// knowing it exists. Nothing in core RimBridge subscribes to these — if no add-on is loaded, raising them is
    /// a no-op and SummaryContributors is empty.</summary>
    public static class Hooks
    {
        /// <summary>Raised whenever a ui.* RPC acts on a thing/pawn by hand (press, order, draft, goto, attack,
        /// job, designate, set_policies...). Args: the thing/pawn touched, a short reason string.</summary>
        public static event Action<Thing, string>? ManualTouch;
        public static void RaiseManualTouch(Thing thing, string reason) => ManualTouch?.Invoke(thing, reason);

        /// <summary>Raised when ui.set_work / ui.set_work_many explicitly sets a pawn's work priorities by hand.</summary>
        public static event Action<Pawn>? PawnWorkSetManually;
        public static void RaisePawnWorkSetManually(Pawn pawn) => PawnWorkSetManually?.Invoke(pawn);

        /// <summary>Raised after a research project finishes (ResearchManager.FinishProject postfix).</summary>
        public static event Action<ResearchProjectDef>? ResearchProjectFinished;
        public static void RaiseResearchProjectFinished(ResearchProjectDef proj) => ResearchProjectFinished?.Invoke(proj);

        /// <summary>An add-on registers a function here to contribute its own named block to state.summary's
        /// output (e.g. return ("steward", someJObject) to appear as result["steward"]). Exceptions are caught
        /// and reported per-contributor by the caller so one broken add-on can't break state.summary.</summary>
        public static readonly List<Func<Map, (string Key, JToken Value)>> SummaryContributors = new();
    }
}
