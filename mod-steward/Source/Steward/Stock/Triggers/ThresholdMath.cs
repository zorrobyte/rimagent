// Derived from Colony Manager Redux Triggers/Trigger_Threshold.cs (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
// Pure logic: no Verse dependency (compiled into the test project).
using System.Collections.Generic;

namespace RimBridge.Steward.Stock
{
    public enum ThresholdOp { LowerThan, Equals, HigherThan, NotEquals }

    public enum Directive { Increase, Decrease, Hold }

    public static class ThresholdMath
    {
        public static readonly IReadOnlyList<ThresholdOp> AllOps = new[]
            { ThresholdOp.LowerThan, ThresholdOp.Equals, ThresholdOp.HigherThan, ThresholdOp.NotEquals };

        /// <summary>Jobs that can only add stock (forestry, hunting, mining...) never support HigherThan.</summary>
        public static readonly IReadOnlyList<ThresholdOp> AccumulationOnlyOps = new[]
            { ThresholdOp.LowerThan, ThresholdOp.Equals, ThresholdOp.NotEquals };

        /// <summary>True when the trigger is satisfied ("count meets target").</summary>
        public static bool Evaluate(ThresholdOp op, int count, int targetCount) => op switch
        {
            ThresholdOp.LowerThan => count >= targetCount,
            ThresholdOp.Equals => count == targetCount,
            ThresholdOp.HigherThan => count <= targetCount,
            ThresholdOp.NotEquals => count != targetCount,
            _ => true,
        };

        public static Directive EvaluateDirective(ThresholdOp op, int count, int targetCount) => op switch
        {
            ThresholdOp.LowerThan => count < targetCount ? Directive.Increase : Directive.Hold,
            ThresholdOp.Equals => count < targetCount ? Directive.Increase : count > targetCount ? Directive.Decrease : Directive.Hold,
            ThresholdOp.HigherThan => count > targetCount ? Directive.Decrease : Directive.Hold,
            ThresholdOp.NotEquals => count == targetCount ? Directive.Increase : Directive.Hold,
            _ => Directive.Hold,
        };

        public static ThresholdOp MigrateUnsupportedOp(ThresholdOp op, IReadOnlyList<ThresholdOp> supported, ThresholdOp fallback)
        {
            foreach (var s in supported) if (s == op) return op;
            return fallback;
        }

        public static int ClampTarget(int value, int max) => value < 0 ? 0 : value > max ? max : value;

        /// <summary>Posture-scaled target: baseline × multiplier truncated to an int, never negative (x0 = target 0 = met at once).</summary>
        public static int ScaleTarget(int baseline, float multiplier)
            => baseline <= 0 || multiplier <= 0f ? 0 : (int)(baseline * multiplier);
    }
}
