using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Qud.API;
using XRL;
using XRL.Messages;
using XRL.UI;
using XRL.World;
using XRL.World.Parts;
using XRL.World.Parts.Mutation;
using XRL.World.Parts.Skill;
using XRL.World.Skills;

namespace QudBridge
{
    // Character advancement: GET /character, and the learn / raise / mutate actions. The game only does these from
    // its status screens (unreadable here), so the checks are copied from those screens without their popups:
    // skills from XRL.UI/SkillsAndPowersScreen.cs:141 (SelectNode), attributes from XRL.UI/StatusScreen.cs:62
    // (BuyStat), mutation ranks from XRL.UI/StatusScreen.cs:574 (ShowMutationPopup). All run on the game thread.
    public static class Advancement
    {
        private static readonly string[] Attributes = { "Strength", "Agility", "Toughness", "Intelligence", "Willpower", "Ego" };

        public static object Describe(bool details)
        {
            GameObject player = The.Player;
            var skills = new List<object>();
            foreach (SkillEntry skill in SkillFactory.Factory.SkillList.Values)
            {
                bool owned = skill.Has(player);
                if (skill.Hidden && !owned)
                {
                    continue;
                }
                var powers = new List<object>();
                foreach (PowerEntry power in skill.PowerList)
                {
                    if (power.Hidden && !player.HasPart(power.Class))
                    {
                        continue;
                    }
                    bool powerOwned = player.HasPart(power.Class);
                    string why = powerOwned ? null : WhyNot(player, skill, power);
                    powers.Add(new
                    {
                        name = StateBuilder.Clean(power.Name),
                        cost = power.Cost,
                        owned = powerOwned ? true : (bool?)null,
                        canLearn = !powerOwned && why == null ? true : (bool?)null,
                        why,
                        desc = details ? NullIfEmpty(StateBuilder.Clean(power.Snippet)) : null,
                    });
                }
                string skillWhy = owned ? null : WhyNot(player, skill, null);
                skills.Add(new
                {
                    name = StateBuilder.Clean(skill.Name),
                    cost = skill.Cost,
                    owned = owned ? true : (bool?)null,
                    canLearn = !owned && skillWhy == null ? true : (bool?)null,
                    why = skillWhy,
                    desc = details ? NullIfEmpty(StateBuilder.Clean(skill.Snippet)) : null,
                    powers,
                });
            }
            var mutations = new List<object>();
            foreach (BaseMutation m in StatusScreen.GetMutationList(player))
            {
                mutations.Add(new
                {
                    name = StateBuilder.Clean(m.GetDisplayName()),
                    rank = m.Level,
                    baseRank = m.BaseLevel,
                    canRaise = RankWhyNot(player, m) == null ? true : (bool?)null,
                    desc = details ? NullIfEmpty(StateBuilder.Clean(m.GetDescription())) : null,
                });
            }
            var attributes = new Dictionary<string, int>();
            foreach (string a in Attributes)
            {
                attributes[a] = player.Statistics[a].BaseValue;
            }
            return new
            {
                points = new { sp = player.Stat("SP"), ap = player.Stat("AP"), mp = player.Stat("MP") },
                attributes,
                canMutate = StatusScreen.TreatAsMutant(player),
                mutations,
                skills,
            };
        }

        // learn {"name": skill or power name}
        public static void Learn(JObject body)
        {
            GameObject player = The.Player;
            string name = (string)body["name"];
            if (string.IsNullOrEmpty(name))
            {
                throw new ActionRejectedException(400, "give the skill or power as \"name\" (see GET /character)");
            }
            SkillEntry skill = null;
            PowerEntry power = null;
            foreach (SkillEntry s in SkillFactory.Factory.SkillList.Values)
            {
                if (Matches(s.Name, s.Class, name))
                {
                    skill = s;
                    break;
                }
                foreach (PowerEntry p in s.PowerList)
                {
                    if (Matches(p.Name, p.Class, name))
                    {
                        skill = s;
                        power = p;
                        break;
                    }
                }
                if (power != null)
                {
                    break;
                }
            }
            if (skill == null)
            {
                throw new ActionRejectedException(400, "no skill or power named \"" + name + "\"");
            }
            string cls = power?.Class ?? skill.Class;
            if (player.HasPart(cls))
            {
                throw new ActionRejectedException(409, "you already have " + StateBuilder.Clean(power?.Name ?? skill.Name));
            }
            string why = WhyNot(player, skill, power);
            if (why != null)
            {
                throw new ActionRejectedException(409, why);
            }
            Type type = ModManager.ResolveType("XRL.World.Parts.Skill." + cls);
            if (type == null)
            {
                throw new ActionRejectedException(409, "the game has no implementation for " + cls);
            }
            int cost = power?.Cost ?? skill.Cost;
            player.GetPart<Skills>().AddSkill(Activator.CreateInstance(type) as BaseSkill);
            player.GetStat("SP").Penalty += cost;
            MessageQueue.AddPlayerMessage("You learn " + StateBuilder.Clean(power?.Name ?? skill.Name) + " for " + cost + " SP.");
        }

        // raise {"attribute": "Strength"|"str"} or raise {"mutation": name}
        public static void Raise(JObject body)
        {
            GameObject player = The.Player;
            string attribute = (string)body["attribute"];
            string mutation = (string)body["mutation"];
            if (attribute != null)
            {
                string stat = ResolveAttribute(attribute);
                Statistic s = player.Statistics[stat];
                if (s.BaseValue >= 100)
                {
                    throw new ActionRejectedException(409, "you may not raise an attribute above 100");
                }
                if (player.Stat("AP") < 1)
                {
                    throw new ActionRejectedException(409, "no attribute points");
                }
                player.GetStat("AP").Penalty++;
                s.BaseValue++;
                MessageQueue.AddPlayerMessage("You have increased your " + stat + " to " + s.Value + ".");
                return;
            }
            if (mutation != null)
            {
                BaseMutation m = null;
                foreach (BaseMutation candidate in StatusScreen.GetMutationList(player))
                {
                    if (Matches(candidate.GetDisplayName(), candidate.Name, mutation))
                    {
                        m = candidate;
                        break;
                    }
                }
                if (m == null)
                {
                    throw new ActionRejectedException(400, "you have no mutation named \"" + mutation + "\" (see GET /character)");
                }
                string why = RankWhyNot(player, m);
                if (why != null)
                {
                    throw new ActionRejectedException(409, why);
                }
                player.GetPart<Mutations>().LevelMutation(m, m.BaseLevel + 1);
                player.UseMP(1);
                MessageQueue.AddPlayerMessage("You have increased " + StateBuilder.Clean(m.GetDisplayName()) + "'s base rank to " + m.BaseLevel + ".");
                return;
            }
            throw new ActionRejectedException(400, "give \"attribute\" (e.g. \"Strength\") or \"mutation\" (a name from GET /character)");
        }

        // mutate: buy a new mutation for 4 MP. The game offers a choice of mutations in a popup
        // (StatusScreen.BuyRandomMutation, XRL.UI/StatusScreen.cs:646), which comes back as a prompt to choose from.
        public static void CheckMutate()
        {
            GameObject player = The.Player;
            if (!StatusScreen.TreatAsMutant(player))
            {
                throw new ActionRejectedException(409, "your type of creature can't buy mutations");
            }
            if (player.Stat("MP") < 4)
            {
                throw new ActionRejectedException(409, "buying a mutation costs 4 MP; you have " + player.Stat("MP"));
            }
        }

        public static void Mutate()
        {
            MutationsAPI.BuyRandomMutation(The.Player, Confirm: false);
        }

        // Why the player can't buy this skill (power == null) or power now, or null if they can.
        private static string WhyNot(GameObject player, SkillEntry skill, PowerEntry power)
        {
            if ((power != null && power.IsSkillInitiatory) || (power == null && skill.Initiatory))
            {
                return "must be initiated into this to learn it";
            }
            if (power != null && !skill.Has(player))
            {
                return "requires the " + StateBuilder.Clean(skill.Name) + " skill (" + skill.Cost + " SP) first";
            }
            int cost = power?.Cost ?? skill.Cost;
            if (player.Stat("SP") < cost)
            {
                return "costs " + cost + " SP; you have " + player.Stat("SP");
            }
            if (power != null)
            {
                if (!power.MeetsRequirements(player))
                {
                    return "requirements not met: " + Requirements(player, power);
                }
            }
            else
            {
                if (!skill.MeetsRequirements(player))
                {
                    return "requirements not met";
                }
                foreach (PowerEntry free in skill.Powers.Values)
                {
                    if (free.Cost == 0 && !free.MeetsAttributeMinimum(player))
                    {
                        return "requirements not met: " + Requirements(player, free);
                    }
                }
            }
            return null;
        }

        private static string Requirements(GameObject player, PowerEntry power)
        {
            var sb = new StringBuilder();
            foreach (PowerEntryRequirement r in power.requirements)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" or ");
                }
                r.Render(player, sb);
            }
            if (!string.IsNullOrEmpty(power.Requires))
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("needs ").Append(power.Requires);
            }
            string text = StateBuilder.Clean(sb.ToString()).Trim();
            return text.Length == 0 ? "see the skills screen" : text;
        }

        private static string RankWhyNot(GameObject player, BaseMutation m)
        {
            if (!StatusScreen.TreatAsMutant(player) || m.BaseLevel <= 0)
            {
                return "this mutation's rank can't be bought";
            }
            if (!m.CanIncreaseLevel())
            {
                return "you may not advance this mutation's rank yet";
            }
            if (player.Stat("MP") < 1)
            {
                return "no mutation points";
            }
            return null;
        }

        private static string ResolveAttribute(string name)
        {
            foreach (string a in Attributes)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Substring(0, 3), name, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }
            throw new ActionRejectedException(400, "\"attribute\" must be one of " + string.Join(", ", Attributes));
        }

        private static bool Matches(string display, string cls, string wanted)
        {
            return string.Equals(StateBuilder.Clean(display), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(cls, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
