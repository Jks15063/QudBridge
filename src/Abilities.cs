using System;
using System.Collections.Generic;
using ConsoleLib.Console;
using Newtonsoft.Json.Linq;
using XRL;
using XRL.World;
using XRL.World.Parts;

namespace QudBridge
{
    // Activated abilities (skills' and mutations' active powers): listed in /state, used with the "ability"
    // action. Used the way the ability bar does: Keyboard.PushMouseEvent("Command:" + command)
    // (Qud.UI/AbilityBarButton.cs:91), which PlayerTurn passes to CommandEvent.Send (XRLCore.cs:2341).
    // Game thread only.
    public static class Abilities
    {
        public static List<object> Describe(GameObject player)
        {
            var list = new List<object>();
            ActivatedAbilities abilities = player.ActivatedAbilities;
            if (abilities == null)
            {
                return list;
            }
            // Everything the ability bar shows (Qud.UI/AbilityBar.cs:334); it doesn't filter on Visible, which is off by default.
            foreach (ActivatedAbilityEntry a in abilities.GetAbilityListOrderedByPreference())
            {
                bool usable = a.IsUsable;
                list.Add(new
                {
                    name = StateBuilder.Clean(a.DisplayName),
                    // Cooldown in rounds (game turns), as the ability bar shows it.
                    cooldown = a.Cooldown > 0 ? a.CooldownRounds : (int?)null,
                    on = a.Toggleable ? a.ToggleState : (bool?)null,
                    attack = a.IsAttack ? true : (bool?)null,
                    // Why it can't be used now, other than cooldown (e.g. "Sprint can't be used at this time.").
                    why = !usable && a.Cooldown <= 0 ? StateBuilder.Clean(a.NotUsableDescription) : null,
                });
            }
            return list;
        }

        // ability {"name": display name or command, "dir"?: direction for abilities that ask "which way?"}.
        // Returns the direction to give the direction prompt, if any (answered in Settle, as for "talk").
        public static string Use(JObject body)
        {
            GameObject player = The.Player;
            string name = (string)body["name"];
            if (string.IsNullOrEmpty(name))
            {
                throw new ActionRejectedException(400, "give the ability as \"name\" (see state.player.abilities)");
            }
            ActivatedAbilityEntry found = null;
            var names = new List<string>();
            foreach (ActivatedAbilityEntry a in player.ActivatedAbilities?.GetAbilityListOrderedByPreference() ?? new List<ActivatedAbilityEntry>())
            {
                string display = StateBuilder.Clean(a.DisplayName);
                names.Add(display);
                if (string.Equals(display, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Command, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = a;
                }
            }
            if (found == null)
            {
                throw new ActionRejectedException(400, "no ability named \"" + name + "\"", new { abilities = names });
            }
            if (player.OnWorldMap() && !found.IsWorldMapUsable)
            {
                throw new ActionRejectedException(409, "you cannot do that on the world map");
            }
            if (!found.IsUsable)
            {
                throw new ActionRejectedException(409, StateBuilder.Clean(found.NotUsableDescription) ?? "can't be used right now");
            }
            string dir = null;
            if (body["dir"] != null)
            {
                dir = ((string)body["dir"]).ToUpperInvariant();
                if (Array.IndexOf(new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "SELF" }, dir) < 0)
                {
                    throw new ActionRejectedException(400, "\"dir\" must be one of N,S,E,W,NE,NW,SE,SW or self");
                }
            }
            Keyboard.PushMouseEvent("Command:" + found.Command);
            return dir;
        }
    }
}
