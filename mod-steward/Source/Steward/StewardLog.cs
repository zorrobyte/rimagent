using Verse;

namespace RimBridge.Steward
{
    /// <summary>All steward logging: "[RimBridge] steward: ..." through BridgeLog, plus once-per-key variants.</summary>
    public static class StewardLog
    {
        public const string Prefix = "steward: ";

        public static void Message(string msg) => BridgeLog.Message(Prefix + msg);
        public static void Warning(string msg) => BridgeLog.Warning(Prefix + msg);
        public static void Error(string msg) => BridgeLog.Error(Prefix + msg);
        public static void ErrorOnce(string msg, int key) => Log.ErrorOnce("[RimBridge] " + Prefix + msg, key);
        public static void WarningOnce(string msg, int key) => Log.WarningOnce("[RimBridge] " + Prefix + msg, key);
    }
}
