// Derived from Colony Manager Redux Patches/Verse_AreaManager_NotifyEveryoneAreaRemoved.cs (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using HarmonyLib;
using Verse;

namespace RimBridge.Steward.Stock
{
    [HarmonyPatch(typeof(AreaManager), "NotifyEveryoneAreaRemoved")]
    public static class Patch_AreaRemoved
    {
        [HarmonyPostfix]
        public static void Postfix(AreaManager __instance, Area area)
        {
            StockComponent.For(__instance.map)?.Notify_AreaRemoved(area);
        }
    }
}
