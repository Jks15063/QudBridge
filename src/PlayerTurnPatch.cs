using System;
using HarmonyLib;
using XRL.Core;

namespace QudBridge
{
    // Marks XRLCore.PlayerTurn (XRL.Core/XRLCore.cs:690) for ActionHandler.InPlayerTurn. Its loop can also end with
    // the player still at 1000 energy (SkipPlayerTurn, or the UI-thread hand-off, XRLCore.cs:2410-2421), so an
    // energy check alone can't tell that it returned.
    [HarmonyPatch(typeof(XRLCore), nameof(XRLCore.PlayerTurn))]
    public static class PlayerTurnPatch
    {
        public static void Prefix()
        {
            ActionHandler.InPlayerTurn = true;
        }

        public static Exception Finalizer(Exception __exception)
        {
            ActionHandler.InPlayerTurn = false;
            return __exception;
        }
    }
}
