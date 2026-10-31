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
                        // Always shown, met or not, so a client can plan (the skills screen shows these too).
                        requires = powerOwned ? null : Requires(power),
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
                    requires = owned ? null : SkillRequires(skill),
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

        // Why the player can't buy this skill (power == null) or power now, or null if they can. Lists every
        // failing reason ("costs 100 SP; you have 44; needs Strength 23 (you have 22)"), in SelectNode's order.
        private static string WhyNot(GameObject player, SkillEntry skill, PowerEntry power)
        {
            if ((power != null && power.IsSkillInitiatory) || (power == null && skill.Initiatory))
            {
                return "must be initiated into this to learn it";
            }
            var reasons = new List<string>();
            if (power != null && !skill.Has(player))
            {
                reasons.Add("requires the " + StateBuilder.Clean(skill.Name) + " skill (" + skill.Cost + " SP) first");
            }
            int cost = power?.Cost ?? skill.Cost;
            if (player.Stat("SP") < cost)
            {
                reasons.Add("costs " + cost + " SP; you have " + player.Stat("SP"));
            }
            if (power != null)
            {
                Unmet(player, power, reasons);
                // Anything else the game checks (PowerEntry.MeetsRequirements ends with the skill class's own check).
                if (reasons.Count == 0 && !power.MeetsRequirements(player))
                {
                    reasons.Add("requirements not met");
                }
            }
            else
            {
                // A skill needs its free powers' requirements (SkillEntry.MeetsRequirements; SelectNode also checks
                // their attribute minimums).
                foreach (PowerEntry free in skill.PowerList)
                {
                    if (free.Cost == 0)
                    {
                        Unmet(player, free, reasons);
                    }
                }
                if (reasons.Count == 0 && !skill.MeetsRequirements(player))
                {
                    reasons.Add("requirements not met");
                }
            }
            return reasons.Count == 0 ? null : string.Join("; ", reasons);
        }

        // Adds the unmet parts of a power's requirements (PowerEntry.MeetsRequirements: attribute minimums, where
        // any one "|" group suffices; Exclusion; Requires).
        private static void Unmet(GameObject player, PowerEntry power, List<string> reasons)
        {
            if (!power.MeetsAttributeMinimum(player))
            {
                reasons.Add("needs " + AttributeText(power, player));
            }
            foreach (string name in Names(power.Exclusion))
            {
                if (player.HasSkill(name) || HasMutation(player, name))
                {
                    reasons.Add("can't be learned with " + DisplayName(name));
                }
            }
            foreach (string name in Names(power.Requires))
            {
                if (!player.HasSkill(name) && !HasMutation(player, name))
                {
                    reasons.Add("needs " + DisplayName(name));
                }
            }
        }

        // All of a power's requirements, met or not: "Strength 23", "Agility 19 or Strength 19; needs Swipe".
        private static string Requires(PowerEntry power)
        {
            var parts = new List<string>();
            string attrs = AttributeText(power, null);
            if (attrs.Length > 0)
            {
                parts.Add(attrs);
            }
            foreach (string name in Names(power.Requires))
            {
                parts.Add("needs " + DisplayName(name));
            }
            foreach (string name in Names(power.Exclusion))
            {
                parts.Add("not with " + DisplayName(name));
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        private static string SkillRequires(SkillEntry skill)
        {
            var parts = new List<string>();
            foreach (PowerEntry free in skill.PowerList)
            {
                if (free.Cost == 0)
                {
                    string r = Requires(free);
                    if (r != null)
                    {
                        parts.Add(r);
                    }
                }
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        // "Strength 23" / "Agility 19 or Strength 19"; with player, adds what they have: "Strength 23 (you have 22)".
        private static string AttributeText(PowerEntry power, GameObject player)
        {
            var groups = new List<string>();
            foreach (PowerEntryRequirement r in power.requirements)
            {
                var terms = new List<string>();
                for (int i = 0; i < r.Attributes.Count; i++)
                {
                    if (r.Minimums[i] <= 0)
                    {
                        continue; // filler in the data ("Strength 0"); always met
                    }
                    string t = r.Attributes[i] + " " + r.Minimums[i];
                    if (player != null)
                    {
                        t += " (you have " + player.BaseStat(r.Attributes[i]) + ")";
                    }
                    terms.Add(t);
                }
                if (terms.Count > 0)
                {
                    groups.Add(string.Join(", ", terms));
                }
            }
            return string.Join(" or ", groups);
        }

        private static IEnumerable<string> Names(string list)
        {
            if (string.IsNullOrEmpty(list))
            {
                yield break;
            }
            foreach (string name in list.Split(','))
            {
                string n = name.Trim();
                if (n.Length > 0)
                {
                    yield return n;
                }
            }
        }

        private static bool HasMutation(GameObject player, string name)
        {
            return MutationFactory.TryGetMutationEntry(name, out MutationEntry entry) && player.HasPart(entry.Class);
        }

        private static string DisplayName(string name)
        {
            if (SkillFactory.Factory.TryGetFirstEntry(name, out var entry))
            {
                return StateBuilder.Clean(entry.Name);
            }
            if (MutationFactory.TryGetMutationEntry(name, out MutationEntry m))
            {
                return StateBuilder.Clean(m.GetDisplayName());
            }
            return name;
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
