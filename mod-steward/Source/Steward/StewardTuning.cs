// Written for Autopilot (2026, MIT) as AutopilotTuning.cs; modified for RimBridge (2026): renamed StewardTuning, per-pawn
// planner deltas dropped (the LLM sets colony-wide posture only), presets added, state persisted in StewardGame.
using System;
using System.Collections.Generic;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>
    /// The single seam through which the LLM biases the deterministic layers.
    /// Effective work adjustment = player baseline (ScorerSettings.globalWorkAdjustments) + posture delta, clamped to ±1.
    /// Effective scorer weights = baseline × posture multiplier. Effective stock target = baseline × posture multiplier.
    /// A posture is time-boxed (expires after N hours); "normal" / clear removes it.
    /// </summary>
    public static class StewardTuning
    {
        /// <summary>WorkTypeDef defName → delta in -1..1 (case-insensitive keys).</summary>
        public static readonly Dictionary<string, float> PostureWorkDeltas = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        /// <summary>ScorerSettings weight field name → multiplier (see ScorerSettings.WeightNames).</summary>
        public static readonly Dictionary<string, float> PostureWeightMultipliers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Stock job kind (Forestry|Foraging|Hunting|HuntingLeather|Mining) → target multiplier.</summary>
        public static readonly Dictionary<string, float> PostureTargetMultipliers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public static string PostureLabel = "";
        public static int PostureExpiresTick = -1;

        /// <summary>Raised (with the label) when a posture lapses by itself; the ledger stage hooks this.</summary>
        public static event Action<string>? PostureExpired;

        public const float DefaultPostureHours = 12f;
        public const int TicksPerHour = 2500;

        // ── Presets ──────────────────────────────────────────────────────────

        public sealed class PosturePreset
        {
            public string Label = "";
            public Dictionary<string, float> Work = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, float> Weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, float> Targets = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }

        private static PosturePreset P(string label, (string, float)[] work, (string, float)[] weights, (string, float)[] targets)
        {
            var p = new PosturePreset { Label = label };
            foreach (var (k, v) in work) p.Work[k] = v;
            foreach (var (k, v) in weights) p.Weights[k] = v;
            foreach (var (k, v) in targets) p.Targets[k] = v;
            return p;
        }

        /// <summary>Named presets: defend | build | harvest | recover | normal (normal = clear).</summary>
        public static readonly IReadOnlyDictionary<string, PosturePreset> Presets = new Dictionary<string, PosturePreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["normal"] = P("normal", new (string, float)[0], new (string, float)[0], new (string, float)[0]),
            ["defend"] = P("defend",
                new[] { ("Doctor", 0.3f), ("Construction", 0.2f), ("Smithing", 0.2f), ("Hauling", 0.1f), ("Firefighter", 0.2f),
                        ("Hunting", -0.6f), ("Mining", -0.4f), ("PlantCutting", -0.4f), ("Growing", -0.2f), ("Research", -0.3f), ("Art", -0.5f) },
                new[] { ("ConsiderWeaponRange", 1.5f) },
                new[] { ("Hunting", 0f), ("HuntingLeather", 0f), ("Forestry", 0.5f), ("Foraging", 0.5f), ("Mining", 0.5f) }),
            ["build"] = P("build",
                new[] { ("Construction", 0.4f), ("Hauling", 0.2f), ("Mining", 0.2f), ("PlantCutting", 0.2f), ("Smithing", 0.1f),
                        ("Art", -0.3f), ("Research", -0.2f), ("Hunting", -0.1f) },
                new (string, float)[0],
                new[] { ("Forestry", 1.5f), ("Mining", 1.5f) }),
            ["harvest"] = P("harvest",
                new[] { ("Growing", 0.4f), ("PlantCutting", 0.2f), ("Hauling", 0.2f), ("Cooking", 0.2f), ("Hunting", 0.1f),
                        ("Construction", -0.2f), ("Research", -0.3f), ("Art", -0.3f), ("Mining", -0.2f) },
                new[] { ("ConsiderLowFood", 1.5f) },
                new[] { ("Foraging", 1.5f), ("Hunting", 1.25f) }),
            ["recover"] = P("recover",
                new[] { ("Doctor", 0.4f), ("Cooking", 0.2f), ("Cleaning", 0.2f), ("Hauling", 0.1f),
                        ("Construction", -0.2f), ("Mining", -0.3f), ("Hunting", -0.3f), ("Research", -0.2f), ("PlantCutting", -0.2f) },
                new[] { ("ConsiderLowFood", 1.25f), ("ConsiderFoodPoisoning", 1.5f) },
                new[] { ("Forestry", 0.75f), ("Foraging", 0.75f), ("Hunting", 0.75f), ("HuntingLeather", 0.75f), ("Mining", 0.75f) }),
        };

        // ── Posture API ──────────────────────────────────────────────────────

        /// <summary>Installs a preset by name for `hours` (normal clears). Returns false for an unknown name.</summary>
        public static bool ApplyPreset(string name, float hours = DefaultPostureHours)
        {
            if (name == null || !Presets.TryGetValue(name.Trim(), out var p)) return false;
            if (p.Work.Count == 0 && p.Weights.Count == 0 && p.Targets.Count == 0) { ClearPosture(); return true; }
            SetPosture(p.Label, hours, p.Work, p.Weights, p.Targets);
            return true;
        }

        /// <summary>
        /// Installs an explicit posture. Unknown/blank label falls back to "custom". If the label names a preset, its
        /// tables seed the posture and the explicit dictionaries override individual entries.
        /// </summary>
        public static void SetPosture(string label, float hours, IDictionary<string, float>? work, IDictionary<string, float>? weights, IDictionary<string, float>? targets)
        {
            PostureWorkDeltas.Clear();
            PostureWeightMultipliers.Clear();
            PostureTargetMultipliers.Clear();
            string lbl = string.IsNullOrWhiteSpace(label) ? "custom" : label.Trim();
            if (Presets.TryGetValue(lbl, out var preset))
            {
                foreach (var kv in preset.Work) PostureWorkDeltas[kv.Key] = kv.Value;
                foreach (var kv in preset.Weights) PostureWeightMultipliers[kv.Key] = kv.Value;
                foreach (var kv in preset.Targets) PostureTargetMultipliers[kv.Key] = kv.Value;
            }
            if (work != null) foreach (var kv in work) PostureWorkDeltas[kv.Key] = Clamp(kv.Value, -1f, 1f);
            if (weights != null) foreach (var kv in weights) PostureWeightMultipliers[kv.Key] = Math.Max(0f, kv.Value);
            if (targets != null) foreach (var kv in targets) PostureTargetMultipliers[kv.Key] = Math.Max(0f, kv.Value);
            PostureLabel = lbl;
            float h = hours <= 0f ? DefaultPostureHours : hours;
            int now = Find.TickManager?.TicksGame ?? 0;
            PostureExpiresTick = now + (int)(h * TicksPerHour);
        }

        public static bool PostureActive()
        {
            if (PostureExpiresTick < 0) return false;
            if (Find.TickManager == null) return false;
            if (Find.TickManager.TicksGame > PostureExpiresTick)
            {
                string label = PostureLabel;
                ClearPosture();
                try { PostureExpired?.Invoke(label); } catch (Exception ex) { StewardLog.Error($"posture expiry hook threw: {ex.Message}"); }
                return false;
            }
            return true;
        }

        public static float PostureExpiresInHours()
        {
            if (!PostureActive()) return 0f;
            return Math.Max(0f, (PostureExpiresTick - Find.TickManager.TicksGame) / (float)TicksPerHour);
        }

        public static void ClearPosture()
        {
            PostureWorkDeltas.Clear();
            PostureWeightMultipliers.Clear();
            PostureTargetMultipliers.Clear();
            PostureLabel = "";
            PostureExpiresTick = -1;
        }

        // ── Effective values ─────────────────────────────────────────────────

        public static float EffectiveWorkAdjust(Pawn pawn, WorkTypeDef wt, ScorerSettings settings)
        {
            float baseline = 0f;
            if (settings?.globalWorkAdjustments != null && settings.globalWorkAdjustments.TryGetValue(wt.defName, out float b))
                baseline = b;
            float posture = PostureActive() && PostureWorkDeltas.TryGetValue(wt.defName, out float d) ? d : 0f;
            return Clamp(baseline + posture, -1f, 1f);
        }

        public static string ExplainWorkAdjust(Pawn pawn, WorkTypeDef wt)
        {
            if (PostureActive() && PostureWorkDeltas.TryGetValue(wt.defName, out float d) && d != 0f)
                return $"Steward: posture {PostureLabel} {d:+0.00;-0.00}";
            return "";
        }

        public static float EffectiveWeight(string settingName, float baseline)
            => PostureActive() && PostureWeightMultipliers.TryGetValue(settingName, out float m) ? baseline * m : baseline;

        public static int EffectiveTarget(string jobKind, int baseline)
            => PostureActive() && PostureTargetMultipliers.TryGetValue(jobKind, out float m) ? ThresholdMath.ScaleTarget(baseline, m) : baseline;

        /// <summary>
        /// Snapshot of the player's scorer settings with posture multipliers applied to the weights.
        /// The globalWorkAdjustments dictionary is shared by reference (never cloned).
        /// </summary>
        public static ScorerSettings EffectiveSettings(ScorerSettings baseline)
        {
            if (baseline == null || !PostureActive() || PostureWeightMultipliers.Count == 0) return baseline!;
            var e = baseline.Clone();
            foreach (var name in ScorerSettings.WeightNames)
                e.SetWeight(name, EffectiveWeight(name, baseline.GetWeight(name)));
            return e;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>Decides whether the scorer may touch a pawn (the "managed" flag lives in StewardGame).</summary>
    public static class ScorerGate
    {
        /// <summary>True when the scorer will write this pawn's priorities: a live free colonist with work settings that nobody unmanaged.</summary>
        public static bool IsManaged(Pawn? pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed) return false;
            if (!pawn.IsFreeColonist || pawn.IsSlaveOfColony) return false;
            if (pawn.workSettings == null) return false;
            return !(StewardGame.Current?.IsUnmanaged(pawn) ?? false);
        }

        /// <summary>Only the flag (ignores whether the pawn is currently eligible).</summary>
        public static bool ManagedFlag(Pawn pawn) => !(StewardGame.Current?.IsUnmanaged(pawn) ?? false);

        public static void SetManaged(Pawn pawn, bool managed) => StewardGame.Current?.SetManaged(pawn, managed);
    }

    /// <summary>
    /// Per-game steward state, auto-instantiated by RimWorld: unmanaged pawn ids, runtime enable overrides, and the
    /// current posture (mirrored into StewardTuning's statics on load).
    /// </summary>
    public partial class StewardGame : GameComponent
    {
        public static StewardGame? Current { get; private set; }

        private HashSet<string> _unmanaged = new HashSet<string>();
        private List<string>? _unmanagedScribe;
        public bool? scorerOverride;
        public bool? stockOverride;
        /// <summary>Ordered ResearchProjectDef defNames (steward.research); advanced by StewardResearch.</summary>
        public List<string> researchQueue = new List<string>();
        private int _lastResearchCheckTick = -1;
        // Posture scribe fields: filled from StewardTuning when saving, loaded during LoadingVars, applied in PostLoadInit.
        // (Scribe only assigns in Saving/LoadingVars, so locals re-read from the statics in PostLoadInit would be empty.)
        private string _postureLabel = "";
        private int _postureExpires = -1;
        private Dictionary<string, float>? _postureWork;
        private Dictionary<string, float>? _postureWeights;
        private Dictionary<string, float>? _postureTargets;

        public StewardGame(Game game)
        {
            Current = this;
            StewardTuning.ClearPosture();
            StewardLedger.Reset();
            StewardLedger.EnsureHooked();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick - _lastResearchCheckTick < 2500) return;
            _lastResearchCheckTick = tick;
            try { StewardResearch.TickCheck(); }
            catch (Exception ex) { StewardLog.ErrorOnce($"research queue tick failed: {ex}", 0x57ea2000); }
        }

        public bool IsUnmanaged(Pawn pawn) => pawn?.ThingID != null && _unmanaged.Contains(pawn.ThingID);

        public void SetManaged(Pawn pawn, bool managed)
        {
            if (pawn?.ThingID == null) return;
            if (managed) _unmanaged.Remove(pawn.ThingID); else _unmanaged.Add(pawn.ThingID);
        }

        public IEnumerable<string> UnmanagedIds => _unmanaged;

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) _unmanagedScribe = new List<string>(_unmanaged);
            Scribe_Collections.Look(ref _unmanagedScribe, "unmanagedPawns", LookMode.Value);
            Scribe_Values.Look(ref scorerOverride, "scorerEnabled");
            Scribe_Values.Look(ref stockOverride, "stockEnabled");
            Scribe_Collections.Look(ref researchQueue, "researchQueue", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                _postureLabel = StewardTuning.PostureLabel;
                _postureExpires = StewardTuning.PostureExpiresTick;
                _postureWork = new Dictionary<string, float>(StewardTuning.PostureWorkDeltas);
                _postureWeights = new Dictionary<string, float>(StewardTuning.PostureWeightMultipliers);
                _postureTargets = new Dictionary<string, float>(StewardTuning.PostureTargetMultipliers);
            }
            Scribe_Values.Look(ref _postureLabel, "postureLabel", "");
            Scribe_Values.Look(ref _postureExpires, "postureExpiresTick", -1);
            Scribe_Collections.Look(ref _postureWork, "postureWork", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _postureWeights, "postureWeights", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _postureTargets, "postureTargets", LookMode.Value, LookMode.Value);
            ExposeOrdersData();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _unmanaged = new HashSet<string>(_unmanagedScribe ?? new List<string>());
                _unmanagedScribe = null;
                researchQueue ??= new List<string>();
                StewardTuning.ClearPosture();
                if (_postureExpires >= 0)
                {
                    StewardTuning.PostureLabel = _postureLabel ?? "";
                    StewardTuning.PostureExpiresTick = _postureExpires;
                    if (_postureWork != null) foreach (var kv in _postureWork) StewardTuning.PostureWorkDeltas[kv.Key] = kv.Value;
                    if (_postureWeights != null) foreach (var kv in _postureWeights) StewardTuning.PostureWeightMultipliers[kv.Key] = kv.Value;
                    if (_postureTargets != null) foreach (var kv in _postureTargets) StewardTuning.PostureTargetMultipliers[kv.Key] = kv.Value;
                }
                _postureWork = null; _postureWeights = null; _postureTargets = null;
            }
        }
    }
}
