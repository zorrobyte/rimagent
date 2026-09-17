// Written for RimBridge (2026): the combat standing order. Draft rules adapted from Autopilot's ThreatDraftReflex /
// ReflexUtil.CanFight (MIT, the user's own code).
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Hostiles that threaten the base (inside the Home area, near the rally, or assaulting) → draft every capable armed
    /// colonist to distinct cells inside the rally rect (cover first), restrict non-fighters to the Home area, hold
    /// positions every 250 ticks, attack the nearest hostile when the rally is overrun; sieges, staging raids and
    /// manhunters with everybody indoors are only watched. 600 hostile-free ticks → undraft what it drafted, restore
    /// areas, run rescue once. All state is per map.
    /// </summary>
    public sealed class Order_Combat : Order
    {
        public override string Id => "combat";
        public override string Label => "Combat: draft and rally on hostiles";
        public override string Doc =>
            "Hostiles are the attackTargetsCache targets that are spawned, not fogged, not downed, not threat-disabled, minus the colony's own " +
            "pawns (berserk colonists, rebelling slaves, prison-breaking prisoners and colony animals are never targeted). Each hostile is " +
            $"classified: inside the Home area or within {ThreatRules.EngageRadius:0} cells of the rally centre → engage; a siege → watch; a raid whose " +
            "duty is assault/breach/sap/kidnap/steal → engage, one that is staging or sleeping → watch; a manhunter → engage only while a colonist " +
            "is outside the Home area; turrets and other structures → engage only when near. With nothing to engage the order sits in watch " +
            "mode (summary 'watching: …', nobody drafted, unforbid/corpses keep running). Engaged: every capable armed managed colonist is drafted " +
            "and sent to a distinct cell inside the rally rect (steward.orders.rally; cover-adjacent cells first; a 13x13 square around the base " +
            "centre when no rally is set; nowhere on a map without a base). Non-fighters are restricted to the Home area. Positions are re-issued " +
            "every 250 ticks; pawns undrafted by a job are re-drafted. A fighter whose food or rest falls below 15% with no hostile within " +
            $"{CombatTimers.NearHostileRadius:0} cells is relieved (undrafted) and drafted again once both are above 50%; not while overrun. A hostile " +
            "inside the home area or within 5 cells of the rally centre switches every fighter to attack its nearest hostile. After 600 " +
            "hostile-free ticks the order undrafts what it drafted (not pawns under a live ui.draft/goto/attack/drafted order/draft gizmo " +
            "touch: they are retried each pass until the touch expires and named in the summary), restores the previous allowed areas " +
            "(unless the director changed the area meanwhile), runs the rescue order once and writes combat_released. Engagements longer " +
            "than half a day are reported once as combat_prolonged. Disabling the order while engaged releases first.";
        public override int IntervalTicks => 60;
        public override IReadOnlyList<string>? TouchScopes => CombatScopes.Touch;

        public const int DefaultRallyHalfSize = 6;

        public void ForgetAssignments()
        {
            var g = StewardGame.Current;
            if (g == null) return;
            foreach (var kv in g.CombatStates) kv.Value.assigned.Clear();
        }

        public override IEnumerable<string> Explain()
        {
            var g = StewardGame.Current;
            yield return "hostiles: spawned attackTargetsCache targets that are not fogged, downed or threat-disabled; never the colony's own pawns (Faction or HostFaction == player: berserk colonists, rebelling slaves, prison breaks, colony animals)";
            yield return $"engage: hostile inside the Home area or within {ThreatRules.EngageRadius:0} cells of the rally centre; a raid with an assault/breach/sap/kidnap/steal duty; a manhunter while a colonist is outside Home";
            yield return "watch (no draft, unforbid/corpses keep running): a siege; a staging or sleeping raid; mech-cluster guards and turrets far away; manhunters with everyone inside Home";
            yield return "draft: free managed colonists with a weapon; skip downed, prisoners, slaves, children, incapable of violence, mental state, health < 30%, unmanaged (steward.pawn managed=false), touched by ui.draft/goto/attack, a drafted right-click order, ui.job, the draft gizmo or an area change in the last hour";
            yield return $"needs: a drafted fighter with food or rest < {CombatEligibility.ReliefPct:P0} and no hostile within {CombatTimers.NearHostileRadius:0} cells is relieved (undrafted) to eat/sleep and drafted again above {CombatEligibility.RecoverPct:P0}; never while overrun";
            yield return "rally: distinct standable cells inside the rally rect, cover-adjacent cells first, spread apart; without a rally rect a 13x13 square around the base centre; on a map without Home area or colonist buildings pawns are drafted but not moved";
            yield return "non-fighters: restricted to the Home area (previous area remembered and restored on release unless the director changed it)";
            yield return "hold: every 250 ticks re-send wanderers and re-draft pawns a job undrafted; only pawns the order actually drafted are undrafted at release";
            yield return "overrun: a hostile inside the home area or within 5 cells of the rally centre → every fighter attacks its nearest engaged hostile (melee weapon → melee, else ranged)";
            yield return "release: 600 hostile-free ticks → undraft what the order drafted (a pawn under a live ui.draft/goto/attack/drafted-order/draft-gizmo touch stays drafted and is retried each pass until the touch expires), restore areas, run rescue once; ledger combat_released names what was left to you";
            yield return $"prolonged: engaged longer than {CombatTimers.ProlongedTicks} ticks → one ledger combat_prolonged";
            yield return "never: dev tools, non-hostile targets, colony pawns or animals, pawns the director drafted/moved by hand, pawns marked unmanaged";
            if (g != null)
            {
                yield return g.rallyW > 0 ? $"rally rect: [{g.rallyX},{g.rallyZ},{g.rallyW},{g.rallyH}] on map {g.rallyMapId}" : "rally rect: none (steward.orders.rally {rect:[x,z,w,h]})";
                foreach (var kv in g.CombatStates)
                {
                    var st = kv.Value;
                    if (st.active) yield return $"map {kv.Key}: engaged since tick {st.engagedTick}: {st.drafted.Count} drafted, {st.prevArea.Count} restricted, {st.relieved.Count} relieved ({st.lastMode})";
                    else if (st.PendingRelease) yield return $"map {kv.Key}: release pending for {st.drafted.Count} drafted, {st.prevArea.Count} restricted (touched by hand)";
                    else if (st.mode == "watch") yield return $"map {kv.Key}: watching";
                }
            }
        }

        // ── detection ──

        /// <summary>Hostiles the order may act on: the colony's own pawns (faction or host faction = player) are excluded.</summary>
        public static List<Thing> Hostiles(Map map)
        {
            var list = new List<Thing>();
            foreach (var t in map.attackTargetsCache.TargetsHostileToColony)
            {
                var th = t.Thing;
                if (th == null || !th.Spawned || th.Destroyed) continue;
                if (t.ThreatDisabled(null)) continue;
                if (th.Position.Fogged(map)) continue;
                if (th is Pawn p)
                {
                    if (p.Downed || p.Dead) continue;
                    if (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer) continue;   // berserk colonist, rebelling slave, prison break, colony animal
                }
                list.Add(th);
            }
            return list;
        }

        /// <summary>Duties the assault lord toils hand out (LordToil_AssaultColony*/Sappers/Breaching/Bossgroup/Kidnap/Steal/AssaultThings/NestAssault).</summary>
        static readonly HashSet<string> AssaultDuties = new HashSet<string>(StringComparer.Ordinal)
        {
            "AssaultColony", "PrisonerAssaultColony", "Breaching", "Sapper", "Escort", "Kidnap", "Steal", "HuntEnemiesIndividual", "AssaultThing", "NestAssault",
        };

        static HostileFacts Facts(Thing th, IntVec3 rallyCenter, Area? home, bool colonistOutside)
        {
            var f = new HostileFacts
            {
                IsPawn = th is Pawn,
                InHome = home != null && home[th.Position],
                DistToRally = th.Position.DistanceTo(rallyCenter),
                ColonistOutside = colonistOutside,
            };
            if (th is Pawn p)
            {
                var ms = p.MentalStateDef;
                f.Manhunter = ms != null && (ms == MentalStateDefOf.Manhunter || ms == MentalStateDefOf.ManhunterPermanent || ms.defName.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0);
                var lord = p.GetLord();
                f.HasLord = lord != null;
                if (lord != null)
                {
                    f.Siege = lord.LordJob is LordJob_Siege;
                    // duty, not LordToil.AllowSatisfyLongNeeds: that flag is also false for mech-cluster guards (DefendPoint) and exit-map toils
                    var duty = p.mindState?.duty?.def;
                    if (duty != null && AssaultDuties.Contains(duty.defName)) f.Assaulting = true;
                }
            }
            return f;
        }

        // ── run ──

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            var st = g.Combat(map);
            var report = new OrderReport();
            var hostiles = Hostiles(map);

            var rect = RallyRect(map, g, out bool hasBase);
            var center = Center(rect);
            var home = map.areaManager.Home;
            bool homeUsable = home != null && home.TrueCount > 0;
            bool colonistOutside = !homeUsable || map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Dead && !home![p.Position]);

            var engaging = new List<Thing>();
            var watching = new List<(Thing thing, string label, float dist)>();
            foreach (var h in hostiles)
            {
                var f = Facts(h, center, homeUsable ? home : null, colonistOutside);
                if (ThreatRules.Engage(f)) engaging.Add(h);
                else watching.Add((h, ThreatRules.Label(f), f.DistToRally));
            }

            if (engaging.Count > 0)
            {
                st.lastHostileTick = tick;
                st.mode = "engaged";
                bool first = !st.active;
                if (first)
                {
                    st.active = true;
                    st.engagedTick = tick;
                    st.lastHoldTick = -1;
                    st.prolongedReported = false;
                    st.assigned.Clear();
                }
                bool hold = CombatTimers.ShouldHold(st.lastHoldTick, tick);
                if (first || hold)
                {
                    st.lastHoldTick = tick;
                    Deploy(map, g, st, engaging, rect, center, hasBase, first, report);
                }
                else
                {
                    foreach (var id in st.drafted) report.Act(id);
                    report.Summary = $"engaged: {engaging.Count} hostile(s), {st.drafted.Count} drafted ({st.lastMode}), {st.prevArea.Count} restricted to Home";
                }
                if (!st.prolongedReported && CombatTimers.IsProlonged(st.engagedTick, tick))
                {
                    st.prolongedReported = true;
                    StewardLedger.Orders("combat_prolonged", $"engaged for {tick - st.engagedTick} ticks, {engaging.Count} hostile(s) still engaged, {st.drafted.Count} drafted",
                        new JObject { ["ticks"] = tick - st.engagedTick, ["hostiles"] = engaging.Count, ["drafted"] = st.drafted.Count, ["watching"] = watching.Count });
                }
                if (watching.Count > 0) report.Summary += "; " + WatchText(watching);
                return report;
            }

            if (st.active)
            {
                if (!CombatTimers.ShouldRelease(st.lastHostileTick, tick))
                {
                    foreach (var id in st.drafted) report.Act(id);
                    int left = CombatTimers.ReleaseAfterHostileFreeTicks - (tick - st.lastHostileTick);
                    report.Summary = $"no engaged hostiles; releasing in {Math.Max(0, left)} ticks ({st.drafted.Count} drafted)";
                    if (watching.Count > 0) report.Summary += "; " + WatchText(watching);
                    return report;
                }
                Release(map, g, st, report, runRescue: true);
                if (watching.Count > 0) report.Summary += "; " + WatchText(watching);
                return report;
            }

            string pending = "";
            if (st.PendingRelease)
            {
                var (undrafted, restored, left) = ReleaseEntries(map, g, st, tick);
                foreach (var id in st.drafted) report.Act(id);
                foreach (var id in st.prevArea.Keys) report.Act(id);
                pending = $"release pending: {undrafted} undrafted, {restored} areas restored" + (left.Count > 0 ? "; left to you: " + string.Join(", ", left) : "");
            }

            if (watching.Count > 0)
            {
                st.mode = "watch";
                foreach (var w in watching) report.Act(w.thing.ThingID);
                report.Summary = WatchText(watching) + (pending.Length > 0 ? "; " + pending : "");
                return report;
            }
            st.mode = "idle";
            if (pending.Length > 0) { report.Summary = pending; return report; }
            return OrderReport.Idle("clear");
        }

        static string WatchText(List<(Thing thing, string label, float dist)> watching)
        {
            var groups = watching.GroupBy(w => w.label).Select(gr => $"{gr.Count()} {gr.Key} at {gr.Min(w => w.dist):0} cells").ToList();
            return $"watching: {watching.Count} hostile(s) not engaging ({string.Join(", ", groups)})";
        }

        // ── deploy / hold ──

        static DraftFlags Flags(Pawn p, Order order, CombatState st, List<Thing> hostiles, IntVec3 center)
        {
            bool near = hostiles.Any(h => h.Position.DistanceTo(p.Position) <= CombatTimers.NearHostileRadius || h.Position.DistanceTo(center) <= CombatTimers.NearHostileRadius);
            return new DraftFlags
            {
                Spawned = p.Spawned,
                Dead = p.Dead,
                Downed = p.Downed,
                Prisoner = p.IsPrisoner,
                Slave = p.IsSlaveOfColony,
                Juvenile = p.DevelopmentalStage.Juvenile(),
                IncapableOfViolence = p.WorkTagIsDisabled(WorkTags.Violent),
                InMentalState = p.InMentalState,
                HasDrafter = p.drafter != null,
                HasWeapon = p.equipment?.Primary != null,
                HealthPct = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Unmanaged = !ScorerGate.ManagedFlag(p),
                Touched = StandingOrders.IsTouched(p.ThingID, order.TouchScopes),
                Food = p.needs?.food?.CurLevelPercentage ?? 1f,
                Rest = p.needs?.rest?.CurLevelPercentage ?? 1f,
                Resting = st.relieved.Contains(p.ThingID),
                HostileNear = near,
            };
        }

        public CellRect RallyRect(Map map, StewardGame g) => RallyRect(map, g, out _);

        /// <summary>The rally rect; hasBase is false when there is neither a rally rect nor a Home area nor a colonist building (nowhere sensible to send anyone).</summary>
        public CellRect RallyRect(Map map, StewardGame g, out bool hasBase)
        {
            var r = g.RallyRect(map);
            if (r.HasValue) { hasBase = true; return r.Value.ClipInsideMap(map); }
            var home = map.areaManager.Home;
            hasBase = (home != null && home.TrueCount > 0) || map.listerBuildings.allBuildingsColonist.Count > 0;
            return CellRect.CenteredOn(ProductCounter.GetBaseCenter(map), DefaultRallyHalfSize).ClipInsideMap(map);
        }

        static IntVec3 Center(CellRect r) => new IntVec3(r.minX + r.Width / 2, 0, r.minZ + r.Height / 2);

        void Deploy(Map map, StewardGame g, CombatState st, List<Thing> hostiles, CellRect rect, IntVec3 center, bool hasBase, bool first, OrderReport report)
        {
            var home = map.areaManager.Home;
            bool homeUsable = home != null && home.TrueCount > 0;
            bool overrun = hostiles.Any(h => h.Position.DistanceTo(center) <= CombatTimers.OverrunRadius || (homeUsable && home![h.Position]));

            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            var fighters = new List<Pawn>();
            var nonFighters = new List<Pawn>();
            var byId = new Dictionary<string, Pawn>();
            var why = new Dictionary<string, string>();
            foreach (var p in colonists)
            {
                byId[p.ThingID] = p;
                var f = Flags(p, this, st, hostiles, center);
                string? reason = CombatEligibility.WhyNot(f);
                if (reason == null) { fighters.Add(p); st.relieved.Remove(p.ThingID); continue; }
                why[p.ThingID] = reason;
                if (reason == CombatEligibility.NeedsReason && overrun) { fighters.Add(p); continue; } // base breached: everybody fights, relief resumes after
                if (!f.Touched && !f.Unmanaged && !f.Downed && !f.Dead && p.Spawned && reason != CombatEligibility.NeedsReason) nonFighters.Add(p);
            }
            fighters.Sort((a, b) => string.CompareOrdinal(a.ThingID, b.ThingID));

            // bookkeeping for pawns we drafted earlier that are no longer fighters
            int relieved = 0;
            var relievedNames = new List<string>();
            foreach (var id in st.drafted.ToList())
            {
                if (fighters.Any(p => p.ThingID == id)) continue;
                if (!byId.TryGetValue(id, out var p) || !p.Drafted) { st.drafted.Remove(id); st.assigned.Remove(id); continue; }   // gone, or a job/the game undrafted it
                if (why.TryGetValue(id, out var r) && r == CombatEligibility.NeedsReason)
                {
                    if (p.drafter != null) p.drafter.Drafted = false;
                    st.drafted.Remove(id); st.assigned.Remove(id); st.relieved.Add(id);
                    relieved++; relievedNames.Add(p.LabelShort);
                }
                // touched / unmanaged pawns still drafted stay recorded: release decides by their touch reason
            }

            int drafted = 0, moved = 0, attacking = 0;
            foreach (var p in fighters)
            {
                if (!p.Drafted)
                {
                    p.drafter!.Drafted = true;
                    st.drafted.Add(p.ThingID);   // only pawns whose draft state the order changed
                    drafted++;
                }
                report.Act(p.ThingID);
            }

            if (overrun)
            {
                st.lastMode = "attacking";
                foreach (var p in fighters)
                {
                    var target = NearestHostile(p, hostiles);
                    if (target == null) continue;
                    if (IsAttacking(p, target)) continue;
                    bool melee = p.equipment?.Primary == null || p.equipment.Primary.def.IsMeleeWeapon;
                    Job job = melee ? JobMaker.MakeJob(JobDefOf.AttackMelee, target) : JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                    if (!melee) job.endIfCantShootTargetFromCurPos = false;
                    if (target is Pawn tp) p.mindState.enemyTarget = tp;
                    if (p.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder)) attacking++;
                }
            }
            else if (!hasBase)
            {
                st.lastMode = "holding in place (no base on this map)";
            }
            else
            {
                st.lastMode = "holding rally";
                AssignCells(map, st, rect, center, fighters);
                foreach (var p in fighters)
                {
                    if (!st.assigned.TryGetValue(p.ThingID, out var cell)) continue;
                    if (p.Position == cell) continue;
                    if (p.CurJobDef == JobDefOf.Goto && p.CurJob.targetA.Cell == cell) continue;
                    if (!p.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) continue;
                    var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    if (p.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder)) moved++;
                }
            }

            int restricted = 0;
            if (homeUsable)
            {
                foreach (var p in nonFighters)
                {
                    if (p.playerSettings == null) continue;
                    var cur = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                    if (cur == home) continue;
                    if (!st.prevArea.ContainsKey(p.ThingID)) st.prevArea[p.ThingID] = cur?.Label ?? "";
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = home;
                    restricted++;
                }
            }

            if (first)
            {
                StewardLedger.Orders("combat_engaged", $"{hostiles.Count} hostiles, {fighters.Count} drafted",
                    new JObject { ["hostiles"] = hostiles.Count, ["drafted"] = fighters.Count, ["overrun"] = overrun, ["rally"] = RallyJson(rect), ["map"] = map.uniqueID });
            }
            string mode = overrun ? $"overrun: {attacking} attacking" : hasBase ? $"holding rally: {moved} moved" : "holding in place (no base on this map)";
            report.Summary = $"engaged: {hostiles.Count} hostile(s), {fighters.Count} fighting (+{drafted} drafted now), {mode}, {st.prevArea.Count} restricted to Home"
                + (restricted > 0 ? $" (+{restricted})" : "")
                + (relieved > 0 ? $"; relieved to eat/sleep: {string.Join(", ", relievedNames)}" : "")
                + (st.relieved.Count > 0 && relieved == 0 ? $"; {st.relieved.Count} resting" : "");
        }

        static JArray RallyJson(CellRect r) => new JArray(r.minX, r.minZ, r.Width, r.Height);

        static Thing? NearestHostile(Pawn p, List<Thing> hostiles)
        {
            Thing? best = null; float bd = float.MaxValue;
            foreach (var h in hostiles)
            {
                float d = p.Position.DistanceToSquared(h.Position);
                if (d < bd) { bd = d; best = h; }
            }
            return best;
        }

        static bool IsAttacking(Pawn p, Thing target)
        {
            var job = p.CurJob;
            if (job == null) return false;
            if (job.def != JobDefOf.AttackMelee && job.def != JobDefOf.AttackStatic) return false;
            return job.targetA.Thing == target;
        }

        static void AssignCells(Map map, CombatState st, CellRect rect, IntVec3 center, List<Pawn> fighters)
        {
            var assigned = st.assigned;
            // drop assignments for pawns no longer fighting or cells outside the current rect
            var live = new HashSet<string>(fighters.Select(p => p.ThingID));
            foreach (var id in assigned.Keys.ToList())
                if (!live.Contains(id) || !rect.Contains(assigned[id]) || !assigned[id].Standable(map)) assigned.Remove(id);
            var need = fighters.Where(p => !assigned.ContainsKey(p.ThingID)).ToList();
            if (need.Count == 0) return;
            var taken = new HashSet<IntVec3>(assigned.Values);
            var candidates = new List<RallyCandidate>();
            foreach (var c in rect)
            {
                if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map) || taken.Contains(c)) continue;
                if (c.GetEdifice(map) is Building_Door) continue;
                candidates.Add(new RallyCandidate(c.x, c.z, CoverUtility.TotalSurroundingCoverScore(c, map)));
            }
            var picks = RallyLogic.Spread(candidates, need.Count, center.x, center.z);
            int i = 0;
            foreach (var p in need)
            {
                if (i >= picks.Count) break;
                assigned[p.ThingID] = new IntVec3(picks[i].X, 0, picks[i].Z);
                i++;
            }
        }

        // ── release ──

        /// <summary>What one release did: counts plus a description of every pawn left to the director.</summary>
        public struct ReleaseResult
        {
            public int Undrafted, Restored;
            public List<string> Left;
        }

        /// <summary>Releases this map now (used when the order is switched off mid-fight); null when not engaged there.</summary>
        public ReleaseResult? ReleaseNow(Map map)
        {
            var g = StewardGame.Current;
            if (g == null || map == null) return null;
            var st = g.CombatOrNull(map);
            if (st == null || !st.active) return null;
            var report = new OrderReport();
            var result = Release(map, g, st, report, runRescue: StandingOrders.Get<Order_Rescue>()?.Enabled ?? false);
            g.RecordOrderRun(Id, Find.TickManager.TicksGame, report);
            return result;
        }

        /// <summary>
        /// Undrafts / restores every entry it may (touched pawns are kept for a later pass) and returns the counts plus a
        /// description of what was left to the director.
        /// </summary>
        (int undrafted, int restored, List<string> left) ReleaseEntries(Map map, StewardGame g, CombatState st, int tick)
        {
            int undrafted = 0, restored = 0;
            var left = new List<string>();
            var home = map.areaManager.Home;
            foreach (var id in st.drafted.ToList())
            {
                var p = FindPawn(map, id);
                if (p == null) { st.drafted.Remove(id); st.assigned.Remove(id); continue; }   // left the map or died
                if (!p.Drafted) { st.drafted.Remove(id); st.assigned.Remove(id); continue; }
                var reasons = g.touches.ReasonsFor(id, tick);
                if (CombatScopes.KeepDrafted(reasons.Select(r => r.reason)))
                {
                    var r0 = reasons.First(r => CombatScopes.KeepDrafted(new[] { r.reason }));
                    left.Add($"{p.LabelShort} (drafted, {r0.reason}, {r0.ticksLeft / 2500f:0.0} h)");
                    continue;
                }
                if (p.drafter != null) { p.drafter.Drafted = false; undrafted++; }
                st.drafted.Remove(id); st.assigned.Remove(id);
            }
            foreach (var kv in st.prevArea.ToList())
            {
                var p = FindPawn(map, kv.Key);
                if (p?.playerSettings == null) { st.prevArea.Remove(kv.Key); continue; }
                var cur = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                bool stillHome = home != null && cur == home;
                var reasons = g.touches.ReasonsFor(kv.Key, tick);
                switch (CombatScopes.AreaAction(reasons.Select(r => r.reason), stillHome))
                {
                    case AreaRelease.Drop:
                        st.prevArea.Remove(kv.Key);   // the director chose the area meanwhile: theirs
                        break;
                    case AreaRelease.Keep:
                        left.Add($"{p.LabelShort} (area Home, {reasons.FirstOrDefault().reason ?? "touched"})");
                        break;
                    default:
                        Area? prev = string.IsNullOrEmpty(kv.Value) ? null : map.areaManager.GetLabeled(kv.Value);
                        p.playerSettings.AreaRestrictionInPawnCurrentMap = prev;
                        st.prevArea.Remove(kv.Key);
                        restored++;
                        break;
                }
            }
            return (undrafted, restored, left);
        }

        ReleaseResult Release(Map map, StewardGame g, CombatState st, OrderReport report, bool runRescue)
        {
            int tick = Find.TickManager.TicksGame;
            var (undrafted, restored, left) = ReleaseEntries(map, g, st, tick);
            int held = st.engagedTick >= 0 ? tick - st.engagedTick : 0;
            st.active = false;
            st.mode = "idle";
            st.engagedTick = -1;
            st.lastHoldTick = -1;
            st.prolongedReported = false;
            st.assigned.Clear();
            st.relieved.Clear();
            st.lastMode = "";
            foreach (var id in st.drafted) report.Act(id);
            foreach (var id in st.prevArea.Keys) report.Act(id);
            string leftText = left.Count > 0 ? "; left to you: " + string.Join(", ", left) : "";
            StewardLedger.Orders("combat_released", $"{undrafted} undrafted, {restored} areas restored after {held} ticks{leftText}",
                new JObject { ["undrafted"] = undrafted, ["restored"] = restored, ["ticks"] = held, ["map"] = map.uniqueID, ["left"] = new JArray(left) });
            string rescue = "";
            var r = runRescue ? StandingOrders.Get<Order_Rescue>() : null;
            if (r != null)
            {
                var rr = StandingOrders.RunNow(r, map);
                rescue = "; rescue: " + rr.Summary;
            }
            report.Summary = $"released: {undrafted} undrafted, {restored} areas restored{leftText}{rescue}";
            return new ReleaseResult { Undrafted = undrafted, Restored = restored, Left = left };
        }

        static Pawn? FindPawn(Map map, string id)
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned) if (p.ThingID == id) return p;
            return null;
        }
    }
}
