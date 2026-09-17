using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimBridge.Ui
{
    /// <summary>Player-parity controls: everything the player can click, the agent can call.</summary>
    public static class UiRpc
    {
        static Verse.Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }

        // ---------- Gizmos ----------

        [Rpc("ui.gizmos", "{thing} buttons shown for a selected thing/pawn: [{i, label, desc, type, disabled, reason, active?}]")]
        public static JToken Gizmos(JObject p)
        {
            Map();
            var t = Lookup.ThingOrNull(P.Str(p, "thing")) ?? Lookup.Pawn(P.Str(p, "thing"));
            var arr = new JArray();
            int i = 0;
            foreach (var g in SafeGizmos(t))
            {
                var o = new JObject { ["i"] = i++, ["type"] = g.GetType().Name, ["disabled"] = g.Disabled };
                if (g.Disabled && !string.IsNullOrEmpty(g.disabledReason)) o["reason"] = g.disabledReason.StripTags();
                if (g is Command c)
                {
                    o["label"] = (c.Label ?? "").StripTags();
                    if (!string.IsNullOrEmpty(c.Desc)) o["desc"] = c.Desc.StripTags().Truncate(300);
                    if (c is Command_Toggle ct) { try { o["active"] = ct.isActive(); } catch { } }
                    if (c is Command_Target || c is Command_VerbTarget) o["needs_target"] = true;
                    if (c is Designator d) o["designator"] = d.GetType().Name;
                }
                else o["label"] = g.GetType().Name;
                arr.Add(o);
            }
            return arr;
        }

        static List<Gizmo> SafeGizmos(Thing t)
        {
            var list = new List<Gizmo>();
            try { foreach (var g in t.GetGizmos()) if (g != null) list.Add(g); }
            catch (Exception ex) { BridgeLog.Warning("GetGizmos: " + ex.Message); }
            return list;
        }

        [Rpc("ui.press", "{thing, label|i, target?: [x,z]|thingId} press a gizmo by label (substring, case-insensitive) or index; targeted gizmos need target")]
        public static JToken Press(JObject p)
        {
            Map();
            var t = Lookup.ThingOrNull(P.Str(p, "thing")) ?? Lookup.Pawn(P.Str(p, "thing"));
            var gizmos = SafeGizmos(t);
            Gizmo? g = null;
            if (p["i"] != null) { int i = P.Int(p, "i"); if (i < 0 || i >= gizmos.Count) throw new RpcError("gizmo index out of range"); g = gizmos[i]; }
            else
            {
                string label = P.Str(p, "label");
                g = gizmos.FirstOrDefault(x => x is Command c && string.Equals(c.Label?.StripTags(), label, StringComparison.OrdinalIgnoreCase))
                    ?? gizmos.FirstOrDefault(x => x is Command c && (c.Label?.StripTags().IndexOf(label, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
                if (g == null) throw new RpcError($"no gizmo matching '{label}'. Available: " + string.Join(" | ", gizmos.OfType<Command>().Select(c => c.Label?.StripTags())));
            }
            if (g.Disabled) throw new RpcError($"gizmo is disabled: {g.disabledReason?.StripTags()}");
            // beds: any gizmo (the beds order honours ui.press); pawns: only the draft/undraft gizmo pauses the combat order (Hold fire etc. do not)
            string pressLabel = (g as Command)?.Label?.StripTags() ?? g.GetType().Name;
            if (t is Building_Bed) Hooks.RaiseManualTouch(t, "ui.press:" + pressLabel);
            else if (t is Pawn && pressLabel.IndexOf("draft", StringComparison.OrdinalIgnoreCase) >= 0) Hooks.RaiseManualTouch(t, "ui.press:draft");
            switch (g)
            {
                case Command_Toggle ct: ct.toggleAction(); return new JObject { ["pressed"] = ct.Label?.StripTags(), ["active"] = ct.isActive() };
                case Command_Action ca: ca.action(); return new JObject { ["pressed"] = ca.Label?.StripTags() };
                case Command_Target ctg:
                {
                    var target = (LocalTargetInfo)Coerce.To(p["target"] ?? throw new RpcError("this gizmo needs a target"), typeof(LocalTargetInfo))!;
                    if (ctg.targetingParams != null && !ctg.targetingParams.CanTarget(target.ToTargetInfo(Find.CurrentMap))) throw new RpcError("target not valid for this command");
                    ctg.action(target);
                    return new JObject { ["pressed"] = ctg.Label?.StripTags(), ["target"] = Render.Value(target, 0) };
                }
                case Command_VerbTarget cv:
                {
                    var target = (LocalTargetInfo)Coerce.To(p["target"] ?? throw new RpcError("this gizmo needs a target"), typeof(LocalTargetInfo))!;
                    var verb = cv.verb;
                    if (verb.CasterPawn != null)
                    {
                        verb.CasterPawn.mindState.enemyTarget = target.Thing;
                        var job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                        job.verbToUse = verb;
                        verb.CasterPawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                        return new JObject { ["pressed"] = cv.Label?.StripTags(), ["target"] = Render.Value(target, 0) };
                    }
                    // turrets etc.
                    verb.TryStartCastOn(target);
                    return new JObject { ["pressed"] = cv.Label?.StripTags() };
                }
                case Command_Ability cab:
                {
                    var ability = cab.Ability;
                    if (p["target"] == null && ability.def.targetRequired) throw new RpcError("this ability needs a target");
                    var target = p["target"] != null ? (LocalTargetInfo)Coerce.To(p["target"], typeof(LocalTargetInfo))! : new LocalTargetInfo(ability.pawn);
                    ability.QueueCastingJob(target, LocalTargetInfo.Invalid);
                    return new JObject { ["pressed"] = cab.Label?.StripTags(), ["target"] = Render.Value(target, 0) };
                }
                case Designator d:
                {
                    if (p["target"] == null) throw new RpcError("designator gizmos need a target cell/thing; or use ui.designate");
                    var target = (LocalTargetInfo)Coerce.To(p["target"], typeof(LocalTargetInfo))!;
                    if (target.HasThing) { var r = d.CanDesignateThing(target.Thing); if (!r.Accepted) throw new RpcError("cannot designate: " + r.Reason); d.DesignateThing(target.Thing); }
                    else { var r = d.CanDesignateCell(target.Cell); if (!r.Accepted) throw new RpcError("cannot designate: " + r.Reason); d.DesignateSingleCell(target.Cell); }
                    return new JObject { ["pressed"] = d.Label?.StripTags() };
                }
                default:
                    try { g.ProcessInput(new Event()); return new JObject { ["pressed"] = g.GetType().Name, ["note"] = "generic ProcessInput" }; }
                    catch (Exception ex) { throw new RpcError($"cannot press {g.GetType().Name}: {ex.Message}"); }
            }
        }

        // ---------- Float menu (right-click orders) ----------

        [Rpc("ui.orders_at", "{pawn, at: [x,z]|thingId} the right-click orders this pawn would get at that cell/thing: [{label, disabled, priority}]")]
        public static JToken OrdersAt(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            var target = (LocalTargetInfo)Coerce.To(p["at"], typeof(LocalTargetInfo))!;
            var opts = GetOptions(pawn, target);
            return new JArray(opts.Select((o, i) => new JObject { ["i"] = i, ["label"] = o.Label.StripTags(), ["disabled"] = o.Disabled, ["priority"] = o.Priority.ToString() }));
        }

        static List<FloatMenuOption> GetOptions(Pawn pawn, LocalTargetInfo target)
        {
            if (!pawn.Spawned || pawn.Map != Find.CurrentMap) throw new RpcError("pawn is not on the current map");
            Vector3 click = target.HasThing ? target.Thing.TrueCenter() : target.Cell.ToVector3Shifted();
            var opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, click, out _);
            return opts ?? new List<FloatMenuOption>();
        }

        [Rpc("ui.order", "{pawn, at: [x,z]|thingId, label|i} execute one of the right-click orders (label substring match)")]
        public static JToken Order(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            var target = (LocalTargetInfo)Coerce.To(p["at"], typeof(LocalTargetInfo))!;
            var opts = GetOptions(pawn, target);
            FloatMenuOption? o;
            if (p["i"] != null) { int i = P.Int(p, "i"); if (i < 0 || i >= opts.Count) throw new RpcError("order index out of range"); o = opts[i]; }
            else
            {
                string label = P.Str(p, "label");
                o = opts.FirstOrDefault(x => string.Equals(x.Label.StripTags(), label, StringComparison.OrdinalIgnoreCase))
                    ?? opts.FirstOrDefault(x => x.Label.StripTags().IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
                if (o == null) throw new RpcError($"no order matching '{label}'. Available: " + string.Join(" | ", opts.Select(x => x.Label.StripTags() + (x.Disabled ? " (disabled)" : ""))));
            }
            if (o.Disabled) throw new RpcError($"order is disabled: {o.Label.StripTags()}");
            if (o.action == null) throw new RpcError("order has no action");
            Hooks.RaiseManualTouch(pawn, "ui.order:" + o.Label.StripTags());
            if (pawn.Drafted) Hooks.RaiseManualTouch(pawn, "ui.order.drafted:" + o.Label.StripTags());   // a drafted attack/move order pauses the combat order for this pawn
            if (target.Thing is Pawn tpawn) Hooks.RaiseManualTouch(tpawn, "ui.order:" + o.Label.StripTags());
            o.action();
            return new JObject { ["ordered"] = o.Label.StripTags(), ["pawn"] = pawn.LabelShort, ["job"] = State.Snapshot.JobText(pawn) };
        }

        // ---------- Direct pawn control ----------

        [Rpc("ui.draft", "{pawn, drafted: bool}")]
        public static JToken Draft(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            if (pawn.drafter == null) throw new RpcError("pawn cannot be drafted");
            if (pawn.Downed) throw new RpcError("pawn is downed");
            pawn.drafter.Drafted = P.Bool(p, "drafted", true);
            Hooks.RaiseManualTouch(pawn, "ui.draft");
            return new JObject { ["pawn"] = pawn.LabelShort, ["drafted"] = pawn.Drafted };
        }

        [Rpc("ui.goto", "{pawn, cell: [x,z], draft?: true} move a pawn to a cell (drafts first unless draft=false, in which case it's an undrafted goto)")]
        public static JToken Goto(JObject p)
        {
            var map = Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            var cell = Lookup.Cell(p["cell"]);
            if (!cell.InBounds(map) || !cell.Standable(map)) { var alt = RCellFinder.BestOrderedGotoDestNear(cell, pawn); if (!alt.IsValid) throw new RpcError("cell not standable and no nearby alternative"); cell = alt; }
            if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) throw new RpcError("pawn cannot reach that cell");
            if (P.Bool(p, "draft", true) && pawn.drafter != null && !pawn.Drafted) pawn.drafter.Drafted = true;
            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;
            bool ok = pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            Hooks.RaiseManualTouch(pawn, "ui.goto");
            return new JObject { ["ok"] = ok, ["pawn"] = pawn.LabelShort, ["cell"] = State.Snapshot.Cell(cell), ["drafted"] = pawn.Drafted };
        }

        [Rpc("ui.attack", "{pawn, target: thingId, melee?: bool} drafted attack order")]
        public static JToken Attack(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            var target = Lookup.ThingOrNull(P.Str(p, "target")) ?? Lookup.Pawn(P.Str(p, "target"));
            if (pawn.drafter != null && !pawn.Drafted) pawn.drafter.Drafted = true;
            bool melee = P.Bool(p, "melee", pawn.equipment?.Primary == null || pawn.equipment.Primary.def.IsMeleeWeapon);
            Job job;
            if (melee) job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            else { job = JobMaker.MakeJob(JobDefOf.AttackStatic, target); job.endIfCantShootTargetFromCurPos = false; }
            job.playerForced = true;
            if (target is Pawn tp) pawn.mindState.enemyTarget = tp;
            bool ok = pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            Hooks.RaiseManualTouch(pawn, "ui.attack");
            return new JObject { ["ok"] = ok, ["pawn"] = pawn.LabelShort, ["target"] = target.ThingID, ["melee"] = melee };
        }

        [Rpc("ui.job", "{pawn, job: JobDef, target?: [x,z]|thingId, target_b?, target_c?, count?, queue?: false} give a pawn a specific job directly (e.g. Ingest, Equip, Wear, TakeInventory, HaulToCell, Rescue, TendPatient, LayDown, Research). Prefer ui.order when possible.")]
        public static JToken JobRpc(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            var def = Lookup.Def<JobDef>(P.Str(p, "job"));
            var job = JobMaker.MakeJob(def);
            if (p["target"] != null) job.targetA = (LocalTargetInfo)Coerce.To(p["target"], typeof(LocalTargetInfo))!;
            if (p["target_b"] != null) job.targetB = (LocalTargetInfo)Coerce.To(p["target_b"], typeof(LocalTargetInfo))!;
            if (p["target_c"] != null) job.targetC = (LocalTargetInfo)Coerce.To(p["target_c"], typeof(LocalTargetInfo))!;
            if (p["count"] != null) job.count = P.Int(p, "count");
            job.playerForced = true;
            bool wasDrafted = pawn.Drafted;
            bool ok = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, P.Bool(p, "queue", false));
            if (def == JobDefOf.Rescue || def == JobDefOf.TendPatient)
            {
                Hooks.RaiseManualTouch(pawn, "ui.job:" + def.defName);
                if (job.targetA.Thing is Pawn patient) Hooks.RaiseManualTouch(patient, "ui.job:" + def.defName);
            }
            else if (wasDrafted || def == JobDefOf.AttackMelee || def == JobDefOf.AttackStatic || def == JobDefOf.Goto)
            {
                // a drafted pawn's job, or an attack/move job, is manual military control: the combat order leaves the pawn alone for an hour
                Hooks.RaiseManualTouch(pawn, "ui.job:" + def.defName);
            }
            return new JObject { ["ok"] = ok, ["pawn"] = pawn.LabelShort, ["job"] = def.defName, ["now"] = State.Snapshot.JobText(pawn) };
        }

        [Rpc("ui.cancel_job", "{pawn} interrupt the pawn's current job (and undraft if drafted=false given)")]
        public static JToken CancelJob(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            pawn.jobs.ClearQueuedJobs();
            return new JObject { ["pawn"] = pawn.LabelShort, ["job"] = State.Snapshot.JobText(pawn) };
        }

        // ---------- Work / schedule / policies ----------

        [Rpc("ui.set_work", "{pawn, priorities: {WorkTypeDef: 0-4}} 0 = disabled, 1 = highest; enables manual priorities mode. Takes the pawn out of steward management first (steward.pawn managed=true hands it back)")]
        public static JToken SetWork(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            if (pawn.workSettings == null || !pawn.workSettings.EverWork) throw new RpcError("pawn cannot work");
            // Validate everything first: a failed call must leave the managed flag and the priorities untouched.
            var pri = P.Obj(p, "priorities") ?? throw new RpcError("missing priorities");
            var resolved = new List<(string key, WorkTypeDef wt, int v)>();
            foreach (var kv in pri)
            {
                var wt = Lookup.DefOrNull(typeof(WorkTypeDef), kv.Key) as WorkTypeDef ?? throw new RpcError($"unknown work type '{kv.Key}'. Known: " + string.Join(", ", DefDatabase<WorkTypeDef>.AllDefs.Select(w => w.defName)));
                int raw;
                try { raw = (int)kv.Value!; } catch { throw new RpcError($"priorities.{kv.Key}: value must be an integer 0-4"); }
                resolved.Add((kv.Key, wt, Math.Max(0, Math.Min(4, raw))));
            }
            // The steward's scorer would clobber manual priorities on its next pass: mark the pawn unmanaged before writing.
            Hooks.RaisePawnWorkSetManually(pawn);
            Current.Game.playSettings.useWorkPriorities = true;
            var applied = new JObject(); var skipped = new JArray();
            foreach (var (key, wt, v) in resolved)
            {
                if (pawn.WorkTypeIsDisabled(wt)) { skipped.Add(key + " (disabled for this pawn)"); continue; }
                pawn.workSettings.SetPriority(wt, v);
                applied[wt.defName] = v;
            }
            return new JObject
            {
                ["pawn"] = pawn.LabelShort, ["applied"] = applied, ["skipped"] = skipped,
                ["steward_managed"] = false,
                ["note"] = "steward no longer sets this pawn's priorities or drafts it in combat (the combat order skips unmanaged pawns: draft or shelter it yourself in a raid); steward.pawn managed=true to hand back",
            };
        }

        [Rpc("ui.set_schedule", "{pawn, hours: 24-char string using A=Anything S=Sleep W=Work J=Joy M=Meditate (hour 0 first)}")]
        public static JToken SetSchedule(JObject p)
        {
            Map();
            var pawn = Lookup.Colonist(P.Str(p, "pawn"));
            if (pawn.timetable == null) throw new RpcError("pawn has no timetable");
            string hours = P.Str(p, "hours").ToUpperInvariant().Replace(" ", "");
            if (hours.Length != 24) throw new RpcError("hours must be exactly 24 characters");
            for (int h = 0; h < 24; h++)
            {
                var def = hours[h] switch { 'A' => TimeAssignmentDefOf.Anything, 'S' => TimeAssignmentDefOf.Sleep, 'W' => TimeAssignmentDefOf.Work, 'J' => TimeAssignmentDefOf.Joy, 'M' => TimeAssignmentDefOf.Meditate, _ => throw new RpcError($"bad hour char '{hours[h]}'") };
                pawn.timetable.SetAssignment(h, def);
            }
            return new JObject { ["pawn"] = pawn.LabelShort, ["schedule"] = hours };
        }

        [Rpc("ui.set_policies", "{pawn, apparel?, food?, drug?, reading?, area?: label|Unrestricted, medical?: NoCare|NoMeds|HerbalOrWorse|NormalOrWorse|Best, hostility?: Flee|Attack|Ignore, self_tend?: bool}")]
        public static JToken SetPolicies(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            var o = new JObject { ["pawn"] = pawn.LabelShort };
            if (p["apparel"] != null) { var pol = Current.Game.outfitDatabase.AllOutfits.FirstOrDefault(x => x.label.Equals(P.Str(p, "apparel"), StringComparison.OrdinalIgnoreCase)) ?? throw new RpcError("unknown apparel policy"); pawn.outfits.CurrentApparelPolicy = pol; o["apparel"] = pol.label; }
            if (p["food"] != null) { var pol = Current.Game.foodRestrictionDatabase.AllFoodRestrictions.FirstOrDefault(x => x.label.Equals(P.Str(p, "food"), StringComparison.OrdinalIgnoreCase)) ?? throw new RpcError("unknown food policy"); pawn.foodRestriction.CurrentFoodPolicy = pol; o["food"] = pol.label; }
            if (p["drug"] != null) { var pol = Current.Game.drugPolicyDatabase.AllPolicies.FirstOrDefault(x => x.label.Equals(P.Str(p, "drug"), StringComparison.OrdinalIgnoreCase)) ?? throw new RpcError("unknown drug policy"); pawn.drugs.CurrentPolicy = pol; o["drug"] = pol.label; }
            if (p["reading"] != null && pawn.reading != null) { var pol = Current.Game.readingPolicyDatabase.AllReadingPolicies.FirstOrDefault(x => x.label.Equals(P.Str(p, "reading"), StringComparison.OrdinalIgnoreCase)) ?? throw new RpcError("unknown reading policy"); pawn.reading.CurrentPolicy = pol; o["reading"] = pol.label; }
            if (p["area"] != null && pawn.playerSettings != null)
            {
                string a = P.Str(p, "area");
                if (a.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase) || a.Equals("none", StringComparison.OrdinalIgnoreCase)) pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                else pawn.playerSettings.AreaRestrictionInPawnCurrentMap = Lookup.AreaOrNull(a) ?? throw new RpcError("unknown area");
                o["area"] = pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label ?? "Unrestricted";
            }
            if (p["medical"] != null && pawn.playerSettings != null) { pawn.playerSettings.medCare = (MedicalCareCategory)Enum.Parse(typeof(MedicalCareCategory), P.Str(p, "medical"), true); o["medical"] = pawn.playerSettings.medCare.ToString(); }
            if (p["hostility"] != null && pawn.playerSettings != null) { pawn.playerSettings.hostilityResponse = (HostilityResponseMode)Enum.Parse(typeof(HostilityResponseMode), P.Str(p, "hostility"), true); o["hostility"] = pawn.playerSettings.hostilityResponse.ToString(); }
            if (p["self_tend"] != null && pawn.playerSettings != null) { pawn.playerSettings.selfTend = P.Bool(p, "self_tend", true); o["self_tend"] = pawn.playerSettings.selfTend; }
            foreach (var key in new[] { "apparel", "food", "drug", "reading", "area", "medical", "hostility", "self_tend" })
                if (p[key] != null) Hooks.RaiseManualTouch(pawn, "ui.set_policies:" + key);
            return o;
        }

        [Rpc("ui.set_research", "{def} set the current research project")]
        public static JToken SetResearch(JObject p)
        {
            Map();
            var def = Lookup.Def<ResearchProjectDef>(P.Str(p, "def"));
            if (def.IsFinished) throw new RpcError("already finished");
            if (!def.CanStartNow) throw new RpcError("cannot start now: prerequisites or research bench/tech requirements unmet (" + string.Join(", ", def.prerequisites?.Where(x => !x.IsFinished).Select(x => x.defName) ?? Enumerable.Empty<string>()) + ")");
            Find.ResearchManager.SetCurrentProject(def);
            return new JObject { ["current"] = def.defName };
        }

        // ---------- Bills ----------

        [Rpc("ui.add_bill", "{thing: work table id (aliases: station/table/bench), recipe: RecipeDef, mode?: RepeatCount|TargetCount|Forever, count?: 1, radius?: 999, suspended?: false, first?: false}")]
        public static JToken AddBill(JObject p)
        {
            Map();
            // "thing" is the documented name, but models often guess "station"/"table"/"bench" for a workbench id.
            string thingId = p["thing"] != null ? P.Str(p, "thing")
                : p["station"] != null ? P.Str(p, "station")
                : p["table"] != null ? P.Str(p, "table")
                : p["bench"] != null ? P.Str(p, "bench")
                : throw new RpcError("missing param 'thing' (the work table id; aliases station/table/bench also accepted)");
            var t = Lookup.Thing(thingId);
            if (!(t is IBillGiver bg)) throw new RpcError($"{t.ThingID} has no bill stack");
            var recipe = Lookup.Def<RecipeDef>(P.Str(p, "recipe"));
            if (!t.def.AllRecipes.Contains(recipe)) throw new RpcError($"{t.def.defName} cannot do {recipe.defName}. Available: " + string.Join(", ", t.def.AllRecipes.Where(r => r.AvailableNow).Select(r => r.defName)));
            if (!recipe.AvailableNow) throw new RpcError("recipe not available now (research?)");
            var bill = recipe.MakeNewBill();
            if (bill is Bill_Production bp)
            {
                string mode = P.Str(p, "mode", "RepeatCount");
                bp.repeatMode = mode switch { "RepeatCount" => BillRepeatModeDefOf.RepeatCount, "TargetCount" => BillRepeatModeDefOf.TargetCount, "Forever" => BillRepeatModeDefOf.Forever, _ => throw new RpcError("mode must be RepeatCount|TargetCount|Forever") };
                int count = P.Int(p, "count", 1);
                if (bp.repeatMode == BillRepeatModeDefOf.TargetCount) bp.targetCount = count; else bp.repeatCount = count;
            }
            bill.ingredientSearchRadius = P.Float(p, "radius", 999f);
            bill.suspended = P.Bool(p, "suspended", false);
            bg.BillStack.AddBill(bill);
            if (P.Bool(p, "first", false)) bg.BillStack.Reorder(bill, -bg.BillStack.Count);
            return new JObject { ["id"] = bill.GetUniqueLoadID(), ["label"] = bill.LabelCap, ["table"] = t.ThingID };
        }

        [Rpc("ui.bill", "{thing, id|index, action: delete|suspend|resume|top|set, count?, mode?, radius?} modify a bill")]
        public static JToken BillEdit(JObject p)
        {
            Map();
            var t = Lookup.Thing(P.Str(p, "thing"));
            if (!(t is IBillGiver bg)) throw new RpcError($"{t.ThingID} has no bill stack");
            RimWorld.Bill? bill = null;
            if (p["id"] != null) bill = bg.BillStack.Bills.FirstOrDefault(b => b.GetUniqueLoadID() == P.Str(p, "id"));
            else if (p["index"] != null) bill = bg.BillStack.Bills.ElementAtOrDefault(P.Int(p, "index"));
            if (bill == null) throw new RpcError("bill not found");
            string action = P.Str(p, "action");
            switch (action)
            {
                case "delete": bg.BillStack.Delete(bill); break;
                case "suspend": bill.suspended = true; break;
                case "resume": bill.suspended = false; break;
                case "top": bg.BillStack.Reorder(bill, -bg.BillStack.Count); break;
                case "set":
                    if (bill is Bill_Production bp)
                    {
                        if (p["mode"] != null) bp.repeatMode = P.Str(p, "mode") switch { "RepeatCount" => BillRepeatModeDefOf.RepeatCount, "TargetCount" => BillRepeatModeDefOf.TargetCount, "Forever" => BillRepeatModeDefOf.Forever, _ => throw new RpcError("bad mode") };
                        if (p["count"] != null) { if (bp.repeatMode == BillRepeatModeDefOf.TargetCount) bp.targetCount = P.Int(p, "count"); else bp.repeatCount = P.Int(p, "count"); }
                    }
                    if (p["radius"] != null) bill.ingredientSearchRadius = P.Float(p, "radius");
                    break;
                default: throw new RpcError("action must be delete|suspend|resume|top|set");
            }
            return new JObject { ["ok"] = true, ["bills"] = bg.BillStack.Count };
        }

        // ---------- Storage ----------

        [Rpc("ui.storage", "{zone?: label, thing?: shelf id, priority?: Low|Normal|Preferred|Important|Critical, allow_all?: bool, disallow_all?: bool, allow?: [ThingDef|ThingCategoryDef...], disallow?: [...]} edit storage settings")]
        public static JToken Storage(JObject p)
        {
            Map();
            StorageSettings settings;
            string what;
            if (p["zone"] != null) { var z = Lookup.ZoneOrNull(P.Str(p, "zone")) as Zone_Stockpile ?? throw new RpcError("no stockpile zone with that label"); settings = z.settings; what = z.label; }
            else { var t = Lookup.Thing(P.Str(p, "thing")); if (!(t is IStoreSettingsParent sp)) throw new RpcError("thing has no storage settings"); settings = sp.GetStoreSettings(); what = t.ThingID; }
            if (p["priority"] != null) settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), P.Str(p, "priority"), true);
            if (P.Bool(p, "disallow_all", false)) settings.filter.SetDisallowAll();
            if (P.Bool(p, "allow_all", false)) settings.filter.SetAllowAll(settings.owner?.GetParentStoreSettings()?.filter ?? settings.filter, false);
            foreach (var (list, allow) in new[] { (P.Arr(p, "allow"), true), (P.Arr(p, "disallow"), false) })
            {
                if (list == null) continue;
                foreach (var item in list)
                {
                    string name = item.ToString();
                    if (Lookup.DefOrNull(typeof(ThingCategoryDef), name) is ThingCategoryDef cat) settings.filter.SetAllow(cat, allow);
                    else if (Lookup.DefOrNull(typeof(ThingDef), name) is ThingDef td) settings.filter.SetAllow(td, allow);
                    else if (Lookup.DefOrNull(typeof(SpecialThingFilterDef), name) is SpecialThingFilterDef sf) settings.filter.SetAllow(sf, allow);
                    else throw new RpcError($"'{name}' is not a ThingDef, ThingCategoryDef or SpecialThingFilterDef");
                }
            }
            return new JObject { ["storage"] = what, ["priority"] = settings.Priority.ToString(), ["allowed_defs"] = settings.filter.AllowedDefCount };
        }
    }
}
