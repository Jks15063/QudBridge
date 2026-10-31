using System;
using System.Collections.Generic;
using System.Threading;
using ConsoleLib.Console;
using Newtonsoft.Json.Linq;
using Qud.API;
using XRL;
using XRL.Core;
using XRL.UI;
using XRL.World;
using XRL.World.Anatomy;
using XRL.World.Capabilities;
using XRL.World.Parts;

namespace QudBridge
{
    public class ActionRejectedException : Exception
    {
        public readonly int Status;
        public readonly object Extra;

        public ActionRejectedException(int status, string message, object extra = null) : base(message)
        {
            Status = status;
            Extra = extra;
        }
    }

    // POST /action. Two execution paths, both the ones the game itself uses:
    //  - Commands (move/attack/wait/pickup/stairs) are injected as "Command:<cmd>" mouse events, exactly as
    //    controller buttons (GameManager.cs:2642) and ability-bar clicks (Qud.UI/AbilityBarButton.cs:91) do.
    //    XRLCore.PlayerTurn pops them (XRL.Core/XRLCore.cs:782-799) and runs the same switch(text2) that
    //    mapped keypresses go through (XRLCore.cs:1001), e.g. "CmdMoveN" -> The.Player.Move("N").
    //  - Item actions (eat/drink/equip/unequip/...) run the item's InventoryAction, as the item menu does:
    //    EquipmentAPI.GetInventoryActions (Qud.API/EquipmentAPI.cs:378) then InventoryAction.Process
    //    (XRL.World/InventoryAction.cs:118), the same calls TwiddleObject makes (EquipmentAPI.cs:154). The game
    //    also runs twiddles from gameQueue (Qud.UI/NearbyItemsWindow.cs:197).
    // Completion: the next XRLCore OnBeginPlayerTurn callback (fires once others have acted, XRLCore.cs:693),
    // or, for actions that took no time, the end of the command (OnEndPlayerTurnSingle, XRLCore.cs:2396).
    public static class ActionHandler
    {
        public const int ActionTimeoutMs = 15000;
        // How long the view must stay off "Stage" before we report the action as blocked on a popup/menu.
        public const int BlockedAfterMs = 1000;

        private static readonly string[] Directions = { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };

        // Shortcut verbs for item actions, matched against InventoryAction.Name (see Physics.cs:2578/2605,
        // and the Food/LiquidVolume parts' AddAction calls).
        private static readonly Dictionary<string, string> VerbAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "equip", "AutoEquip" },
            { "unequip", "Remove" },
        };

        private static readonly object ActionLock = new object();
        private static readonly ManualResetEventSlim Progress = new ManualResetEventSlim(false);
        private static int BeginTurns;
        private static int ReadyMarks;
        private static bool HooksRegistered;

        // Called once at mod load. XRLCore's callback lists are static and never cleared (XRLCore.cs:253-257).
        public static void RegisterHooks()
        {
            if (HooksRegistered)
            {
                return;
            }
            HooksRegistered = true;
            XRLCore.RegisterOnBeginPlayerTurnCallback(_ =>
            {
                Interlocked.Increment(ref BeginTurns);
                Progress.Set();
            });
            // "Single" end-of-turn callbacks run only when PlayerTurn just processed a command (the if (flag)
            // block, XRLCore.cs:2396). If the player can still act, the command took no time.
            XRLCore.RegisterOnEndPlayerTurnCallback(_ => MarkReadyIfPlayerCanAct(), Single: true);
        }

        private static void MarkReadyIfPlayerCanAct()
        {
            GameObject player = The.Player;
            if (player != null && player.Energy != null && player.Energy.Value >= 1000 && !AutoAct.IsActive())
            {
                Interlocked.Increment(ref ReadyMarks);
                Progress.Set();
            }
        }

        public static object Handle(JObject body, int messageCount)
        {
            if (!Monitor.TryEnter(ActionLock))
            {
                throw new ActionRejectedException(409, "another action is in progress");
            }
            try
            {
                return Run(body, messageCount);
            }
            finally
            {
                Monitor.Exit(ActionLock);
            }
        }

        private class Started
        {
            public int BeginTurns;
            public int ReadyMarks;
            public int MessageIndex;
            public long Turn;
        }

        private static object Run(JObject body, int messageCount)
        {
            string type = ((string)body["type"])?.ToLowerInvariant();
            if (string.IsNullOrEmpty(type))
            {
                throw new ActionRejectedException(400, "missing \"type\"");
            }
            Progress.Reset();
            // Validate and start the action on the game thread, at a point where the player is waiting for input.
            Started started = GameThread.Invoke(() =>
            {
                RequirePlayerTurn();
                var s = new Started
                {
                    BeginTurns = Volatile.Read(ref BeginTurns),
                    ReadyMarks = Volatile.Read(ref ReadyMarks),
                    MessageIndex = The.Game.Player.Messages.Messages.Count,
                    Turn = The.Game.Turns,
                };
                Start(type, body);
                return s;
            });

            string outcome = WaitForCompletion(started, out string screen);
            if (outcome != "ok")
            {
                return new
                {
                    result = outcome,
                    screen,
                    error = outcome == "blocked"
                        ? "the game is waiting on a popup or menu (" + screen + "); popups are not supported yet"
                        : "action did not finish within " + ActionTimeoutMs + "ms",
                };
            }
            return GameThread.Invoke(() =>
            {
                var all = The.Game.Player.Messages.Messages;
                var log = new List<string>();
                for (int i = Math.Min(started.MessageIndex, all.Count); i < all.Count; i++)
                {
                    log.Add(StateBuilder.Clean(all[i]));
                }
                return new
                {
                    result = "ok",
                    turns = The.Game.Turns - started.Turn,
                    log,
                    state = StateBuilder.Build(messageCount, includeLegend: false),
                };
            });
        }

        private static void RequirePlayerTurn()
        {
            if (The.Game == null || !The.Game.Running || The.Player == null)
            {
                throw new NoGameException("no game in progress");
            }
            string view = GameManager.Instance.CurrentGameView;
            if (view != Options.StageViewID)
            {
                throw new ActionRejectedException(409, "not in normal play (screen: " + view + ")");
            }
            // gameQueue is also drained mid-turn (RenderBase during autoact/animations); only act when the
            // player has a full turn's energy, as PlayerTurn's loop requires (XRLCore.cs:754).
            if (AutoAct.IsActive() || The.Player.Energy.Value < 1000)
            {
                throw new ActionRejectedException(409, "not the player's turn");
            }
        }

        private static void Start(string type, JObject body)
        {
            switch (type)
            {
                case "move":
                    Command("CmdMove" + Direction(body));
                    return;
                case "attack":
                    Command("CmdAttack" + Direction(body));
                    return;
                case "wait":
                    Command("CmdWait");
                    return;
                case "pickup":
                    // CommandGet picks up what's here; several items opens a picker (XRLCore.cs:2163).
                    Command("CmdGet");
                    return;
                case "stairs":
                    Command(Stairs(body));
                    return;
                case "item":
                    ItemAction(body, (string)body["verb"]);
                    return;
                case "eat":
                case "drink":
                case "equip":
                case "unequip":
                    ItemAction(body, type);
                    return;
                default:
                    throw new ActionRejectedException(400, "unknown action type \"" + type + "\"",
                        new { types = new[] { "move", "attack", "wait", "pickup", "stairs", "eat", "drink", "equip", "unequip", "item" } });
            }
        }

        private static void Command(string command)
        {
            Keyboard.PushMouseEvent("Command:" + command);
        }

        private static string Direction(JObject body)
        {
            string dir = ((string)body["dir"])?.ToUpperInvariant();
            if (Array.IndexOf(Directions, dir) < 0)
            {
                throw new ActionRejectedException(400, "\"dir\" must be one of " + string.Join(",", Directions));
            }
            return dir;
        }

        private static string Stairs(JObject body)
        {
            string dir = ((string)body["dir"])?.ToLowerInvariant();
            Cell cell = The.Player.CurrentCell;
            if (dir == "up")
            {
                // On the surface, CmdMoveU without stairs ascends to the world map, as '<' does in-game (XRLCore.cs:1436).
                return "CmdMoveU";
            }
            if (dir == "down")
            {
                // Off stairs, CmdMoveD would start auto-walking to the nearest stairway (XRLCore.cs:1529); refuse
                // instead so "stairs" only ever means "take the stairs here". The world map enters the zone.
                if (!cell.ParentZone.IsWorldMap() && !cell.HasObjectWithPart("StairsDown"))
                {
                    throw new ActionRejectedException(409, "no stairs down here");
                }
                return "CmdMoveD";
            }
            throw new ActionRejectedException(400, "\"dir\" must be \"up\" or \"down\"");
        }

        private static void ItemAction(JObject body, string verb)
        {
            GameObject player = The.Player;
            GameObject item = ResolveItem(player, body);
            List<InventoryAction> actions = EquipmentAPI.GetInventoryActions(item, player) ?? new List<InventoryAction>();
            InventoryAction action = MatchAction(actions, verb);
            if (action == null)
            {
                var available = new List<string>();
                foreach (InventoryAction a in actions)
                {
                    if (a.IsVisible(player, item))
                    {
                        available.Add(StateBuilder.Clean(a.Display));
                    }
                }
                throw new ActionRejectedException(400,
                    (verb == null ? "missing \"verb\"" : "no \"" + verb + "\" action") + " for " + StateBuilder.Clean(item.DisplayName),
                    new { available });
            }
            if (!action.IsUsable(player, item))
            {
                throw new ActionRejectedException(409, "cannot " + StateBuilder.Clean(action.Display) + " " + StateBuilder.Clean(item.DisplayName) + " right now");
            }
            // Run it as its own queued task rather than inside this Invoke: it may open a popup, and the HTTP
            // side detects that by watching the view instead of blocking on this call.
            GameThread.Post(() =>
            {
                action.Process(item, player);
                MarkReadyIfPlayerCanAct();
            });
        }

        private static GameObject ResolveItem(GameObject player, JObject body)
        {
            JToken index = body["item"];
            string slot = (string)body["slot"];
            if (index != null && index.Type == JTokenType.Integer)
            {
                var objects = player.Inventory?.Objects;
                int i = (int)index;
                if (objects == null || i < 0 || i >= objects.Count)
                {
                    throw new ActionRejectedException(400, "\"item\" " + i + " is not an inventory index (see state.inventory[].i)");
                }
                return objects[i];
            }
            if (slot != null)
            {
                if (player.Body != null)
                {
                    foreach (BodyPart part in player.Body.GetParts())
                    {
                        if (part.Equipped != null && string.Equals(part.GetCardinalDescription().Strip(), slot, StringComparison.OrdinalIgnoreCase))
                        {
                            return part.Equipped;
                        }
                    }
                }
                throw new ActionRejectedException(400, "nothing equipped in slot \"" + slot + "\" (see state.equipped[].slot)");
            }
            throw new ActionRejectedException(400, "give \"item\" (inventory index) or \"slot\" (equipped slot)");
        }

        // Match by InventoryAction.Name ("Eat", "AutoEquip"), then by its menu text ("eat", "equip (auto)").
        private static InventoryAction MatchAction(List<InventoryAction> actions, string verb)
        {
            if (string.IsNullOrEmpty(verb))
            {
                return null;
            }
            string name = VerbAliases.TryGetValue(verb, out string alias) ? alias : verb;
            foreach (InventoryAction a in actions)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }
            foreach (InventoryAction a in actions)
            {
                if (string.Equals(StateBuilder.Clean(a.Display), verb, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }
            return null;
        }

        // Runs on the HTTP thread. Only reads two ints and the current view name; never touches game state.
        private static string WaitForCompletion(Started started, out string screen)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long offStageSince = -1;
            while (true)
            {
                if (Volatile.Read(ref BeginTurns) != started.BeginTurns || Volatile.Read(ref ReadyMarks) != started.ReadyMarks)
                {
                    screen = null;
                    return "ok";
                }
                screen = GameManager.Instance?.CurrentGameView;
                if (screen != Options.StageViewID)
                {
                    if (offStageSince < 0)
                    {
                        offStageSince = timer.ElapsedMilliseconds;
                    }
                    else if (timer.ElapsedMilliseconds - offStageSince >= BlockedAfterMs)
                    {
                        return "blocked";
                    }
                }
                else
                {
                    offStageSince = -1;
                }
                if (timer.ElapsedMilliseconds >= ActionTimeoutMs)
                {
                    return "timeout";
                }
                Progress.Wait(50);
                Progress.Reset();
            }
        }
    }
}
