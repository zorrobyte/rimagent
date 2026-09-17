using System;
using HarmonyLib;
using RimBridge.Server;
using UnityEngine;
using Verse;

namespace RimBridge
{
    public class BridgeSettings : ModSettings
    {
        public int port = 8765;
        public bool enabled = true;
        public bool neverPause = false;      // keep time flowing even when dialogs/letters would pause
        public bool devModeOnStart = true;   // turn on Prefs.DevMode when the bridge starts

        public override void ExposeData()
        {
            Scribe_Values.Look(ref port, "port", 8765);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref neverPause, "neverPause", false);
            Scribe_Values.Look(ref devModeOnStart, "devModeOnStart", true);
        }
    }

    public class RimBridgeMod : Mod
    {
        public static BridgeSettings Settings = null!;
        public static HttpServer? Server;

        public RimBridgeMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<BridgeSettings>();
            new Harmony("zorrobyte.rimbridge").PatchAll();
            Rpc.RegisterAll();
            if (Settings.enabled)
            {
                Server = new HttpServer(Settings.port);
                Server.Start();
            }
            if (Settings.devModeOnStart) Prefs.DevMode = true;
            BridgeLog.Message($"loaded; {Rpc.Count} rpc methods; listening on 127.0.0.1:{Settings.port}");
        }

        public override string SettingsCategory() => "RimBridge";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            l.CheckboxLabeled("Enabled (restart game to apply)", ref Settings.enabled);
            l.CheckboxLabeled("Never pause (dialogs/letters don't stop time)", ref Settings.neverPause);
            l.CheckboxLabeled("Enable dev mode on start", ref Settings.devModeOnStart);
            string buf = Settings.port.ToString();
            l.TextFieldNumericLabeled("Port", ref Settings.port, ref buf, 1024, 65535);
            l.End();
        }
    }

    public static class BridgeLog
    {
        public static void Message(string msg) => Log.Message("[RimBridge] " + msg);
        public static void Warning(string msg) => Log.Warning("[RimBridge] " + msg);
        public static void Error(string msg) => Log.Error("[RimBridge] " + msg);
    }
}
