// Written for RimBridge (2026): the steward.* RPC surface (policy knobs over the scorer and stock layers).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimBridge.Steward.Scorer;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>
    /// steward.status / enable / pawn / explain / posture / stock.* / settings / research. Every method runs on the
    /// main thread (Rpc default). Stock kinds are reported snake_case (forestry, forestry_clear, foraging, hunting,
    /// hunting_leather, mining, production, livestock) and accepted in any casing.
    /// </summary>
    public static class StewardRpc
    {
        const float TicksPerHour = 2500f;

        static Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }
        static StockComponent Stock(Map map) => StockComponent.For(map) ?? throw new RpcError("no stock component on the current map");

        // ───────────────────────────── kinds ─────────────────────────────

        static readonly (string canon, string key)[] Kinds =
        {
            ("Forestry", "forestry"), ("ForestryClear", "forestryclear"), ("Foraging", "foraging"),
            ("Hunting", "hunting"), ("HuntingLeather", "huntingleather"), ("Mining", "mining"), ("Production", "production"),
            ("Livestock", "livestock"),
        };
        static string KindList => string.Join("|", Kinds.Select(k => KindOut(k.canon)));

        /// <summary>"hunting_leather" / "HuntingLeather" / "leather" → "HuntingLeather"; null for unknown.</summary>
        public static string? CanonKind(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string k = s!.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
            switch (k)
            {
                case "wood": case "logging": case "trees": k = "forestry"; break;
                case "clear": case "cleararea": k = "forestryclear"; break;
                case "meat": k = "hunting"; break;
                case "leather": k = "huntingleather"; break;
                case "steel": case "ore": k = "mining"; break;
                case "bill": case "bills": case "craft": case "crafting": k = "production"; break;
                case "animals": case "animal": case "herd": case "pen": case "ranching": case "taming": k = "livestock"; break;
            }
            foreach (var (canon, key) in Kinds) if (key == k) return canon;
            return null;
        }

        public static string KindOut(string canon) => canon switch
        {
            "ForestryClear" => "forestry_clear",
            "HuntingLeather" => "hunting_leather",
            _ => (canon ?? "").ToLowerInvariant(),
        };

        static string RequireKind(string s) => CanonKind(s) ?? throw new RpcError($"unknown stock kind '{s}'. Known: {KindList}");

        // ───────────────────────────── status ─────────────────────────────

        [Rpc("steward.status", "steward overview: {enabled: {scorer, stock}, posture: null|{label, expires_in_hours, work, weights, targets}, pawns: [{id, name, managed, priorities: {WorkTypeDef: 1-4}, top: [{work, priority, why}]}], stock: [{id, kind, label, target, current, enabled, suspended, managed, last_run_hours_ago, designations (created by the job), adopted? (external designations counted toward the target, never removed by the job), failures, summary, notes}], problems: [string], orders: [{id, enabled, summary, acting_on}] (standing orders, see steward.orders), rally: [x,z,w,h]|null}")]
        public static JToken Status(JObject p)
        {
            var map = Map();
            return new JObject
            {
                ["enabled"] = Enabled(),
                ["posture"] = PostureJson(),
                ["pawns"] = PawnRows(map),
                ["stock"] = StockRows(map, false),
                ["problems"] = new JArray(Problems(map)),
                ["orders"] = Orders.OrdersRpc.StatusRows(),
                ["rally"] = Orders.OrdersRpc.RallyJson(map),
            };
        }

        /// <summary>The compact block state.summary carries: {scorer, stock, posture, stock_brief, problems, orders_active, rally}.</summary>
        public static JObject SummaryBlock(Map map)
        {
            var brief = new JArray();
            var comp = StockComponent.For(map);
            if (comp != null)
                foreach (var job in comp.Jobs)
                {
                    int? cur = SafeCount(job);
                    brief.Add(new JObject
                    {
                        ["kind"] = KindOut(job.Kind),
                        ["target"] = job.EffectiveTarget,
                        ["current"] = cur.HasValue ? (JToken)cur.Value : JValue.CreateNull(),
                        ["ok"] = cur.HasValue && job.CountMeetsTarget(cur.Value),
                    });
                }
            return new JObject
            {
                ["scorer"] = StewardSwitch.ScorerEnabled,
                ["stock"] = StewardSwitch.StockEnabled,
                ["posture"] = StewardTuning.PostureActive() ? (JToken)StewardTuning.PostureLabel : JValue.CreateNull(),
                ["stock_brief"] = brief,
                ["problems"] = new JArray(Problems(map)),
                ["orders_active"] = Orders.OrdersRpc.ActiveIds(),
                ["rally"] = StewardGame.Current?.HasRally(map) ?? false,
            };
        }

        static JObject Enabled() => new JObject { ["scorer"] = StewardSwitch.ScorerEnabled, ["stock"] = StewardSwitch.StockEnabled };

        static IEnumerable<WorkTypeDef> WorkTypes => DefDatabase<WorkTypeDef>.AllDefs.Where(w => w.visible).OrderByDescending(w => w.naturalPriority);

        static JArray PawnRows(Map map)
        {
            var arr = new JArray();
            var scorer = ScorerComponent.For(map);
            foreach (var pawn in map.mapPawns.FreeColonists.OrderBy(x => x.LabelShort))
            {
                bool managed = ScorerGate.IsManaged(pawn);
                var o = new JObject { ["id"] = pawn.ThingID, ["name"] = pawn.LabelShort, ["managed"] = managed };
                var pri = new JObject();
                if (pawn.workSettings != null && pawn.workSettings.EverWork)
                    foreach (var wt in WorkTypes)
                    {
                        int v = pawn.workSettings.GetPriority(wt);
                        if (v > 0) pri[wt.defName] = v;
                    }
                o["priorities"] = pri;
                var top = new JArray();
                if (managed && scorer != null)
                {
                    var cached = scorer.TryGetPriorities(pawn);
                    if (cached != null)
                    {
                        var rows = cached.Values
                            .Select(pr => (pr, gp: SafeGamePriority(pr)))
                            .Where(t => t.gp > 0)
                            .OrderBy(t => t.gp).ThenByDescending(t => t.pr.Value).Take(3);
                        foreach (var (pr, gp) in rows)
                            top.Add(new JObject { ["work"] = pr.WorkTypeDef.defName, ["priority"] = gp, ["why"] = Why(pr, 2) });
                    }
                }
                o["top"] = top;
                arr.Add(o);
            }
            return arr;
        }

        static int SafeGamePriority(Priority pr) { try { return pr.ToGamePriority(); } catch { return 0; } }

        /// <summary>Top-n reasons: enable/disable first, then by |delta|; explicit Set steps only when nothing else exists.</summary>
        static string Why(Priority pr, int n)
        {
            var reasons = pr.Reasons ?? new List<PriorityReason>();
            var ranked = reasons
                .Where(r => r.Kind != PriorityReasonKind.Set)
                .OrderByDescending(r => r.Kind == PriorityReasonKind.Enable || r.Kind == PriorityReasonKind.Disable ? 1 : 0)
                .ThenByDescending(r => Math.Abs(r.Delta))
                .Take(n).ToList();
            if (ranked.Count == 0) ranked = reasons.Take(n).ToList();
            return string.Join("; ", ranked.Select(r => r.Kind == PriorityReasonKind.Add || r.Kind == PriorityReasonKind.Multiply ? $"{r.Label} {r.Delta:+0.00;-0.00}" : r.ToString()));
        }

        static int? SafeCount(StockJob job) { try { return job.Trigger == null ? (int?)null : job.CurrentCount; } catch { return null; } }

        static JArray StockRows(Map map, bool full)
        {
            var arr = new JArray();
            var comp = StockComponent.For(map);
            if (comp == null) return arr;
            foreach (var job in comp.Jobs) arr.Add(JobRow(comp, job, full));
            return arr;
        }

        static JObject JobRow(StockComponent comp, StockJob job, bool full)
        {
            int? cur = SafeCount(job);
            int tick = Find.TickManager.TicksGame;
            var o = new JObject
            {
                ["id"] = job.Id,
                ["kind"] = KindOut(job.Kind),
                ["label"] = job.Label,
                ["target"] = job.EffectiveTarget,
                ["current"] = cur.HasValue ? (JToken)cur.Value : JValue.CreateNull(),
                ["enabled"] = StewardSwitch.StockEnabled && comp.IsKindEnabled(job),
                ["suspended"] = job.Suspended,
                ["managed"] = job.Managed,
                ["last_run_hours_ago"] = job.LastRunTick >= 0 ? (JToken)Math.Round((tick - job.LastRunTick) / TicksPerHour, 1) : JValue.CreateNull(),
                ["designations"] = DesignationCount(job),
                ["failures"] = job.ConsecutiveFailures,
                ["summary"] = job.LastRunSummary != null ? (JToken)job.LastRunSummary : JValue.CreateNull(),
                ["notes"] = new JArray(job.Notes.Reverse().Take(full ? 20 : 3)),
            };
            if (job.AdoptedDesignations.Count > 0) o["adopted"] = job.AdoptedDesignations.Count;
            if (job.Trigger.TargetCount != job.EffectiveTarget) o["base_target"] = job.Trigger.TargetCount;
            if (StewardLedger.IsStalled(job)) o["stalled"] = true;
            if (job.RunsWithoutTargets > 0) o["runs_without_targets"] = job.RunsWithoutTargets;
            if (job is StockJob_Livestock lv)
            {
                o["species"] = lv.Species?.defName;
                o["min"] = lv.EffectiveMin;
                o["counts"] = BucketJson(lv.Counts());
                if (lv.PerBucket) o["buckets"] = BucketTargetsJson(lv);
            }
            if (!full) return o;
            o["allowed"] = new JArray(AllowedNames(job));
            o["available"] = new JArray(AvailableNames(job).Take(80));
            o["counted"] = new JArray(job.Trigger.ThresholdFilter.AllowedThingDefs.Select(d => d.defName));
            o["auto_scaled"] = job.AutoScaled;
            o["interval_hours"] = Math.Round(job.UpdateIntervalTicks / TicksPerHour, 2);
            o["check_reachable"] = job.CheckReachable;
            o["settings"] = KindSettings(job);
            return o;
        }

        /// <summary>Designations the job created (adopted external ones are reported separately as "adopted").</summary>
        static int DesignationCount(StockJob job)
        {
            int n = job.Designations.Count;
            if (job is StockJob_Mining m) n += m.HaulDesignations.Count + m.DeconstructDesignations.Count;
            if (job is StockJob_Livestock lv) n += lv.SlaughterDesignations.Count;
            return n;
        }

        static JObject BucketJson(int[] counts)
        {
            var o = new JObject();
            for (int b = 0; b < LivestockRule.BucketCount; b++) o[LivestockRule.BucketNames[b]] = counts[b];
            return o;
        }

        static JObject BucketTargetsJson(StockJob_Livestock lv)
        {
            var t = lv.EffectiveTargets();
            var o = new JObject();
            for (int b = 0; b < LivestockRule.BucketCount; b++)
                o[LivestockRule.BucketNames[b]] = new JObject
                {
                    ["min"] = t.MinOf(b),
                    ["max"] = t.MaxOf(b) < 0 ? JValue.CreateNull() : (JToken)t.MaxOf(b),
                };
            return o;
        }

        static IEnumerable<string> AllowedNames(StockJob job) => job switch
        {
            StockJob_Forestry f => f.AllowedTrees.Select(d => d.defName),
            StockJob_Foraging g => g.AllowedPlants.Select(d => d.defName),
            StockJob_Hunting h => h.AllowedAnimals.Select(d => d.defName),
            StockJob_Mining m => m.Trigger.ThresholdFilter.AllowedThingDefs.Select(d => d.defName),
            StockJob_Production pr => pr.Recipe != null ? new[] { pr.Recipe.defName } : Enumerable.Empty<string>(),
            StockJob_Livestock lv => lv.Train.Select(t => t.defName),
            _ => job.Targets,
        };

        static IEnumerable<string> AvailableNames(StockJob job) => job switch
        {
            StockJob_Forestry f => f.AllPlants.Select(d => d.defName),
            StockJob_Foraging g => g.AllPlants.Select(d => d.defName),
            StockJob_Hunting h => h.AllAnimals.Select(d => d.defName + (HuntSafety.MayHunt(job.Map, d) ? "" : " (dangerous)")),
            StockJob_Mining m => m.Trigger.ParentFilter.AllowedThingDefs.Select(d => d.defName),
            StockJob_Production pr => pr.CandidateTables.Select(t => t.ThingID),
            StockJob_Livestock lv => lv.AvailableTrainables().Select(t => t.defName),
            _ => Enumerable.Empty<string>(),
        };

        static JObject KindSettings(StockJob job)
        {
            var s = StewardMod.Settings.stock;
            var o = new JObject { ["max_radius"] = s.MaxWorkRadius, ["danger_avoid_radius"] = s.DangerAvoidRadius };
            switch (job)
            {
                case StockJob_Forestry f:
                    o["type"] = f.Type.ToString(); o["allow_saplings"] = f.AllowSaplings; o["area"] = f.LoggingArea?.Label; o["invert_area"] = f.InvertLoggingArea;
                    if (f.ClearAreas.Count > 0) o["clear_areas"] = new JArray(f.ClearAreas.Select(a => a.Label));
                    break;
                case StockJob_Foraging g:
                    o["area"] = g.ForagingArea?.Label; o["invert_area"] = g.InvertForagingArea; o["force_fully_mature"] = g.ForceFullyMature;
                    break;
                case StockJob_Hunting h:
                    o["resource"] = h.TargetResource.ToString(); o["area"] = h.HuntingGrounds?.Label; o["invert_area"] = h.InvertHuntingGrounds;
                    o["unforbid_corpses"] = h.UnforbidCorpses; o["hunt_predators"] = s.HuntPredators; o["expected_from_designated"] = h.ExpectedAdditionalCount;
                    break;
                case StockJob_Mining m:
                    o["allow_mining"] = m.AllowMining; o["haul_chunks"] = m.HaulMapChunks; o["deconstruct_buildings"] = m.DeconstructBuildings;
                    o["check_roof_support"] = m.CheckRoofSupport; o["check_room_division"] = m.CheckRoomDivision; o["mine_thick_roofs"] = m.MineThickRoofs;
                    o["area"] = m.MiningArea?.Label; o["invert_area"] = m.InvertMiningArea;
                    break;
                case StockJob_Production pr:
                    o["recipe"] = pr.Recipe?.defName; o["table"] = pr.Table?.ThingID; o["table_label"] = pr.Table?.LabelCap.ToString();
                    o["bill"] = pr.Bill != null ? new JObject { ["id"] = pr.Bill.GetUniqueLoadID(), ["target"] = pr.Bill.targetCount, ["suspended"] = pr.Bill.suspended } : null;
                    break;
                case StockJob_Livestock lv:
                    o["species"] = lv.Species?.defName; o["species_label"] = lv.Species?.label;
                    o["min"] = lv.Min; o["max"] = lv.Max; o["tame"] = lv.Tame; o["slaughter"] = lv.Slaughter;
                    o["area"] = lv.RestrictArea?.Label; o["train"] = new JArray(lv.Train.Select(t => t.defName));
                    o["buckets"] = lv.PerBucket ? BucketTargetsJson(lv) : null;
                    o["counts"] = BucketJson(lv.Counts());
                    o["dangerous"] = !StockJob_Livestock.MayTameKind(lv.Species);
                    if (!StockJob_Livestock.MayTameKind(lv.Species)) o["danger_reason"] = StockJob_Livestock.DangerReason(lv.Species);
                    o["hunt_predators"] = s.HuntPredators;
                    o["pending_tame"] = lv.Designations.Count; o["pending_slaughter"] = lv.SlaughterDesignations.Count;
                    o["wild_on_map"] = lv.Species != null ? lv.Map.mapPawns.AllPawnsSpawned.Count(p => p.kindDef == lv.Species && p.Faction == null && !p.Dead) : 0;
                    break;
            }
            return o;
        }

        static List<string> Problems(Map map)
        {
            var list = new List<string>();
            var comp = StockComponent.For(map);
            if (comp != null && StewardSwitch.StockEnabled)
            {
                foreach (var job in comp.Jobs)
                {
                    string kind = KindOut(job.Kind);
                    if (!comp.IsKindEnabled(job) || !job.Managed) continue;
                    if (StewardLedger.IsStalled(job))
                        list.Add($"{kind}: stalled ({job.ConsecutiveFailures} failed runs, {job.RunsWithoutTargets} runs without targets){(job.Suspended ? ", suspended" : "")}: {job.LastRunSummary ?? "no progress"}");
                    else if (job is StockJob_Hunting h && !job.Suspended && h.AllowedAnimals.Count == 0 && SafeBelow(job))
                        list.Add($"{kind}: no allowed animals (every kind on this map counts as dangerous; steward.stock.set allow=[...] or hunt_predators=true)");
                    else if (job is StockJob_Production pr && !job.Suspended && pr.Bill == null && job.RunsWithoutTargets > 0)
                        list.Add($"{kind}: no work table can make {pr.Recipe?.label ?? "?"} (build one)");
                    else if (job is StockJob_Livestock lv && !job.Suspended && lv.Species != null && job.RunsWithoutTargets > 0 && SafeBelow(job))
                        list.Add(lv.Tame && !StockJob_Livestock.MayTameKind(lv.Species)
                            ? $"{kind}: {lv.Species.label} below min but it is {StockJob_Livestock.DangerReason(lv.Species)} (hunt_predators=true to tame)"
                            : $"{kind}: {lv.Species.label} below min ({lv.CurrentCount}/{lv.EffectiveMin}) and {(lv.Tame ? "no tameable wild ones on the map" : "taming is off")}");
                }
            }
            foreach (var pawn in map.mapPawns.FreeColonists)
            {
                if (ScorerGate.IsManaged(pawn) || pawn.workSettings == null || !pawn.workSettings.EverWork) continue;
                bool any = WorkTypes.Any(wt => pawn.workSettings.GetPriority(wt) > 0);
                if (!any) list.Add($"{pawn.LabelShort} is unmanaged with every work type disabled (steward.pawn managed=true or ui.set_work)");
            }
            return list;
        }

        static bool SafeBelow(StockJob job) { try { return job.WantsWork; } catch { return false; } }

        // ───────────────────────────── enable / pawn ─────────────────────────────

        [Rpc("steward.enable", "{scorer?: bool, stock?: bool} switch the work-priority scorer and/or the stock jobs at runtime (persisted per game) -> {scorer, stock}")]
        public static JToken Enable(JObject p)
        {
            if (p["scorer"] != null && p["scorer"]!.Type != JTokenType.Null) StewardSwitch.ScorerEnabled = P.Bool(p, "scorer", true);
            if (p["stock"] != null && p["stock"]!.Type != JTokenType.Null) StewardSwitch.StockEnabled = P.Bool(p, "stock", true);
            return Enabled();
        }

        [Rpc("steward.pawn", "{pawn: id|name, managed: bool} managed=false takes the pawn manual (the scorer stops writing its priorities), managed=true hands it back -> {pawn, id, managed}")]
        public static JToken PawnManaged(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            if (p["managed"] == null || p["managed"]!.Type == JTokenType.Null) throw new RpcError("missing param 'managed'");
            bool managed = P.Bool(p, "managed", true);
            ScorerGate.SetManaged(pawn, managed);
            var o = new JObject { ["pawn"] = pawn.LabelShort, ["id"] = pawn.ThingID, ["managed"] = ScorerGate.ManagedFlag(pawn) };
            if (managed && !ScorerGate.IsManaged(pawn)) o["note"] = "flag set but the pawn is not scorable right now (not a free colonist, slave, or no work settings)";
            if (managed && !StewardSwitch.ScorerEnabled) o["note"] = "scorer is disabled (steward.enable scorer=true)";
            return o;
        }

        // ───────────────────────────── explain ─────────────────────────────

        [Rpc("steward.explain", "{pawn: id|name, work?: WorkTypeDef} why the scorer gives this pawn each work priority: [{work, priority: 0-4, score: 0..1, reasons: [{label, delta}]}] sorted best first (all work types, or the one asked)")]
        public static JToken Explain(JObject p)
        {
            var map = Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            if (pawn.workSettings == null) throw new RpcError($"{pawn.LabelShort} has no work settings");
            var scorer = ScorerComponent.For(pawn.Map ?? map) ?? throw new RpcError("no scorer component on the map");
            IEnumerable<WorkTypeDef> wts = P.OptStr(p, "work") is { } w
                ? new[] { Lookup.DefOrNull(typeof(WorkTypeDef), w) as WorkTypeDef ?? throw new RpcError($"unknown work type '{w}'. Known: " + string.Join(", ", WorkTypes.Select(x => x.defName))) }
                : WorkTypes;
            var rows = new List<(int gp, float score, JObject row)>();
            foreach (var wt in wts)
            {
                var pr = scorer.Explain(pawn, wt);
                int gp = SafeGamePriority(pr);
                var reasons = new JArray();
                foreach (var r in pr.Reasons)
                {
                    var ro = new JObject { ["label"] = r.Label, ["delta"] = Math.Round(r.Delta, 3) };
                    if (r.Kind == PriorityReasonKind.Enable || r.Kind == PriorityReasonKind.Disable || r.Kind == PriorityReasonKind.Set) ro["kind"] = r.Kind.ToString().ToLowerInvariant();
                    reasons.Add(ro);
                }
                var row = new JObject { ["work"] = wt.defName, ["priority"] = gp, ["score"] = Math.Round(pr.Value, 3), ["reasons"] = reasons };
                if (pawn.WorkTypeIsDisabled(wt)) row["disabled"] = true;
                rows.Add((gp, pr.Value, row));
            }
            rows.Sort((a, b) =>
            {
                int ka = a.gp == 0 ? 99 : a.gp, kb = b.gp == 0 ? 99 : b.gp;
                int c = ka.CompareTo(kb);
                return c != 0 ? c : b.score.CompareTo(a.score);
            });
            return new JArray(rows.Select(r => r.row));
        }

        // ───────────────────────────── posture ─────────────────────────────

        [Rpc("steward.posture", "{preset?: defend|build|harvest|recover|normal, label?: string (a label equal to a preset name seeds that preset; use another word for a custom posture), hours?: float (default 12), work?: {WorkTypeDef: -1..1 delta ADDED to every managed pawn's 0..1 score: +0.5 = about two priority steps up, -1 = off for everyone; values outside -1..1 are clamped}, weights?: {ConsiderX: multiplier >= 0 on that scorer weight}, targets?: {stock kind: multiplier >= 0 on the job's target count: 1.5 = +50%, 0 = target 0 so the job releases its own designations and idles}, clear?: bool ends the posture} time-boxed colony-wide bias; explicit dicts override preset entries; a new call replaces the old posture -> posture or null")]
        public static JToken Posture(JObject p)
        {
            if (P.Bool(p, "clear", false)) { StewardTuning.ClearPosture(); return JValue.CreateNull(); }
            string? preset = P.OptStr(p, "preset");
            string? label = P.OptStr(p, "label");
            var work = ParseWork(P.Obj(p, "work"));
            var weights = ParseWeights(P.Obj(p, "weights"));
            var targets = ParseTargets(P.Obj(p, "targets"));
            float hours = P.Float(p, "hours", StewardTuning.DefaultPostureHours);
            if (hours <= 0f) throw new RpcError("hours must be > 0");
            bool anyDict = work.Count + weights.Count + targets.Count > 0;

            if (preset == null && label != null && StewardTuning.Presets.ContainsKey(label)) { preset = label; }
            if (preset != null && !StewardTuning.Presets.TryGetValue(preset, out _))
                throw new RpcError($"unknown preset '{preset}'. Known: " + string.Join("|", StewardTuning.Presets.Keys));
            if (preset == null && !anyDict)
                throw new RpcError("give preset (defend|build|harvest|recover|normal), or label + work/weights/targets, or clear:true");

            if (preset != null && string.Equals(preset, "normal", StringComparison.OrdinalIgnoreCase) && !anyDict)
            {
                StewardTuning.ClearPosture();
                return JValue.CreateNull();
            }

            var pw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var pwt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var pt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (preset != null)
            {
                var pr = StewardTuning.Presets[preset];
                foreach (var kv in pr.Work) pw[kv.Key] = kv.Value;
                foreach (var kv in pr.Weights) pwt[kv.Key] = kv.Value;
                foreach (var kv in pr.Targets) pt[kv.Key] = kv.Value;
            }
            foreach (var kv in work) pw[kv.Key] = kv.Value;
            foreach (var kv in weights) pwt[kv.Key] = kv.Value;
            foreach (var kv in targets) pt[kv.Key] = kv.Value;

            string lbl = label ?? preset ?? "custom";
            if (string.Equals(lbl, "normal", StringComparison.OrdinalIgnoreCase)) lbl = "custom";
            StewardTuning.SetPosture(lbl, hours, pw, pwt, pt);
            return PostureJson() ?? (JToken)JValue.CreateNull();
        }

        static JObject? PostureJson()
        {
            if (!StewardTuning.PostureActive()) return null;
            return new JObject
            {
                ["label"] = StewardTuning.PostureLabel,
                ["expires_in_hours"] = Math.Round(StewardTuning.PostureExpiresInHours(), 1),
                ["work"] = new JObject(StewardTuning.PostureWorkDeltas.Select(kv => new JProperty(kv.Key, Math.Round(kv.Value, 3)))),
                ["weights"] = new JObject(StewardTuning.PostureWeightMultipliers.Select(kv => new JProperty(kv.Key, Math.Round(kv.Value, 3)))),
                ["targets"] = new JObject(StewardTuning.PostureTargetMultipliers.Select(kv => new JProperty(CanonKind(kv.Key) is { } c ? KindOut(c) : kv.Key, Math.Round(kv.Value, 3)))),
            };
        }

        static float NumOf(JToken? t, string what)
        {
            if (t == null || t.Type == JTokenType.Null) throw new RpcError($"{what}: value must be a number");
            try { return (float)t; } catch { throw new RpcError($"{what}: value must be a number"); }
        }

        static Dictionary<string, float> ParseWork(JObject? o)
        {
            var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (o == null) return d;
            foreach (var kv in o)
            {
                var wt = Lookup.DefOrNull(typeof(WorkTypeDef), kv.Key) as WorkTypeDef
                    ?? throw new RpcError($"work: unknown work type '{kv.Key}'. Known: " + string.Join(", ", WorkTypes.Select(x => x.defName)));
                float v = NumOf(kv.Value, "work." + kv.Key);
                d[wt.defName] = Math.Max(-1f, Math.Min(1f, v));
            }
            return d;
        }

        static Dictionary<string, float> ParseWeights(JObject? o)
        {
            var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (o == null) return d;
            foreach (var kv in o)
            {
                string name = ScorerSettings.WeightNames.FirstOrDefault(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? ScorerSettings.WeightNames.FirstOrDefault(n => string.Equals(n, "Consider" + kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RpcError($"weights: unknown weight '{kv.Key}'. Known: " + string.Join(", ", ScorerSettings.WeightNames));
                float v = NumOf(kv.Value, "weights." + kv.Key);
                if (v < 0f) throw new RpcError($"weights.{kv.Key}: multiplier must be >= 0");
                d[name] = v;
            }
            return d;
        }

        static Dictionary<string, float> ParseTargets(JObject? o)
        {
            var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (o == null) return d;
            foreach (var kv in o)
            {
                string canon = CanonKind(kv.Key) ?? throw new RpcError($"targets: unknown stock kind '{kv.Key}'. Known: {KindList}");
                float v = NumOf(kv.Value, "targets." + kv.Key);
                if (v < 0f) throw new RpcError($"targets.{kv.Key}: multiplier must be >= 0");
                d[canon] = v;
            }
            return d;
        }

        // ───────────────────────────── stock ─────────────────────────────

        [Rpc("steward.stock.list", "stock jobs in full: status rows plus allowed: [defName], available: [defName], counted: [ThingDef], auto_scaled, interval_hours, check_reachable, settings (per kind: area, invert_area, allow_saplings | force_fully_mature | resource, unforbid_corpses, hunt_predators | haul_chunks, deconstruct_buildings, mine_thick_roofs | recipe, table, bill | livestock: species, min, max, tame, slaughter, area, train, buckets, counts, wild_on_map, pending_tame, pending_slaughter)")]
        public static JToken StockList(JObject p) => StockRows(Map(), true);

        [Rpc("steward.stock.set", "{id|kind, target?: int, suspended?: bool, managed?: bool, allow?: [defName|label], disallow?: [...], allow_all?: bool, disallow_all?: bool, area?: label|null, max_radius?: int (global; 0 = no distance cap, the whole map is fair game — the default), hunt_predators?: bool (global), check_reachable?: bool, label?, interval_hours?: float, table?: thing id (production), min?/max?/tame?/slaughter?/train?: [TrainableDef]/buckets?: {adult_male: {min,max}..}|null (livestock; allow/disallow edit train)} edit a stock job -> full job row")]
        public static JToken StockSet(JObject p)
        {
            var map = Map();
            var comp = Stock(map);
            var job = ResolveJob(comp, p);
            var warnings = new List<string>();
            var settings = StewardMod.Settings.stock;
            bool settingsChanged = false;

            if (P.OptStr(p, "label") is { } label) job.Label = label;
            if (Has(p, "target"))
            {
                int target = P.Int(p, "target");
                if (target < 0) throw new RpcError("target must be >= 0");
                SetTarget(job, target);
            }
            if (Has(p, "suspended"))
            {
                bool s = P.Bool(p, "suspended", false);
                if (job.Suspended && !s) job.ConsecutiveFailures = 0;
                job.Suspended = s;
            }
            if (Has(p, "managed")) job.Managed = P.Bool(p, "managed", true);
            if (Has(p, "check_reachable")) job.CheckReachable = P.Bool(p, "check_reachable", true);
            if (Has(p, "interval_hours"))
            {
                float h = P.Float(p, "interval_hours");
                if (h <= 0f) throw new RpcError("interval_hours must be > 0");
                job.UpdateIntervalTicks = Math.Max(250, (int)(h * TicksPerHour));
            }
            if (Has(p, "max_radius"))
            {
                settings.MaxWorkRadius = Math.Max(0, Math.Min(1000, P.Int(p, "max_radius")));
                settingsChanged = true;
            }
            if (Has(p, "hunt_predators"))
            {
                settings.HuntPredators = P.Bool(p, "hunt_predators", false);
                settingsChanged = true;
            }
            if (p["area"] != null) SetArea(job, p["area"]!.Type == JTokenType.Null ? null : P.Str(p, "area"));
            if (P.OptStr(p, "table") is { } tableId)
            {
                if (!(job is StockJob_Production pr)) throw new RpcError("table only applies to production jobs");
                var t = Lookup.Thing(tableId) as Building ?? throw new RpcError($"{tableId} is not a building");
                if (!pr.CanUse(t)) throw new RpcError($"{t.def.defName} cannot make {pr.Recipe?.defName}. Tables that can: " + string.Join(", ", pr.CandidateTables.Select(b => b.ThingID)));
                pr.SetTable(t);
            }
            if (job is StockJob_Livestock lvs) ApplyLivestock(lvs, p, warnings);
            if (P.Bool(p, "disallow_all", false)) DisallowAll(job);
            if (P.Bool(p, "allow_all", false)) AllowAll(job);
            if (P.Arr(p, "allow") is { } allow) foreach (var t in allow) SetAllowedByName(job, t.ToString(), true, warnings);
            if (P.Arr(p, "disallow") is { } disallow) foreach (var t in disallow) SetAllowedByName(job, t.ToString(), false, warnings);

            if (settingsChanged) StewardMod.Save();
            var row = JobRow(comp, job, true);
            if (warnings.Count > 0) row["warnings"] = new JArray(warnings);
            return row;
        }

        [Rpc("steward.stock.add", "{kind: forestry|forestry_clear|foraging|hunting|hunting_leather|mining|production|livestock, target: int (livestock: = max), allow?: [defName|label], area?: label (forestry_clear: area to clear; livestock: restrict tamed animals to it), recipe?: RecipeDef (production), table?: thing id (production), species: PawnKindDef (livestock), min?: int, max?: int, tame?: true, slaughter?: true, train?: [Obedience|Release|Rescue|Haul], buckets?: {adult_male|adult_female|juvenile_male|juvenile_female: {min, max}} (livestock)} add a stock job -> full job row")]
        public static JToken StockAdd(JObject p)
        {
            var map = Map();
            var comp = Stock(map);
            string canon = RequireKind(P.Str(p, "kind"));
            var warnings = new List<string>();
            if (canon == "Livestock")
            {
                var species = Species(P.Str(p, "species"));
                if (comp.JobsOfType<StockJob_Livestock>().Any(j => j.Species == species))
                    throw new RpcError($"a livestock job for {species.defName} exists (id {comp.JobsOfType<StockJob_Livestock>().First(j => j.Species == species).Id}); steward.stock.set it");
                int max = Has(p, "max") ? P.Int(p, "max") : Has(p, "target") ? P.Int(p, "target") : -1;
                if (max < 0 && !Has(p, "buckets")) throw new RpcError("livestock needs max (or target), or buckets");
                var lv = comp.Add(new StockJob_Livestock(map, species, Has(p, "min") ? P.Int(p, "min") : 0, Math.Max(0, max)));
                lv.AutoScaled = false;
                ApplyLivestock(lv, p, warnings);
                if (p["area"] != null) SetArea(lv, p["area"]!.Type == JTokenType.Null ? null : P.Str(p, "area"));
                if (P.Arr(p, "allow") is { } tr) foreach (var t in tr) SetAllowedByName(lv, t.ToString(), true, warnings);
                var lrow = JobRow(comp, lv, true);
                if (warnings.Count > 0) lrow["warnings"] = new JArray(warnings);
                return lrow;
            }
            int target = P.Int(p, "target");
            if (target < 0) throw new RpcError("target must be >= 0");
            StockJob job;
            switch (canon)
            {
                case "Forestry": job = new StockJob_Forestry(map); break;
                case "ForestryClear":
                {
                    var f = new StockJob_Forestry(map, StockJob_Forestry.JobType.ClearArea);
                    string areaLabel = P.Str(p, "area");
                    var area = Lookup.AreaOrNull(areaLabel) ?? throw new RpcError($"no area '{areaLabel}'");
                    f.ClearAreas.Add(area);
                    job = f;
                    break;
                }
                case "Foraging": job = new StockJob_Foraging(map); break;
                case "Hunting": job = new StockJob_Hunting(map, StockJob_Hunting.HuntingTargetResource.Meat); break;
                case "HuntingLeather": job = new StockJob_Hunting(map, StockJob_Hunting.HuntingTargetResource.Leather); break;
                case "Mining": job = new StockJob_Mining(map); break;
                case "Production":
                {
                    var recipe = Lookup.Def<RecipeDef>(P.Str(p, "recipe"));
                    if (recipe.products == null || recipe.products.Count == 0) throw new RpcError($"{recipe.defName} produces nothing countable");
                    if (!recipe.AvailableNow) throw new RpcError($"{recipe.defName} is not available now (research?)");
                    Building? table = null;
                    if (P.OptStr(p, "table") is { } tid) table = Lookup.Thing(tid) as Building ?? throw new RpcError($"{tid} is not a building");
                    var pr = new StockJob_Production(map, recipe, table);
                    if (table != null && !pr.CanUse(table)) throw new RpcError($"{table.def.defName} cannot make {recipe.defName}");
                    job = pr;
                    break;
                }
                default: throw new RpcError($"unsupported kind '{canon}'");
            }
            comp.Add(job);
            SetTarget(job, target);
            if (P.Arr(p, "allow") is { } allow && allow.Count > 0)
            {
                DisallowAll(job);
                foreach (var t in allow) SetAllowedByName(job, t.ToString(), true, warnings);
            }
            var row = JobRow(comp, job, true);
            if (warnings.Count > 0) row["warnings"] = new JArray(warnings);
            return row;
        }

        [Rpc("steward.stock.remove", "{id|kind} delete a stock job and the designations/bill it created -> {removed: true, id, kind}")]
        public static JToken StockRemove(JObject p)
        {
            var comp = Stock(Map());
            var job = ResolveJob(comp, p);
            comp.Remove(job);
            return new JObject { ["removed"] = true, ["id"] = job.Id, ["kind"] = KindOut(job.Kind) };
        }

        [Rpc("steward.stock.run", "{id|kind} run a stock job now (ignores its interval and suspension) -> {ran: bool (work was designated/changed), summary, current, target, designations}")]
        public static JToken StockRun(JObject p)
        {
            var comp = Stock(Map());
            var job = ResolveJob(comp, p);
            bool ran = comp.RunJob(job);
            return new JObject
            {
                ["ran"] = ran,
                ["summary"] = job.LastRunSummary != null ? (JToken)job.LastRunSummary : JValue.CreateNull(),
                ["current"] = SafeCount(job) is { } c ? (JToken)c : JValue.CreateNull(),
                ["target"] = job.EffectiveTarget,
                ["designations"] = DesignationCount(job),
                ["failures"] = job.ConsecutiveFailures,
            };
        }

        static bool Has(JObject p, string key) => p[key] != null && p[key]!.Type != JTokenType.Null;

        static void SetTarget(StockJob job, int target)
        {
            if (target > job.Trigger.MaxUpperThreshold) job.Trigger.MaxUpperThreshold = Math.Min(100000, target);
            job.Trigger.TargetCount = target;
            job.AutoScaled = false;
        }

        static StockJob ResolveJob(StockComponent comp, JObject p)
        {
            var idTok = p["id"];
            if (idTok != null && idTok.Type != JTokenType.Null)
            {
                if (idTok.Type == JTokenType.Integer || int.TryParse(idTok.ToString(), out _))
                {
                    int id = (int)idTok;
                    return comp.FindById(id) ?? throw new RpcError($"no stock job with id {id}. Jobs: " + JobList(comp));
                }
                return ByKind(comp, idTok.ToString());
            }
            if (Has(p, "kind")) return ByKind(comp, P.Str(p, "kind"));
            throw new RpcError("give id or kind. Jobs: " + JobList(comp));
        }

        static string JobList(StockComponent comp) => comp.Jobs.Count == 0 ? "(none)" : string.Join(", ", comp.Jobs.Select(j => $"{j.Id}={KindOut(j.Kind)}"));

        static StockJob ByKind(StockComponent comp, string kind)
        {
            string canon = RequireKind(kind);
            var matches = comp.Jobs.Where(j => string.Equals(j.Kind, canon, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new RpcError($"no {KindOut(canon)} job (steward.stock.add). Jobs: " + JobList(comp));
            if (matches.Count > 1) throw new RpcError($"several {KindOut(canon)} jobs (ids {string.Join(", ", matches.Select(j => j.Id))}); use id");
            return matches[0];
        }

        static void SetArea(StockJob job, string? label)
        {
            Area? area = null;
            if (label != null && !string.Equals(label, "Unrestricted", StringComparison.OrdinalIgnoreCase))
                area = Lookup.AreaOrNull(label) ?? throw new RpcError($"no area '{label}'. Known: " + string.Join(", ", job.Map.areaManager.AllAreas.Select(a => a.Label)));
            switch (job)
            {
                case StockJob_Forestry f: f.LoggingArea = area; break;
                case StockJob_Foraging g: g.ForagingArea = area; break;
                case StockJob_Hunting h: h.HuntingGrounds = area; break;
                case StockJob_Mining m: m.MiningArea = area; break;
                case StockJob_Livestock lv: lv.RestrictArea = area; break;
                default: throw new RpcError($"{KindOut(job.Kind)} jobs have no area");
            }
        }

        static bool NameMatches(Def d, string name) =>
            string.Equals(d.defName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(d.label, name, StringComparison.OrdinalIgnoreCase);

        static T? Match<T>(IEnumerable<T> pool, string name) where T : Def
        {
            var list = pool.ToList();
            var exact = list.FirstOrDefault(d => NameMatches(d, name));
            if (exact != null) return exact;
            var partial = list.Where(d => d.label != null && d.label.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        static void SetAllowedByName(StockJob job, string name, bool allow, List<string> warnings)
        {
            switch (job)
            {
                case StockJob_Forestry f:
                {
                    var plants = PlantsFor(f.AllPlants, name);
                    if (plants.Count == 0) throw new RpcError($"no tree '{name}'. Available: " + string.Join(", ", f.AllPlants.Select(d => d.defName)));
                    foreach (var d in plants) f.SetAllowed(d, allow);
                    return;
                }
                case StockJob_Foraging g:
                {
                    var plants = PlantsFor(g.AllPlants, name);
                    if (plants.Count == 0) throw new RpcError($"no forageable plant '{name}'. Available: " + string.Join(", ", g.AllPlants.Select(d => d.defName)));
                    foreach (var d in plants) g.SetAllowed(d, allow);
                    return;
                }
                case StockJob_Hunting h:
                {
                    var kind = Match(h.AllAnimals, name)
                        ?? (Lookup.DefOrNull(typeof(PawnKindDef), name) as PawnKindDef is { } pk && pk.RaceProps != null && pk.RaceProps.Animal ? pk : null)
                        ?? throw new RpcError($"no animal kind '{name}'. Available: " + string.Join(", ", h.AllAnimals.Select(d => d.defName)));
                    if (allow && !HuntSafety.MayHunt(job.Map, kind)) warnings.Add($"{kind.defName} allowed although dangerous: {HuntSafety.DangerReason(kind)}");
                    h.SetAllowed(kind, allow);
                    return;
                }
                case StockJob_Mining m:
                {
                    var def = Match(m.Trigger.ParentFilter.AllowedThingDefs, name)
                        ?? throw new RpcError($"no minable resource '{name}'. Available: " + string.Join(", ", m.Trigger.ParentFilter.AllowedThingDefs.Select(d => d.defName)));
                    m.Trigger.ThresholdFilter.SetAllow(def, allow);
                    return;
                }
                case StockJob_Production _:
                    throw new RpcError("production jobs have no allow list (the recipe is fixed at add time)");
                case StockJob_Livestock lv:
                {
                    var td = Trainable(name);
                    if (allow) AddTrainable(lv, td, warnings); else lv.Train.Remove(td);
                    return;
                }
                default:
                    throw new RpcError($"{KindOut(job.Kind)} jobs have no allow list");
            }
        }

        static TrainableDef Trainable(string name) =>
            Match(DefDatabase<TrainableDef>.AllDefsListForReading.Where(t => t != TrainableDefOf.Tameness), name)
            ?? throw new RpcError($"no trainable '{name}'. Known: " + string.Join(", ", DefDatabase<TrainableDef>.AllDefsListForReading.Where(t => t != TrainableDefOf.Tameness).Select(t => t.defName)));

        static void AddTrainable(StockJob_Livestock lv, TrainableDef td, List<string> warnings)
        {
            if (lv.Species?.race != null && !Pawn_TrainingTracker.CanAssignToTrain(td, lv.Species.race, out _).Accepted)
            {
                warnings.Add($"{lv.Species.defName} cannot learn {td.defName} (trainability); skipped");
                return;
            }
            if (!lv.Train.Contains(td)) lv.Train.Add(td);
        }

        /// <summary>{adult_male: {min, max} | [min, max] | max, ...}; missing buckets are 0..∞.</summary>
        static void ApplyBuckets(StockJob_Livestock lv, JToken tok)
        {
            if (tok.Type == JTokenType.Null) { lv.SetBuckets(null, null); return; }
            if (!(tok is JObject o)) throw new RpcError("buckets must be an object {adult_male|adult_female|juvenile_male|juvenile_female: {min, max} | [min, max]} or null");
            var min = new int[LivestockRule.BucketCount];
            var max = new int[LivestockRule.BucketCount];
            for (int b = 0; b < LivestockRule.BucketCount; b++) max[b] = LivestockRule.NoLimit;
            foreach (var kv in o)
            {
                int b = LivestockRule.BucketIndex(kv.Key);
                if (b < 0) throw new RpcError($"buckets: unknown bucket '{kv.Key}'. Known: " + string.Join(", ", LivestockRule.BucketNames));
                var v = kv.Value;
                int lo = 0, hi = LivestockRule.NoLimit;
                if (v is JArray arr)
                {
                    if (arr.Count > 0) lo = (int)NumOf(arr[0], $"buckets.{kv.Key}[0]");
                    if (arr.Count > 1 && arr[1].Type != JTokenType.Null) hi = (int)NumOf(arr[1], $"buckets.{kv.Key}[1]");
                }
                else if (v is JObject bo)
                {
                    if (Has(bo, "min")) lo = P.Int(bo, "min");
                    if (Has(bo, "max")) hi = P.Int(bo, "max");
                }
                else if (v != null && v.Type != JTokenType.Null) hi = (int)NumOf(v, $"buckets.{kv.Key}");
                if (lo < 0) throw new RpcError($"buckets.{kv.Key}: min must be >= 0");
                if (hi >= 0 && hi < lo) throw new RpcError($"buckets.{kv.Key}: max must be >= min");
                min[b] = lo; max[b] = hi < 0 ? LivestockRule.NoLimit : hi;
            }
            lv.SetBuckets(min, max);
        }

        static PawnKindDef Species(string name)
        {
            var animals = DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.RaceProps != null && k.RaceProps.Animal);
            return Match(animals, name)
                ?? throw new RpcError($"no animal kind '{name}' (PawnKindDef defName or label)");
        }

        /// <summary>Livestock knobs shared by add and set: min/max/tame/slaughter/train/buckets.</summary>
        static void ApplyLivestock(StockJob_Livestock lv, JObject p, List<string> warnings)
        {
            if (Has(p, "min"))
            {
                int min = P.Int(p, "min");
                if (min < 0) throw new RpcError("min must be >= 0");
                lv.Min = min;
            }
            if (Has(p, "max"))
            {
                int max = P.Int(p, "max");
                if (max < 0) throw new RpcError("max must be >= 0");
                SetTarget(lv, max);
            }
            if (lv.Min > lv.Max) warnings.Add($"min {lv.Min} > max {lv.Max}: max is treated as {lv.Min}");
            if (Has(p, "tame")) lv.Tame = P.Bool(p, "tame", true);
            if (Has(p, "slaughter")) lv.Slaughter = P.Bool(p, "slaughter", true);
            if (p["train"] != null)
            {
                lv.Train.Clear();
                if (p["train"] is JArray arr) foreach (var t in arr) AddTrainable(lv, Trainable(t.ToString()), warnings);
                else if (p["train"]!.Type != JTokenType.Null) AddTrainable(lv, Trainable(p["train"]!.ToString()), warnings);
            }
            if (p["buckets"] != null) ApplyBuckets(lv, p["buckets"]!);
            if (lv.Tame && lv.Species != null && !StockJob_Livestock.MayTameKind(lv.Species))
                warnings.Add($"{lv.Species.defName} is {StockJob_Livestock.DangerReason(lv.Species)}: taming needs hunt_predators=true");
        }

        /// <summary>A plant by name, or every plant yielding the named product (e.g. "WoodLog", "RawBerries").</summary>
        static List<ThingDef> PlantsFor(IEnumerable<ThingDef> pool, string name)
        {
            var list = pool.ToList();
            var one = Match(list, name);
            if (one != null) return new List<ThingDef> { one };
            var products = list.Where(d => d.plant?.harvestedThingDef != null).Select(d => d.plant.harvestedThingDef).Distinct();
            var product = Match(products, name);
            return product == null ? new List<ThingDef>() : list.Where(d => d.plant?.harvestedThingDef == product).ToList();
        }

        static void AllowAll(StockJob job)
        {
            switch (job)
            {
                case StockJob_Forestry f: foreach (var d in f.AllPlants.ToList()) f.SetAllowed(d, true); break;
                case StockJob_Foraging g: foreach (var d in g.AllPlants.ToList()) g.SetAllowed(d, true); break;
                case StockJob_Hunting h: foreach (var k in h.AllAnimals.ToList()) if (HuntSafety.MayHunt(job.Map, k)) h.SetAllowed(k, true); break;
                case StockJob_Mining m: foreach (var d in m.Trigger.ParentFilter.AllowedThingDefs.ToList()) m.Trigger.ThresholdFilter.SetAllow(d, true); break;
                case StockJob_Livestock lv: foreach (var td in lv.AvailableTrainables().ToList()) if (!lv.Train.Contains(td)) lv.Train.Add(td); break;
                default: throw new RpcError($"{KindOut(job.Kind)} jobs have no allow list");
            }
        }

        static void DisallowAll(StockJob job)
        {
            switch (job)
            {
                case StockJob_Forestry f: foreach (var d in f.AllowedTrees.ToList()) f.SetAllowed(d, false); break;
                case StockJob_Foraging g: foreach (var d in g.AllowedPlants.ToList()) g.SetAllowed(d, false); break;
                case StockJob_Hunting h: foreach (var k in h.AllowedAnimals.ToList()) h.SetAllowed(k, false); break;
                case StockJob_Mining m: foreach (var d in m.Trigger.ThresholdFilter.AllowedThingDefs.ToList()) m.Trigger.ThresholdFilter.SetAllow(d, false); break;
                case StockJob_Production _: break; // nothing to clear
                case StockJob_Livestock lv: lv.Train.Clear(); break;
                default: throw new RpcError($"{KindOut(job.Kind)} jobs have no allow list");
            }
        }

        // ───────────────────────────── settings ─────────────────────────────

        [Rpc("steward.settings", "{scorer?: {field: value...}, stock?: {field: value...}} read (no args) or write scorer/stock settings by field name (bools/ints/floats clamped; scorer.globalWorkAdjustments: {WorkTypeDef: -1..1}; Enabled = mod default, use steward.enable for runtime) -> {scorer: {...}, stock: {...}}")]
        public static JToken Settings(JObject p)
        {
            var s = StewardMod.Settings;
            bool changed = false;
            if (P.Obj(p, "scorer") is { } so) changed |= ApplySettings(s.scorer, so, "scorer");
            if (P.Obj(p, "stock") is { } st) changed |= ApplySettings(s.stock, st, "stock");
            if (changed) StewardMod.Save();
            return new JObject { ["scorer"] = DumpSettings(s.scorer), ["stock"] = DumpSettings(s.stock) };
        }

        static IEnumerable<FieldInfo> SettingFields(object target) =>
            target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly && !f.IsLiteral);

        static (float min, float max) Range(string name) => name switch
        {
            "TicksBetweenActions" => (1, 1000),
            "SchedulerIntervalTicks" => (30, 60000),
            "MaxDesignationsPerJob" => (1, 500),
            "BudgetMsWarn" => (1, 10000),
            "MaxWorkRadius" => (0, 1000),
            "DangerAvoidRadius" => (0, 300),
            _ => (0, 10),
        };

        static bool ApplySettings(object target, JObject values, string group)
        {
            bool changed = false;
            var fields = SettingFields(target).ToList();
            foreach (var kv in values)
            {
                var f = fields.FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RpcError($"{group}: unknown field '{kv.Key}'. Known: " + string.Join(", ", fields.Select(x => x.Name)));
                var v = kv.Value;
                if (v == null || v.Type == JTokenType.Null) throw new RpcError($"{group}.{f.Name}: value must not be null");
                if (f.FieldType == typeof(bool))
                {
                    bool b; try { b = (bool)v; } catch { throw new RpcError($"{group}.{f.Name}: must be a boolean"); }
                    f.SetValue(target, b); changed = true;
                }
                else if (f.FieldType == typeof(int))
                {
                    int i; try { i = (int)v; } catch { throw new RpcError($"{group}.{f.Name}: must be an integer"); }
                    var (lo, hi) = Range(f.Name);
                    f.SetValue(target, (int)Math.Max(lo, Math.Min(hi, i))); changed = true;
                }
                else if (f.FieldType == typeof(float))
                {
                    float x; try { x = (float)v; } catch { throw new RpcError($"{group}.{f.Name}: must be a number"); }
                    var (lo, hi) = Range(f.Name);
                    f.SetValue(target, Math.Max(lo, Math.Min(hi, x))); changed = true;
                }
                else if (f.FieldType == typeof(Dictionary<string, float>))
                {
                    if (!(v is JObject o)) throw new RpcError($"{group}.{f.Name}: must be an object {{WorkTypeDef: -1..1}}");
                    var dict = (Dictionary<string, float>?)f.GetValue(target) ?? new Dictionary<string, float>();
                    foreach (var e in o)
                    {
                        var wt = Lookup.DefOrNull(typeof(WorkTypeDef), e.Key) as WorkTypeDef
                            ?? throw new RpcError($"{group}.{f.Name}: unknown work type '{e.Key}'. Known: " + string.Join(", ", WorkTypes.Select(w => w.defName)));
                        if (e.Value == null || e.Value.Type == JTokenType.Null) { dict.Remove(wt.defName); continue; }
                        float x; try { x = (float)e.Value; } catch { throw new RpcError($"{group}.{f.Name}.{e.Key}: must be a number"); }
                        dict[wt.defName] = Math.Max(-1f, Math.Min(1f, x));
                    }
                    f.SetValue(target, dict); changed = true;
                }
                else throw new RpcError($"{group}.{f.Name}: unsupported field type {f.FieldType.Name}");
            }
            return changed;
        }

        static JObject DumpSettings(object target)
        {
            var o = new JObject();
            foreach (var f in SettingFields(target))
            {
                var v = f.GetValue(target);
                if (v is Dictionary<string, float> d) o[f.Name] = new JObject(d.Select(kv => new JProperty(kv.Key, Math.Round(kv.Value, 3))));
                else if (v is float x) o[f.Name] = Math.Round(x, 3);
                else o[f.Name] = v != null ? JToken.FromObject(v) : JValue.CreateNull();
            }
            return o;
        }

        // ───────────────────────────── research ─────────────────────────────

        [Rpc("steward.research", "{queue?: [ResearchProjectDef...], append?: false, clear?: bool} ordered research queue: when the current project finishes (or none is set) the next queued project that can start now becomes current; {} reads -> {queue: [defName], current, current_progress, started?, skipped?}")]
        public static JToken Research(JObject p)
        {
            Map();
            var g = StewardGame.Current ?? throw new RpcError("no steward game component");
            ResearchProjectDef? started = null;
            var skipped = new JArray();
            if (P.Bool(p, "clear", false)) StewardResearch.Clear();
            if (P.Arr(p, "queue") is { } arr)
            {
                var names = new List<string>();
                foreach (var t in arr)
                {
                    var def = Lookup.Def<ResearchProjectDef>(t.ToString());
                    if (def.IsFinished) { skipped.Add($"{def.defName} (finished)"); continue; }
                    names.Add(def.defName);
                }
                started = StewardResearch.Set(names, P.Bool(p, "append", false));
            }
            var cur = Find.ResearchManager.GetProject();
            var o = new JObject
            {
                ["queue"] = new JArray(g.researchQueue),
                ["queue_detail"] = new JArray(g.researchQueue.Select(n => StewardResearch.Def(n) is { } d
                    ? new JObject { ["def"] = d.defName, ["label"] = d.label, ["available"] = d.CanStartNow, ["progress"] = Math.Round(d.ProgressPercent * 100) }
                    : new JObject { ["def"] = n, ["unknown"] = true })),
                ["current"] = cur?.defName,
                ["current_progress"] = cur != null ? Math.Round(cur.ProgressPercent * 100) : 0,
            };
            if (started != null) o["started"] = started.defName;
            if (skipped.Count > 0) o["skipped"] = skipped;
            return o;
        }
    }
}
