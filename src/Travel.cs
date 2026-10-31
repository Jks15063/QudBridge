using System;
using ConsoleLib.Console;
using Newtonsoft.Json.Linq;
using XRL;
using XRL.Core;
using XRL.World;
using XRL.World.Capabilities;

namespace QudBridge
{
    // Multi-turn movement and waiting, all through the game's own auto-actions (AutoAct.Setting), which
    // ActionManager steps through one turn at a time (XRL.Core/ActionManager.cs:855). They stop by themselves when
    // hostiles come into view ("You stop exploring because ..." in the log) or a popup is raised. The begin-turn
    // hook ignores the steps (ActionHandler.RegisterHooks), so the action returns when the whole thing is over.
    // Game thread only.
    public static class Travel
    {
        // explore: auto-explore, as the sidebar button does (Qud.UI/PlayerStatusBar.cs:632, XRLCore.cs:1939).
        // When nothing is left it shows "There doesn't seem to be anywhere else to explore." (a popup).
        public static void Explore()
        {
            if (The.Player.OnWorldMap())
            {
                throw new ActionRejectedException(409, "auto-explore doesn't work on the world map");
            }
            Keyboard.PushMouseEvent("Command:CmdAutoExplore");
        }

        // travel {"x","y"}: walk to a cell of this zone, as clicking the map does (TileAreaHandler.cs:77 ->
        // XRLCore.cs:927, auto-act "M"). Unexplored cells are allowed (the game explores its way there); no path
        // shows "You cannot find a path to your destination." (a popup).
        // travel {"edge": "N"|"S"|"E"|"W"}: walk to that edge of the zone (CmdMoveToEdge, XRLCore.cs:1274);
        // returns the direction to give its direction prompt.
        public static string To(JObject body)
        {
            GameObject player = The.Player;
            Zone zone = player.CurrentZone;
            if (body["edge"] != null)
            {
                string edge = ((string)body["edge"])?.ToUpperInvariant();
                if (edge != "N" && edge != "S" && edge != "E" && edge != "W")
                {
                    throw new ActionRejectedException(400, "\"edge\" must be one of N,S,E,W");
                }
                if (zone == null || zone.IsWorldMap())
                {
                    throw new ActionRejectedException(409, "there are no zone edges on the world map");
                }
                Keyboard.PushMouseEvent("Command:CmdMoveToEdge");
                return edge;
            }
            JToken jx = body["x"], jy = body["y"];
            if (jx == null || jy == null || jx.Type != JTokenType.Integer || jy.Type != JTokenType.Integer)
            {
                throw new ActionRejectedException(400, "give the destination as \"x\" and \"y\", or a zone edge as \"edge\"");
            }
            int x = (int)jx, y = (int)jy;
            if (zone == null || x < 0 || y < 0 || x >= zone.Width || y >= zone.Height)
            {
                throw new ActionRejectedException(400, "(" + x + "," + y + ") is outside the map");
            }
            Cell here = player.CurrentCell;
            if (here.X == x && here.Y == y)
            {
                throw new ActionRejectedException(400, "you're already there");
            }
            // Next to the player this is a plain move (XRLCore.cs:937), which also attacks whatever is there.
            Keyboard.PushMouseEvent("Command:CmdMoveToBorder:" + x + "," + y);
            return null;
        }

        // rest: wait until healed (CmdWaitUntilHealed, XRLCore.cs:1615). With hostiles in view the game refuses
        // with a popup naming them.
        public static void Rest()
        {
            Keyboard.PushMouseEvent("Command:CmdWaitUntilHealed");
        }

        // wait {"turns": n}: what "Wait N Turns" does after asking for the number (CmdWaitN, XRLCore.cs:1571).
        // Posted as its own task because the hostile check can show a popup.
        public static void WaitTurns(JObject body)
        {
            JToken t = body["turns"];
            if (t.Type != JTokenType.Integer || (int)t < 1)
            {
                throw new ActionRejectedException(400, "\"turns\" must be a positive integer");
            }
            int turns = (int)t;
            if (turns == 1)
            {
                Keyboard.PushMouseEvent("Command:CmdWait");
                return;
            }
            ActionHandler.Posted(() =>
            {
                if (!AutoAct.ShouldHostilesInterrupt(".", null, logSpot: false, popSpot: true))
                {
                    AutoAct.Setting = "." + turns;
                    The.Player.PassTurn();
                }
            });
        }
    }
}
