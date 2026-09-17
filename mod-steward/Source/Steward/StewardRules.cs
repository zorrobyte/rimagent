// Written for RimBridge (2026). Verse-free rules shared by StewardRpc, StewardLedger and the unit tests.
using System;
using System.Collections.Generic;

namespace RimBridge.Steward
{
    /// <summary>Ordering rules for the steward.research queue (pure; the game-facing part lives in StewardResearch).</summary>
    public static class ResearchQueueLogic
    {
        /// <summary>Replace or append; duplicates are dropped (first occurrence wins), order otherwise preserved.</summary>
        public static List<string> Merge(IEnumerable<string>? existing, IEnumerable<string>? incoming, bool append)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Take(IEnumerable<string>? src)
            {
                if (src == null) return;
                foreach (var s in src)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var t = s.Trim();
                    if (seen.Add(t)) result.Add(t);
                }
            }
            if (append) Take(existing);
            Take(incoming);
            return result;
        }

        /// <summary>
        /// Drops finished entries from the queue, then returns (and removes) the first entry that can start now.
        /// Entries that are not yet available stay queued in place so they start once their prerequisites finish.
        /// </summary>
        public static string? PickNext(List<string> queue, Func<string, bool> isFinished, Func<string, bool> canStart)
        {
            if (queue == null || queue.Count == 0) return null;
            queue.RemoveAll(q => isFinished(q));
            for (int i = 0; i < queue.Count; i++)
            {
                if (!canStart(queue[i])) continue;
                var next = queue[i];
                queue.RemoveAt(i);
                return next;
            }
            return null;
        }
    }

    /// <summary>When a stock job counts as stalled and how often that is reported (once per job per day).</summary>
    public static class StockStallRule
    {
        public const int TicksPerDay = 60000;

        /// <summary>Three failed runs in a row, or a whole day's worth of runs that found nothing to designate.</summary>
        public static bool IsStalled(int consecutiveFailures, int runsWithoutTargets, int intervalTicks)
        {
            if (consecutiveFailures >= 3) return true;
            if (runsWithoutTargets <= 0) return false;
            return (long)runsWithoutTargets * Math.Max(1, intervalTicks) >= TicksPerDay;
        }

        public static bool ShouldReport(int day, int? lastReportedDay) => lastReportedDay == null || day > lastReportedDay.Value;
    }

    /// <summary>
    /// Livestock arithmetic (pure): the colony's tame animals of one species are split into four buckets
    /// (adult/juvenile × male/female); targets are either a total [min, max] or a per-bucket [min, max]
    /// (max &lt; 0 = no upper bound). Compute() says how many to tame and to slaughter per bucket.
    /// </summary>
    public static class LivestockRule
    {
        public const int BucketCount = 4;
        public const int AdultMale = 0, AdultFemale = 1, JuvenileMale = 2, JuvenileFemale = 3;
        public const int NoLimit = -1;
        public static readonly string[] BucketNames = { "adult_male", "adult_female", "juvenile_male", "juvenile_female" };
        /// <summary>Total mode: cull adult males first, then young males, then adult females, then young females.</summary>
        public static readonly int[] SlaughterOrder = { AdultMale, JuvenileMale, AdultFemale, JuvenileFemale };
        /// <summary>Total mode: tame breeding stock first (adult females), then adult males, then the young.</summary>
        public static readonly int[] TameOrder = { AdultFemale, AdultMale, JuvenileFemale, JuvenileMale };

        public static int Bucket(bool adult, bool female) => (adult ? 0 : 2) + (female ? 1 : 0);

        /// <summary>"adult_male" / "AdultMale" / "adult male" → 0 … 3; -1 when unknown.</summary>
        public static int BucketIndex(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string k = name!.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
            if (k == "adultmale" || k == "males" || k == "male") return AdultMale;
            if (k == "adultfemale" || k == "females" || k == "female") return AdultFemale;
            if (k == "juvenilemale" || k == "youngmale") return JuvenileMale;
            if (k == "juvenilefemale" || k == "youngfemale") return JuvenileFemale;
            return -1;
        }

        public sealed class Targets
        {
            public int Min;
            public int Max;
            public int[]? BucketMin;
            public int[]? BucketMax;
            public bool PerBucket => BucketMin != null && BucketMax != null && BucketMin.Length == BucketCount && BucketMax.Length == BucketCount;
            public int MinOf(int b) => PerBucket ? Math.Max(0, BucketMin![b]) : Math.Max(0, Min);
            public int MaxOf(int b) => PerBucket ? BucketMax![b] : Max;
        }

        public sealed class Plan
        {
            public int[] Tame = new int[BucketCount];
            public int[] Slaughter = new int[BucketCount];
            /// <summary>Pending tame designations to release because the bucket/total is already at or over max.</summary>
            public int[] ReleaseTame = new int[BucketCount];
            public int TameTotal { get { int n = 0; foreach (var x in Tame) n += x; return n; } }
            public int SlaughterTotal { get { int n = 0; foreach (var x in Slaughter) n += x; return n; } }
            public int ReleaseTotal { get { int n = 0; foreach (var x in ReleaseTame) n += x; return n; } }
            public bool Empty => TameTotal == 0 && SlaughterTotal == 0 && ReleaseTotal == 0;
        }

        static int Sum(int[] a) { int n = 0; for (int i = 0; i < a.Length; i++) n += a[i]; return n; }

        /// <summary>Upper bound that is never below the lower bound; NoLimit stays unbounded.</summary>
        static int EffectiveMax(int min, int max) => max < 0 ? int.MaxValue : Math.Max(min, max);

        /// <summary>True when every constraint holds for the given tame counts (pending designations ignored).</summary>
        public static bool Meets(Targets t, int[] have)
        {
            if (t.PerBucket)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    int min = t.MinOf(b), max = EffectiveMax(min, t.MaxOf(b));
                    if (have[b] < min || have[b] > max) return false;
                }
                return true;
            }
            int total = Sum(have), tmin = t.MinOf(0), tmax = EffectiveMax(tmin, t.MaxOf(0));
            return total >= tmin && total <= tmax;
        }

        /// <summary>
        /// have: owned tame animals per bucket (those already marked for slaughter excluded);
        /// pendingTame: wild animals already designated Tame; wild: tameable wild animals not yet designated;
        /// cullable: owned animals that may be slaughtered (subset of have); maxNew: designation budget left.
        /// </summary>
        public static Plan Compute(Targets t, int[] have, int[] pendingTame, int[] wild, int[] cullable, int maxNew, bool tame = true, bool slaughter = true)
        {
            var plan = new Plan();
            int budget = Math.Max(0, maxNew);
            if (t.PerBucket)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    int min = t.MinOf(b), max = EffectiveMax(min, t.MaxOf(b));
                    int expected = have[b] + pendingTame[b];
                    if (tame && expected < min)
                    {
                        int n = Math.Min(Math.Min(min - expected, wild[b]), budget);
                        plan.Tame[b] = n; budget -= n;
                    }
                    else if (expected > max && pendingTame[b] > 0)
                        plan.ReleaseTame[b] = Math.Min(expected - max, pendingTame[b]);
                    if (slaughter && have[b] > max)
                    {
                        int n = Math.Min(Math.Min(have[b] - max, cullable[b]), budget);
                        plan.Slaughter[b] = n; budget -= n;
                    }
                }
                return plan;
            }

            int tmin = t.MinOf(0), tmax = EffectiveMax(tmin, t.MaxOf(0));
            int owned = Sum(have), pending = Sum(pendingTame), exp = owned + pending;
            if (tame && exp < tmin)
            {
                int need = Math.Min(tmin - exp, budget);
                foreach (int b in TameOrder)
                {
                    if (need <= 0) break;
                    int n = Math.Min(need, wild[b]);
                    plan.Tame[b] = n; need -= n; budget -= n;
                }
            }
            else if (exp > tmax && pending > 0)
            {
                int rel = Math.Min(exp - tmax, pending);
                for (int i = TameOrder.Length - 1; i >= 0 && rel > 0; i--)
                {
                    int b = TameOrder[i];
                    int n = Math.Min(rel, pendingTame[b]);
                    plan.ReleaseTame[b] = n; rel -= n;
                }
            }
            if (slaughter && owned > tmax)
            {
                int surplus = Math.Min(owned - tmax, budget);
                foreach (int b in SlaughterOrder)
                {
                    if (surplus <= 0) break;
                    int n = Math.Min(surplus, cullable[b]);
                    plan.Slaughter[b] = n; surplus -= n;
                }
            }
            return plan;
        }

        /// <summary>"adult ♂2 ♀4 · young ♂1 ♀0 = 7 (min 4, max 10)".</summary>
        public static string Summary(Targets t, int[] have)
        {
            string counts = $"adult ♂{have[AdultMale]} ♀{have[AdultFemale]} · young ♂{have[JuvenileMale]} ♀{have[JuvenileFemale]} = {Sum(have)}";
            if (!t.PerBucket) return $"{counts} (min {t.MinOf(0)}, max {(t.Max < 0 ? "∞" : t.Max.ToString())})";
            var parts = new List<string>();
            for (int b = 0; b < BucketCount; b++)
                parts.Add($"{BucketNames[b]} {t.MinOf(b)}..{(t.BucketMax![b] < 0 ? "∞" : t.BucketMax[b].ToString())}");
            return $"{counts} ({string.Join(", ", parts)})";
        }
    }
}
