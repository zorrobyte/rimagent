using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimBridge.Ledger;
using RimBridge.Server;
using RimWorld;
using Verse;

namespace RimBridge.Ui
{
    /// <summary>Open modal windows (choice dialogs, message boxes) — the player sees them, so the agent must too.</summary>
    public static class DialogRpc
    {
        static readonly AccessTools.FieldRef<Dialog_NodeTree, DiaNode> CurNode = AccessTools.FieldRefAccess<Dialog_NodeTree, DiaNode>("curNode");
        static readonly System.Reflection.MethodInfo Activate = AccessTools.Method(typeof(DiaOption), "Activate");

        static string OptText(DiaOption o) => ((string)AccessTools.Field(typeof(DiaOption), "text").GetValue(o) ?? "").StripTags();

        /// <summary>Every real window counts (dialogs, trade, caravans, rituals...), not just the ones we have readers for. UI chrome is excluded.</summary>
        public static bool IsInteresting(Window w)
        {
            if (w == null || w.IsDebug || w is ImmediateWindow || w is FloatMenu || w is MainTabWindow || w is Dialog_ModSettings || w is Dialog_Options) return false;
            string n = w.GetType().Name;
            if (n.StartsWith("Dialog_Debug") || n.Contains("EditWindow") || n == "Dialog_InfoCard" || n.StartsWith("Page_")) return false;
            return true;
        }

        /// <summary>Generic introspection for windows we have no specific reader for: text-ish fields, pawn lists, and invokable actions/methods.</summary>
        static void DescribeGeneric(Window w, JObject o)
        {
            var t = w.GetType();
            var texts = new JObject(); var actions = new JArray(); var pawns = new JObject(); var numbers = new JObject();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy))
            {
                object? v; try { v = f.GetValue(w); } catch { continue; }
                if (v == null) continue;
                switch (v)
                {
                    case string sv when sv.Length > 0 && sv.Length < 2000 && !f.Name.Contains("Key") && !f.Name.Contains("Tex"): texts[f.Name] = sv.StripTags(); break;
                    case TaggedString ts when ts.RawText != null && ts.RawText.Length > 0: texts[f.Name] = ts.ToString().StripTags(); break;
                    case Action _: actions.Add(f.Name); break;
                    case Pawn pw: pawns[f.Name] = pw.LabelShort; break;
                    case IEnumerable<Pawn> pl: pawns[f.Name] = new JArray(pl.Take(20).Select(x => x.LabelShort)); break;
                    case int iv: numbers[f.Name] = iv; break;
                    case float fv: numbers[f.Name] = Math.Round(fv, 2); break;
                    case bool bv when f.Name.ToLowerInvariant().Contains("can") || f.Name.ToLowerInvariant().Contains("enabled"): numbers[f.Name] = bv; break;
                }
            }
            var methods = new JArray();
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy))
            {
                if (m.GetParameters().Length != 0 || m.ReturnType != typeof(void) || m.DeclaringType == typeof(Window) || m.DeclaringType == typeof(object)) continue;
                string n = m.Name;
                if (n.StartsWith("Do") || n.StartsWith("Draw") || n.StartsWith("get_") || n.StartsWith("set_") || n == "ExposeData" || n.Contains("Update") || n.Contains("GUI")) continue;
                if (n.Contains("Accept") || n.Contains("Start") || n.Contains("Cancel") || n.Contains("Confirm") || n.Contains("Close") || n.Contains("Reset") || n.Contains("Send") || n.Contains("Launch") || n.Contains("Try") || n.Contains("Finish") || n.Contains("Done") || n.Contains("Ok") || n.Contains("Begin") || n.Contains("Choose"))
                    methods.Add(n);
            }
            if (texts.Count > 0) o["fields"] = texts;
            if (numbers.Count > 0) o["numbers"] = numbers;
            if (pawns.Count > 0) o["pawns"] = pawns;
            if (actions.Count > 0) o["actions"] = actions;
            if (methods.Count > 0) o["methods"] = methods;
            if (o["how"] == null) o["how"] = "generic window: rw_ui_dialog(method=\"<name>\") invokes a listed method, rw_ui_dialog(action=\"<field>\") invokes a listed Action field, rw_ui_dialog(set={field: value}) writes a field, rw_ui_dialog(close=true) closes.";
        }

        public static JObject Describe(Window w, int index)
        {
            var o = new JObject { ["i"] = index, ["type"] = w.GetType().Name, ["force_pause"] = w.forcePause };
            if (!string.IsNullOrEmpty(w.optionalTitle)) o["title"] = w.optionalTitle.StripTags();
            switch (w)
            {
                case Dialog_NodeTree nt:
                {
                    var node = CurNode(nt);
                    o["text"] = node?.text.ToString().StripTags();
                    o["choices"] = new JArray((node?.options ?? new List<DiaOption>()).Select((c, i) => new JObject { ["i"] = i, ["label"] = OptText(c), ["disabled"] = c.disabled, ["reason"] = c.disabled ? c.disabledReason : null }));
                    break;
                }
                case Dialog_GiveName gn:
                {
                    var t = Traverse.Create(gn);
                    string? cur = t.Field("curName").GetValue<string>();
                    string? cur2 = t.Field("curSecondName").GetValue<string>();
                    bool second = t.Field("useSecondName").GetValue<bool>();
                    string? key = t.Field("nameMessageKey").GetValue<string>();
                    var pawn = t.Field("suggestingPawn").GetValue<Pawn>();
                    string prompt = "";
                    try { prompt = key != null ? key.Translate(pawn?.LabelShort ?? "", pawn).ToString().StripTags() : ""; } catch { }
                    if (string.IsNullOrEmpty(cur)) { try { cur = t.Field("nameGenerator").GetValue<Func<string>>()?.Invoke(); t.Field("curName").SetValue(cur); } catch { } }
                    if (second && string.IsNullOrEmpty(cur2)) { try { cur2 = t.Field("secondNameGenerator").GetValue<Func<string>>()?.Invoke(); t.Field("curSecondName").SetValue(cur2); } catch { } }
                    o["text"] = prompt;
                    o["kind"] = "give_name";
                    o["fields"] = new JObject { ["name"] = cur, ["second_name"] = second ? cur2 : null };
                    o["how"] = "rw_ui_dialog(name=..., second_name=...) to set your own, or rw_ui_dialog(choice=\"OK\") to accept the suggestions";
                    break;
                }
                case Dialog_BeginLordJob lj:
                {
                    // ritual / lord-job setup: header, description, blocking issues, role assignments, Start/Cancel
                    o["kind"] = "begin_ritual";
                    try { o["title"] = lj.HeaderLabel.ToString().StripTags(); } catch { }
                    TargetInfo target = TargetInfo.Invalid;
                    try { target = Traverse.Create(lj).Field("target").GetValue<TargetInfo>(); } catch { }
                    try { o["text"] = (lj.DescriptionLabel.ToString() + "\n" + lj.ExtraExplanationLabel.ToString()).StripTags().Trim(); } catch { }
                    try { o["can_begin"] = lj.CanBegin; } catch { }
                    try { o["blocking_issues"] = new JArray((Traverse.Create(lj).Method("BlockingIssues").GetValue<IEnumerable<string>>() ?? Enumerable.Empty<string>()).Select(x => x.StripTags())); } catch { }
                    try
                    {
                        var assignments = Traverse.Create(lj).Field("assignments").GetValue<RitualRoleAssignments>();
                        if (assignments != null)
                        {
                            var roles = new JArray();
                            foreach (var role in assignments.AllRolesForReading)
                                roles.Add(new JObject { ["id"] = role.id, ["label"] = role.Label.ToString(), ["required"] = role.required, ["assigned"] = new JArray(assignments.AssignedPawns(role).Select(x => x.LabelShort)), ["candidates"] = new JArray(assignments.CandidatesForRole(role, target).Take(8).Select(x => x.LabelShort)) });
                            o["roles"] = roles;
                            o["spectators"] = new JArray(assignments.SpectatorsForReading.Select(x => x.LabelShort));
                        }
                    }
                    catch (Exception ex) { o["roles_error"] = ex.Message; }
                    var ch = new JArray();
                    string ok = "Start"; try { ok = lj.OkButtonLabel.ToString().StripTags(); } catch { }
                    ch.Add(new JObject { ["i"] = 0, ["label"] = ok, ["disabled"] = !(bool)(o["can_begin"] ?? true) });
                    ch.Add(new JObject { ["i"] = 1, ["label"] = "Cancel" });
                    o["choices"] = ch;
                    o["how"] = "rw_ui_dialog(choice=\"Start\") begins it with the shown assignments; rw_ui_dialog(choice=\"Cancel\") dismisses. To (re)assign a role first: rw_ui_dialog(assign={\"<role id>\": \"<pawn>\"}) then Start.";
                    break;
                }
                case Dialog_Trade dt:
                {
                    o["kind"] = "trade";
                    var deal = TradeSession.deal;
                    o["trader"] = TradeSession.trader?.TraderName;
                    o["negotiator"] = TradeSession.playerNegotiator?.LabelShort;
                    o["gift_mode"] = TradeSession.giftMode;
                    var items = new JArray();
                    if (deal != null)
                    {
                        int idx2 = 0;
                        foreach (var tr in deal.AllTradeables)
                        {
                            if (!tr.TraderWillTrade && tr.CountHeldBy(Transactor.Colony) == 0) { idx2++; continue; }
                            items.Add(new JObject
                            {
                                ["i"] = idx2++, ["def"] = tr.ThingDef?.defName, ["label"] = tr.Label,
                                ["trader_has"] = tr.CountHeldBy(Transactor.Trader), ["colony_has"] = tr.CountHeldBy(Transactor.Colony),
                                ["buy_price"] = Math.Round(tr.GetPriceFor(TradeAction.PlayerBuys), 1), ["sell_price"] = Math.Round(tr.GetPriceFor(TradeAction.PlayerSells), 1),
                                ["buying"] = tr.CountToTransfer < 0 ? -tr.CountToTransfer : 0, ["selling"] = tr.CountToTransfer > 0 ? tr.CountToTransfer : 0, ["currency"] = tr.IsCurrency, ["will_trade"] = tr.TraderWillTrade,
                            });
                        }
                        o["silver_colony"] = deal.CurrencyTradeable?.CountHeldBy(Transactor.Colony);
                        o["silver_trader"] = deal.CurrencyTradeable?.CountHeldBy(Transactor.Trader);
                        o["trader_can_afford"] = deal.DoesTraderHaveEnoughSilver();
                    }
                    o["items"] = items;
                    o["how"] = "rw_ui_dialog(trade={\"<def or i>\": n}) with n>0 = BUY n from the trader, n<0 = SELL n to the trader (sets the counts, silver balances automatically); then rw_ui_dialog(choice=\"Accept\"). rw_ui_dialog(choice=\"Cancel\") leaves.";
                    break;
                }
                case Dialog_MessageBox mb:
                {
                    o["text"] = mb.text.ToString().StripTags();
                    if (!string.IsNullOrEmpty(mb.title)) o["title"] = mb.title.StripTags();
                    var ch = new JArray();
                    if (!string.IsNullOrEmpty(mb.buttonAText)) ch.Add(new JObject { ["i"] = 0, ["label"] = mb.buttonAText.StripTags() });
                    if (!string.IsNullOrEmpty(mb.buttonBText)) ch.Add(new JObject { ["i"] = 1, ["label"] = mb.buttonBText.StripTags() });
                    if (!string.IsNullOrEmpty(mb.buttonCText)) ch.Add(new JObject { ["i"] = 2, ["label"] = mb.buttonCText.StripTags() });
                    o["choices"] = ch;
                    break;
                }
                default:
                    DescribeGeneric(w, o);
                    break;
            }
            if (o["how"] == null && !(w is Dialog_NodeTree) && !(w is Dialog_MessageBox)) DescribeGeneric(w, o);
            return o;
        }

        [Rpc("state.dialogs", "open modal windows with their text and choices (these pause the game until answered; answer with ui.dialog)")]
        public static JToken Dialogs(JObject p)
        {
            var arr = new JArray();
            var ws = Find.WindowStack?.Windows;
            if (ws == null) return arr;
            for (int i = 0; i < ws.Count; i++) if (IsInteresting(ws[i])) arr.Add(Describe(ws[i], i));
            return arr;
        }

        [Rpc("ui.dialog", "{i?: window index from state.dialogs (default: topmost), choice?: label|index, name?/second_name?: naming dialogs, assign?: {role: pawn} rituals, trade?: {def|i: +buy/-sell} trade window, method?/action?/set? generic windows, close?: true} answer any open window")]
        public static JToken Answer(JObject p)
        {
            var ws = Find.WindowStack?.Windows ?? new List<Window>();
            Window? w = null;
            if (p["i"] != null) { int i = P.Int(p, "i"); if (i < 0 || i >= ws.Count) throw new RpcError("window index out of range"); w = ws[i]; }
            else w = ws.LastOrDefault(IsInteresting);
            if (w == null) throw new RpcError("no open dialog");
            if (P.Bool(p, "close", false)) { w.Close(); return new JObject { ["closed"] = w.GetType().Name }; }
            string? label = P.OptStr(p, "choice");
            int idx = p["choice"]?.Type == JTokenType.Integer ? (int)p["choice"]! : -1;
            switch (w)
            {
                case Dialog_GiveName gn:
                {
                    var t = Traverse.Create(gn);
                    bool second = t.Field("useSecondName").GetValue<bool>();
                    string name = P.OptStr(p, "name") ?? t.Field("curName").GetValue<string>() ?? t.Field("nameGenerator").GetValue<Func<string>>()?.Invoke() ?? "";
                    string name2 = P.OptStr(p, "second_name") ?? t.Field("curSecondName").GetValue<string>() ?? (second ? t.Field("secondNameGenerator").GetValue<Func<string>>()?.Invoke() : null) ?? "";
                    if (!t.Method("IsValidName", name).GetValue<bool>()) throw new RpcError($"invalid name '{name}'");
                    if (second && !t.Method("IsValidSecondName", name2).GetValue<bool>()) throw new RpcError($"invalid second name '{name2}'");
                    t.Method("Named", name).GetValue();
                    if (second) t.Method("NamedSecond", name2).GetValue();
                    Find.WindowStack.TryRemove(gn);
                    EventLedger.Add("dialog_answered", $"named '{name}'" + (second ? $" / '{name2}'" : ""));
                    return new JObject { ["named"] = name, ["second_name"] = second ? name2 : null };
                }
                case Dialog_NodeTree nt:
                {
                    var opts = CurNode(nt)?.options ?? new List<DiaOption>();
                    var opt = idx >= 0 ? opts.ElementAtOrDefault(idx) : opts.FirstOrDefault(c => OptText(c).Equals(label, StringComparison.OrdinalIgnoreCase)) ?? opts.FirstOrDefault(c => OptText(c).IndexOf(label ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (opt == null) throw new RpcError("no such choice. Available: " + string.Join(" | ", opts.Select(OptText)));
                    if (opt.disabled) throw new RpcError("choice disabled: " + opt.disabledReason);
                    string chosen = OptText(opt);
                    Activate.Invoke(opt, null);
                    EventLedger.Add("dialog_answered", $"chose '{chosen}'");
                    var still = Find.WindowStack.Windows.Contains(nt) ? Describe(nt, Find.WindowStack.Windows.IndexOf(nt)) : null;
                    return new JObject { ["chose"] = chosen, ["next"] = still };
                }
                case Dialog_BeginLordJob lj:
                {
                    var tr = Traverse.Create(lj);
                    if (p["assign"] is JObject asg)
                    {
                        var assignments = tr.Field("assignments").GetValue<RitualRoleAssignments>() ?? throw new RpcError("no assignments on this dialog");
                        var results = new JObject();
                        foreach (var kv in asg)
                        {
                            var role = assignments.AllRolesForReading.FirstOrDefault(r => r.id == kv.Key || string.Equals(r.Label, kv.Key, StringComparison.OrdinalIgnoreCase)) ?? throw new RpcError($"no role '{kv.Key}'");
                            var pawn = Engine.Lookup.Pawn(kv.Value!.ToString());
                            bool okA = assignments.TryAssign(pawn, role, out var reason);
                            results[kv.Key] = okA ? "assigned " + pawn.LabelShort : "failed: " + reason;
                        }
                        if (label == null && idx < 0) return new JObject { ["assign"] = results, ["dialog"] = Describe(lj, Find.WindowStack.Windows.IndexOf(lj)) };
                    }
                    bool start = idx == 0 || (label != null && (label.Equals("start", StringComparison.OrdinalIgnoreCase) || label.Equals("ok", StringComparison.OrdinalIgnoreCase) || label.Equals("begin", StringComparison.OrdinalIgnoreCase) || (lj.OkButtonLabel.ToString().StripTags().IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)));
                    if (start)
                    {
                        if (!lj.CanBegin) throw new RpcError("cannot begin: " + string.Join("; ", tr.Method("BlockingIssues").GetValue<IEnumerable<string>>() ?? Enumerable.Empty<string>()));
                        tr.Method("Start").GetValue();
                        EventLedger.Add("dialog_answered", $"started {lj.HeaderLabel.ToString().StripTags()}");
                        return new JObject { ["started"] = lj.HeaderLabel.ToString().StripTags() };
                    }
                    tr.Method("Cancel").GetValue();
                    EventLedger.Add("dialog_answered", $"cancelled {lj.HeaderLabel.ToString().StripTags()}");
                    return new JObject { ["cancelled"] = lj.HeaderLabel.ToString().StripTags() };
                }
                case Dialog_Trade dt:
                {
                    var deal = TradeSession.deal ?? throw new RpcError("no trade session");
                    if (p["trade"] is JObject tj)
                    {
                        var res = new JObject();
                        foreach (var kv in tj)
                        {
                            Tradeable? tr = int.TryParse(kv.Key, out int ti) ? deal.AllTradeables.ElementAtOrDefault(ti) : deal.AllTradeables.FirstOrDefault(x => x.ThingDef?.defName == kv.Key) ?? deal.AllTradeables.FirstOrDefault(x => string.Equals(x.Label, kv.Key, StringComparison.OrdinalIgnoreCase));
                            if (tr == null) { res[kv.Key] = "not found"; continue; }
                            int n = (int)kv.Value!;
                            // Our convention: n > 0 = BUY n, n < 0 = SELL n. Engine convention: CountToTransfer > 0 = player sells. So negate.
                            int min = tr.GetMinimumToTransfer(), max = tr.GetMaximumToTransfer();
                            int want = Math.Max(min, Math.Min(max, -n));
                            tr.AdjustTo(want);
                            res[kv.Key] = $"{(tr.CountToTransfer < 0 ? "buy " + (-tr.CountToTransfer) : tr.CountToTransfer > 0 ? "sell " + tr.CountToTransfer : "none")} (engine range {min}..{max})";
                        }
                        if (label == null && idx < 0) return new JObject { ["trade"] = res, ["dialog"] = Describe(dt, Find.WindowStack.Windows.IndexOf(dt)) };
                    }
                    bool accept = idx == 0 || (label != null && (label.IndexOf("accept", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("gift", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (accept)
                    {
                        if (!deal.DoesTraderHaveEnoughSilver()) throw new RpcError("trader cannot afford this deal");
                        bool ok2 = deal.TryExecute(out bool traded);
                        if (ok2) { dt.Close(); EventLedger.Add("dialog_answered", traded ? "trade executed" : "trade closed (nothing traded)"); return new JObject { ["traded"] = traded }; }
                        throw new RpcError("trade could not execute (check silver / counts)");
                    }
                    dt.Close();
                    EventLedger.Add("dialog_answered", "trade cancelled");
                    return new JObject { ["cancelled"] = true };
                }
                case Dialog_MessageBox mb:
                {
                    var actions = new[] { (mb.buttonAText, mb.buttonAAction), (mb.buttonBText, mb.buttonBAction), (mb.buttonCText, mb.buttonCAction) };
                    int pick = idx >= 0 ? idx : Array.FindIndex(actions, a => !string.IsNullOrEmpty(a.Item1) && a.Item1.StripTags().IndexOf(label ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (pick < 0 || pick > 2 || string.IsNullOrEmpty(actions[pick].Item1)) throw new RpcError("no such button. Available: " + string.Join(" | ", actions.Where(a => !string.IsNullOrEmpty(a.Item1)).Select(a => a.Item1.StripTags())));
                    actions[pick].Item2?.Invoke();
                    mb.Close();
                    EventLedger.Add("dialog_answered", $"pressed '{actions[pick].Item1.StripTags()}'");
                    return new JObject { ["pressed"] = actions[pick].Item1.StripTags() };
                }
                default:
                {
                    var tr = Traverse.Create(w);
                    if (p["set"] is JObject setj)
                    {
                        foreach (var kv in setj)
                        {
                            var f = tr.Field(kv.Key);
                            if (!f.FieldExists()) throw new RpcError($"no field '{kv.Key}'");
                            var ft = f.GetValueType();
                            f.SetValue(Engine.Coerce.To(kv.Value, ft));
                        }
                        if (p["method"] == null && p["action"] == null) return new JObject { ["set"] = setj, ["dialog"] = Describe(w, Find.WindowStack.Windows.IndexOf(w)) };
                    }
                    if (p["action"] != null)
                    {
                        var f = tr.Field(P.Str(p, "action"));
                        if (!f.FieldExists()) throw new RpcError("no such Action field");
                        (f.GetValue() as Action)?.Invoke();
                        EventLedger.Add("dialog_answered", $"{w.GetType().Name}: action {P.Str(p, "action")}");
                        return new JObject { ["invoked"] = P.Str(p, "action"), ["still_open"] = Find.WindowStack.Windows.Contains(w) };
                    }
                    if (p["method"] != null)
                    {
                        var m = tr.Method(P.Str(p, "method"));
                        if (!m.MethodExists()) throw new RpcError("no such method (see 'methods' in state.dialogs)");
                        m.GetValue();
                        EventLedger.Add("dialog_answered", $"{w.GetType().Name}: {P.Str(p, "method")}");
                        return new JObject { ["invoked"] = P.Str(p, "method"), ["still_open"] = Find.WindowStack.Windows.Contains(w) };
                    }
                    if (label != null)
                    {
                        // try a method whose name matches the choice label
                        foreach (var m in w.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy))
                            if (m.GetParameters().Length == 0 && m.Name.IndexOf(label.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0 && m.ReturnType == typeof(void))
                            { m.Invoke(w, null); EventLedger.Add("dialog_answered", $"{w.GetType().Name}: {m.Name}"); return new JObject { ["invoked"] = m.Name, ["still_open"] = Find.WindowStack.Windows.Contains(w) }; }
                        throw new RpcError($"no choice/method matching '{label}' on {w.GetType().Name}; use method=/action=/set=/close=true");
                    }
                    w.Close();
                    EventLedger.Add("dialog_answered", $"closed {w.GetType().Name}");
                    return new JObject { ["closed"] = w.GetType().Name };
                }
            }
        }
    }

    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    static class Patch_WindowAdd
    {
        static void Postfix(Window window)
        {
            try
            {
                if (!DialogRpc.IsInteresting(window)) return;
                var d = DialogRpc.Describe(window, -1);
                EventLedger.Add("dialog", $"{window.GetType().Name}: {State.Snapshot.Trunc(((string?)d["text"] ?? (string?)d["title"] ?? ""), 160)}", d);
            }
            catch (Exception ex) { BridgeLog.Warning("ledger dialog: " + ex.Message); }
        }
    }
}
