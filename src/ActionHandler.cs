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
        public const int AnsweredGraceMs = 1500;

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
        private static long LastTurn;
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
            // PlayerTurn runs (and calls this, XRLCore.cs:693) for every step of an auto-action too ("take all",
            // auto-explore, walking), so a turn only counts as new once no auto-action is running; Settle then
            // returns after the whole auto-action instead of after its first step.
            // Likewise when the low-HP warning is pending: PlayerTurn shows it right after this callback
            // (XRLCore.cs:767), so the turn isn't ready yet; Settle's UI probe reports the popup instead.
            XRLCore.RegisterOnBeginPlayerTurnCallback(core =>
            {
                Interlocked.Exchange(ref LastTurn, The.Game?.Turns ?? 0);
                if (AutoAct.IsActive() || core.HPWarning)
                {
                    Progress.Set();
                    return;
                }
                Interlocked.Increment(ref BeginTurns);
                Progress.Set();
            });
            // Every message-log line passes through here (XRL.Messages/MessageQueue.cs:148), on whatever thread
            // added it. Keeping our own numbered copy lets any request report the lines it caused, even while
            // the game thread is blocked in a popup.
            XRLCore.RegisterNewMessageLogEntryCallback(MessageLog.Add);
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
            public long MessageSeq;
            public long Turn;
            // Answer for a direction prompt the command is expected to raise ("talk").
            public string AutoDirection;
            // The non-popup prompt "choose" just answered, serialized; it may still be up briefly (see Run).
            public string Answered;
        }

        private static Started Snapshot(long turn)
        {
            return new Started
            {
                BeginTurns = Volatile.Read(ref BeginTurns),
                ReadyMarks = Volatile.Read(ref ReadyMarks),
                MessageSeq = MessageLog.LastSeq,
                Turn = turn,
            };
        }

        private static object Run(JObject body, int messageCount)
        {
            string type = ((string)body["type"])?.ToLowerInvariant();
            if (string.IsNullOrEmpty(type))
            {
                throw new ActionRejectedException(400, "missing \"type\"");
            }
            Progress.Reset();
            Started started;
            if (type == "choose")
            {
                // The game thread is blocked in the popup, so this is answered on the UI thread.
                started = Snapshot(Interlocked.Read(ref LastTurn));
                started.Answered = UiThread.Invoke(() =>
                {
                    object before = PopupBridge.DescribePrompt();
                    PopupBridge.Choose(body);
                    // Answering a popup calls its Hide(), which clears canvasGroup.interactable at once
                    // (Qud.UI/PopupMessage.cs:1092), so PopupBridge.Top() never sees it again: any popup found
                    // afterwards is a new one, even with identical text. The other prompts close a moment later
                    // (the picker and trade screen finish asynchronously; the direction picker reads our key on
                    // the game thread), so for those remember what was answered and give it time to close.
                    if (before == null || (string)JObject.FromObject(before)["kind"] == "popup")
                    {
                        return null;
                    }
                    return Newtonsoft.Json.JsonConvert.SerializeObject(before);
                });
            }
            else
            {
                // Validate and start the action on the game thread, at a point where the player is waiting for input.
                started = GameThread.Invoke(() =>
                {
                    RequirePlayerTurn();
                    Started s = Snapshot(The.Game.Turns);
                    s.AutoDirection = Start(type, body);
                    return s;
                });
            }
            return Settle(started, messageCount);
        }

        private static object Finish(Started started, int messageCount)
        {
            return GameThread.Invoke(() =>
            {
                Interlocked.Exchange(ref LastTurn, The.Game.Turns);
                return new
                {
                    result = "ok",
                    turns = The.Game.Turns - started.Turn,
                    log = MessageLog.Since(started.MessageSeq),
                    state = StateBuilder.Build(messageCount, includeLegend: false),
                };
            });
        }

        // On the game thread: is the player back in normal play with a turn to take?
        private static bool IsPlayerReady()
        {
            return The.Game != null && The.Game.Running && The.Player != null
                && GameManager.Instance.CurrentGameView == Options.StageViewID
                && !AutoAct.IsActive() && The.Player.Energy.Value >= 1000;
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

        // Starts the action; returns the direction to give if the command will ask for one.
        private static string Start(string type, JObject body)
        {
            switch (type)
            {
                case "move":
                    Command("CmdMove" + Direction(body));
                    return null;
                case "attack":
                {
                    string dir = Direction(body);
                    RequireMeleeTarget(dir);
                    Command("CmdAttack" + dir);
                    return null;
                }
                case "fire":
                    Fire(body);
                    return null;
                case "ability":
                    // Like the ability bar; a "which direction?" prompt is answered with "dir" if given.
                    return Abilities.Use(body);
                case "learn":
                    // Instant, no popups (Advancement.cs); mark the action done right away.
                    Advancement.Learn(body);
                    MarkReadyIfPlayerCanAct();
                    return null;
                case "raise":
                    Advancement.Raise(body);
                    MarkReadyIfPlayerCanAct();
                    return null;
                case "mutate":
                    Advancement.CheckMutate();
                    // Opens the game's "Choose a mutation" popup; Settle reports it as a prompt.
                    GameThread.Post(() =>
                    {
                        Advancement.Mutate();
                        MarkReadyIfPlayerCanAct();
                    });
                    return null;
                case "reload":
                    // CommandReloadEvent (XRLCore.cs:2225); "nothing to reload" etc. show up in the log.
                    Command("CmdReload");
                    return null;
                case "wait":
                    Command("CmdWait");
                    return null;
                case "pickup":
                    // CommandGet picks up what's here; several items opens a picker (XRLCore.cs:2163).
                    Command("CmdGet");
                    return null;
                case "stairs":
                    Command(Stairs(body));
                    return null;
                case "getfrom":
                    // Take from a container (e.g. a chest) in that direction: CommandGetFrom (XRLCore.cs:2169).
                    Command("CmdGetFrom" + Direction(body));
                    return null;
                case "talk":
                {
                    // CmdTalk asks for a direction (PickDirection.ShowPicker, XRLCore.cs:2233), answered in Settle.
                    string dir = Direction(body);
                    Command("CmdTalk");
                    return dir;
                }
                case "choose":
                    // Handled in Run (UI thread); only reaches here if the game wasn't in a popup.
                    throw new ActionRejectedException(409, "no popup is open");
                case "item":
                    ItemAction(body, (string)body["verb"]);
                    return null;
                case "eat":
                case "drink":
                case "equip":
                case "unequip":
                    ItemAction(body, type);
                    return null;
                default:
                    throw new ActionRejectedException(400, "unknown action type \"" + type + "\"",
                        new { types = new[] { "move", "attack", "fire", "reload", "ability", "learn", "raise", "mutate", "wait", "pickup", "getfrom", "stairs", "eat", "drink", "equip", "unequip", "item", "talk", "choose" } });
            }
        }

        // The game's AttackDirection silently does nothing (no message, no time) when the cell has no target it
        // can reach (Combat.AttackCell, GameObject.AttackDirection's swoop check), so say why instead.
        private static void RequireMeleeTarget(string dir)
        {
            GameObject player = The.Player;
            Cell cell = player.CurrentCell?.GetCellFromDirection(dir, BuiltOnly: false);
            if (cell == null)
            {
                throw new ActionRejectedException(409, "no cell in that direction");
            }
            if (cell.GetCombatTarget(player) != null)
            {
                return;
            }
            GameObject flyer = cell.GetCombatTarget(player, IgnoreFlight: true);
            if (flyer != null && !player.IsFlying)
            {
                throw new ActionRejectedException(409, "can't reach " + StateBuilder.Clean(flyer.DisplayNameOnly) + " in melee: it is flying (use \"fire\")");
            }
            throw new ActionRejectedException(409, "nothing to attack there");
        }

        // Fires the equipped missile weapons at a cell, as CmdFire does (XRLCore.cs:1859) but with the target given,
        // which skips the game's targeting UI (Combat.FireMissileWeapon with TargetCell, Combat.cs:409).
        private static void Fire(JObject body)
        {
            GameObject player = The.Player;
            JToken jx = body["x"], jy = body["y"];
            if (jx == null || jy == null || jx.Type != JTokenType.Integer || jy.Type != JTokenType.Integer)
            {
                throw new ActionRejectedException(400, "give the target cell as \"x\" and \"y\" (e.g. a creature's position from state.creatures)");
            }
            Zone zone = player.CurrentZone;
            if (zone == null || zone.IsWorldMap())
            {
                throw new ActionRejectedException(409, "cannot fire on the world map");
            }
            Cell target = zone.GetCell((int)jx, (int)jy);
            if (target == null)
            {
                throw new ActionRejectedException(400, "(" + jx + "," + jy + ") is outside the map");
            }
            if (target == player.CurrentCell)
            {
                throw new ActionRejectedException(400, "that's your own cell");
            }
            List<GameObject> weapons = player.GetMissileWeapons();
            bool any = false, ready = false;
            string notReady = null;
            foreach (GameObject w in weapons ?? new List<GameObject>())
            {
                if (w.TryGetPart<MissileWeapon>(out MissileWeapon mw))
                {
                    any = true;
                    if (mw.ReadyToFire())
                    {
                        ready = true;
                        break;
                    }
                    notReady = notReady ?? mw.GetNotReadyToFireMessage();
                }
            }
            if (!any)
            {
                throw new ActionRejectedException(409, "no missile weapon equipped");
            }
            if (!ready)
            {
                throw new ActionRejectedException(409, StateBuilder.Clean(notReady ?? "you need to reload") + " (use \"reload\")");
            }
            // Own task, like item actions: firing can raise popups (e.g. confirming an attack on a neutral).
            GameThread.Post(() =>
            {
                Combat.FireMissileWeapon(player, null, target);
                MarkReadyIfPlayerCanAct();
            });
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
                        if (part.Equipped != null && string.Equals(StateBuilder.Clean(part.GetCardinalDescription()), slot, StringComparison.OrdinalIgnoreCase))
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

        // Runs on the HTTP thread, after an action or a popup answer. Finishes when one of these happens:
        //  - a new player turn begins, or a command/item action ends with the player still able to act
        //    (the hooks above);
        //  - the game is back in normal play and the player can act (game-thread probe; covers popups that
        //    weren't caused by an action, e.g. the HP warning at the top of PlayerTurn, XRLCore.cs:771);
        //  - a popup is waiting for an answer (UI-thread probe) -> returned so the client can "choose";
        //  - the view stays on a screen we can't read -> "blocked".
        private static object Settle(Started started, int messageCount)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long unreadableSince = -1;
            while (true)
            {
                if (Volatile.Read(ref BeginTurns) != started.BeginTurns || Volatile.Read(ref ReadyMarks) != started.ReadyMarks)
                {
                    return Finish(started, messageCount);
                }
                string screen = GameManager.Instance?.CurrentGameView;
                if (screen != Options.StageViewID)
                {
                    if (started.AutoDirection != null && TryAnswerDirection(started.AutoDirection))
                    {
                        // "talk" etc.: the command asked which way; we were already told.
                        started.AutoDirection = null;
                        unreadableSince = -1;
                        continue;
                    }
                    object prompt = TryDescribePopup();
                    if (prompt != null && started.Answered != null && timer.ElapsedMilliseconds < AnsweredGraceMs
                        && Newtonsoft.Json.JsonConvert.SerializeObject(prompt) == started.Answered)
                    {
                        // Still the prompt we just answered; give it time to close. If it never does (the answer was
                        // refused), it is reported after the grace period so the client sees nothing changed.
                        prompt = null;
                        unreadableSince = -1;
                    }
                    else if (prompt != null)
                    {
                        return new { result = "prompt", screen, log = MessageLog.Since(started.MessageSeq), prompt };
                    }
                    if (screen == "MainMenu" && !(The.Game?.Running ?? false))
                    {
                        // The end screen was closed (or the game otherwise ended): nothing more will happen.
                        return new { result = "ended", screen, log = MessageLog.Since(started.MessageSeq), lastGame = GameOver.Last };
                    }
                    if (unreadableSince < 0)
                    {
                        unreadableSince = timer.ElapsedMilliseconds;
                    }
                    else if (timer.ElapsedMilliseconds - unreadableSince >= BlockedAfterMs)
                    {
                        return new
                        {
                            result = "blocked",
                            screen,
                            log = MessageLog.Since(started.MessageSeq),
                            error = "the game is on a screen this API can't read yet (" + screen + ")",
                        };
                    }
                }
                else
                {
                    unreadableSince = -1;
                    if (timer.ElapsedMilliseconds >= 100 && TryGame(IsPlayerReady))
                    {
                        return Finish(started, messageCount);
                    }
                }
                if (timer.ElapsedMilliseconds >= ActionTimeoutMs)
                {
                    return new { result = "timeout", screen, log = MessageLog.Since(started.MessageSeq), error = "action did not finish within " + ActionTimeoutMs + "ms" };
                }
                Progress.Wait(50);
                Progress.Reset();
            }
        }

        private static bool TryAnswerDirection(string dir)
        {
            try
            {
                return UiThread.Invoke(() =>
                {
                    if (!PopupBridge.IsDirectionPrompt())
                    {
                        return false;
                    }
                    PopupBridge.AnswerDirection(dir);
                    return true;
                }, 500);
            }
            catch (GameThreadTimeoutException)
            {
                return false;
            }
        }

        public static object TryDescribePopup()
        {
            try
            {
                return UiThread.Invoke(() =>
                {
                    return PopupBridge.DescribePrompt();
                }, 500);
            }
            catch (GameThreadTimeoutException)
            {
                return null;
            }
        }

        private static bool TryGame(Func<bool> probe)
        {
            try
            {
                return GameThread.Invoke(probe, 300);
            }
            catch (GameThreadTimeoutException)
            {
                return false;
            }
        }
    }
}
