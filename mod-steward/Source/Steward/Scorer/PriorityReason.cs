using System;

namespace RimBridge.Steward.Scorer
{
    public enum PriorityReasonKind { Set, Add, Multiply, Enable, Disable }

    /// <summary>
    /// One step of a Priority computation: what was considered and its signed effect on the 0..1 score.
    /// Structured twin of Priority.AdjustmentStrings, added for steward.explain.
    /// </summary>
    public readonly struct PriorityReason
    {
        private readonly Func<string> _describe;
        /// <summary>Signed change of Priority.Value caused by this step (0 for enable/disable).</summary>
        public readonly float Delta;
        public readonly PriorityReasonKind Kind;

        public PriorityReason(Func<string> describe, float delta, PriorityReasonKind kind)
        {
            _describe = describe;
            Delta = delta;
            Kind = kind;
        }

        /// <summary>Human label, e.g. "major passion for shooting" (evaluated lazily; never throws).</summary>
        public string Label
        {
            get { try { return _describe?.Invoke() ?? ""; } catch (Exception ex) { return "error: " + ex.Message; } }
        }

        public override string ToString() => Kind switch
        {
            PriorityReasonKind.Enable => $"{Label}: enabled",
            PriorityReasonKind.Disable => $"{Label}: disabled",
            PriorityReasonKind.Set => $"{Label}: ={Delta:+0.00;-0.00}",
            _ => $"{Label}: {Delta:+0.00;-0.00}",
        };
    }
}
