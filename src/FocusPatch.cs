using HarmonyLib;
using XRL.Core;

namespace QudBridge
{
    // Keep the game "focused" while the bridge is running, so an external client can drive it from the
    // background. Unpatched, losing window focus sets GameManager.focused = false (GameManager.cs:1912-1921),
    // and the player-turn loop then sleeps until refocused (XRL.Core/XRLCore.cs:756), so gameQueue is never
    // drained. Unity itself must also keep updating in the background: that is the game's
    // "Keep Caves of Qud active in background" option (Application.runInBackground, XRL.UI/Options.cs:1445).
    // Patches are applied by ModInfo.ApplyHarmonyPatches (XRL/ModInfo.cs:846).
    [HarmonyPatch(typeof(GameManager), "OnApplicationFocus")]
    public static class FocusPatch
    {
        public static bool Prefix()
        {
            if (!BridgeServer.Running)
            {
                return true;
            }
            XRLCore.bThreadFocus = true;
            GameManager.focused = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "OnApplicationPause")]
    public static class PausePatch
    {
        public static bool Prefix()
        {
            if (!BridgeServer.Running)
            {
                return true;
            }
            GameManager.focused = true;
            return false;
        }
    }
}
