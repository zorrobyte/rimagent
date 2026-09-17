// Derived from Colony Manager Redux Triggers/Trigger_Threshold.cs + Trigger.cs (MIT, see THIRD_PARTY_NOTICES.md), UI removed.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// "Keep the count of things matching ThresholdFilter [op] TargetCount". Owns the filter, the
    /// operator, the optional stockpile restriction, and a TTL-cached current count.
    /// </summary>
    public sealed class Trigger_Threshold : IExposable
    {
        public const int CountCacheTicks = 250;

        private StockJob _job;
        private IReadOnlyList<ThresholdOp> _supportedOps;
        private ThresholdOp _op;
        private int _targetCount;
        private int _maxUpperThreshold;
        private bool _countAllOnMap;
        private Zone_Stockpile? _stockpile;
        private string? _stockpileScribe;
        private ThingFilter _thresholdFilter = null!;
        private readonly Cached<int> _cachedCount = new Cached<int>(0, CountCacheTicks);

        /// <summary>What the LLM / UI may pick from (e.g. all tree products for forestry).</summary>
        public ThingFilter ParentFilter { get; private set; } = null!;
        public ThingFilter ThresholdFilter => _thresholdFilter;
        public Action? SettingsChanged { get; set; }

        /// <summary>Scribe_Deep reconstructs the trigger through Activator.CreateInstance(type, new object[] { job }),
        /// which needs a constructor with exactly one parameter: optional parameters are not filled in by reflection.
        /// Without this overload every stock job's target silently loaded as 0 (MissingMethodException in SaveableFromNode).</summary>
        public Trigger_Threshold(StockJob job) : this(job, null, 3000) { }

        public Trigger_Threshold(StockJob job, IReadOnlyList<ThresholdOp>? supportedOps = null, int maxUpperThreshold = 3000)
        {
            _job = job;
            _supportedOps = supportedOps ?? ThresholdMath.AllOps;
            _op = _supportedOps[0];
            _maxUpperThreshold = maxUpperThreshold;
            _countAllOnMap = false;
            _targetCount = 0;
            ParentFilter = CreateFilter(null);
            ParentFilter.SetAllowAll(null, true);
            _thresholdFilter = CreateFilter(OnFilterChanged);
        }

        /// <summary>A freshly constructed ThingFilter rejects everything until hit points/quality ranges are set.</summary>
        public static ThingFilter CreateFilter(Action? changed)
        {
            var f = changed != null ? new ThingFilter(changed) : new ThingFilter();
            f.SetDisallowAll();
            f.AllowedHitPointsPercents = FloatRange.ZeroToOne;
            f.AllowedQualityLevels = QualityRange.All;
            return f;
        }

        public ThresholdOp Op
        {
            get => _op;
            set { if (SupportsOp(value) && _op != value) { _op = value; OnFilterChanged(); } }
        }

        public bool SupportsOp(ThresholdOp op) => _supportedOps.Contains(op);

        public int TargetCount
        {
            get => _targetCount;
            set { int v = ThresholdMath.ClampTarget(value, _maxUpperThreshold); if (v != _targetCount) { _targetCount = v; SettingsChanged?.Invoke(); } }
        }

        public int MaxUpperThreshold { get => _maxUpperThreshold; set => _maxUpperThreshold = Math.Max(1, value); }

        public bool CountAllOnMap
        {
            get => _countAllOnMap;
            set { if (_countAllOnMap != value) { _countAllOnMap = value; OnFilterChanged(); } }
        }

        public Zone_Stockpile? Stockpile
        {
            get => _stockpile;
            set { if (_stockpile != value) { _stockpile = value; OnFilterChanged(); } }
        }

        public void SetParentFilter(Action<ThingFilter> configure)
        {
            ParentFilter = CreateFilter(null);
            configure(ParentFilter);
        }

        public bool IsValid => _thresholdFilter.AllowedDefCount > 0;

        private void OnFilterChanged() { _cachedCount.Invalidate(); SettingsChanged?.Invoke(); }

        public int GetCurrentCount(bool cached = true)
        {
            if (cached && _cachedCount.TryGetValue(out int v)) return v;
            return _cachedCount.Update(ProductCounter.CountProducts(_job.Map, _thresholdFilter, _stockpile, _countAllOnMap));
        }

        /// <summary>True while the trigger wants work (count does not meet the target).</summary>
        public bool State => !DoesCountMeetTarget(GetCurrentCount());

        public bool DoesCountMeetTarget(int count) => ThresholdMath.Evaluate(_op, count, _targetCount);
        public Directive GetDirective(int count) => ThresholdMath.EvaluateDirective(_op, count, _targetCount);

        public string OpString => _op switch
        {
            ThresholdOp.LowerThan => "<",
            ThresholdOp.Equals => "=",
            ThresholdOp.HigherThan => ">",
            ThresholdOp.NotEquals => "≠",
            _ => "?",
        };

        public string StatusLine => $"{GetCurrentCount()} / {OpString} {_targetCount}";

        public void ExposeData()
        {
            Scribe_Values.Look(ref _targetCount, "count");
            Scribe_Values.Look(ref _maxUpperThreshold, "maxUpperThreshold", 3000);
            Scribe_Values.Look(ref _op, "operator");
            Scribe_Deep.Look(ref _thresholdFilter, "thresholdFilter", new object[] { (Action)OnFilterChanged });
            Scribe_Values.Look(ref _countAllOnMap, "countAllOnMap");
            if (Scribe.mode == LoadSaveMode.Saving) _stockpileScribe = _stockpile?.label ?? "null";
            Scribe_Values.Look(ref _stockpileScribe, "stockpile", "null");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _thresholdFilter ??= CreateFilter(OnFilterChanged);
                _op = ThresholdMath.MigrateUnsupportedOp(_op, _supportedOps, _supportedOps[0]);
                if (_stockpileScribe != null && _stockpileScribe != "null" && _job?.Map != null)
                    _stockpile = _job.Map.zoneManager.AllZones.FirstOrDefault(z => z is Zone_Stockpile && z.label == _stockpileScribe) as Zone_Stockpile;
            }
        }

        /// <summary>Called by the owning job after load so the trigger can resolve its map.</summary>
        internal void Attach(StockJob job) => _job = job;
    }
}
