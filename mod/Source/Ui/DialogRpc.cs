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

        public static bool IsInteresting(Window w) => w is Dialog_NodeTree || w is Dialog_MessageBox || (w.forcePause && !(w is Dialog_ModSettings) && !w.IsDebug && !(w is ImmediateWindow) && !(w is FloatMenu));

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
            }
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

        [Rpc("ui.dialog", "{i?: window index from state.dialogs (default: topmost), choice?: label|index, name?/second_name?: for naming dialogs, close?: true} answer or close a dialog")]
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
                    w.Close();
                    return new JObject { ["closed"] = w.GetType().Name };
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
