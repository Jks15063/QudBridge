using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using XRL;
using XRL.UI;
using XRL.World;
using XRL.World.Anatomy;
using XRL.World.Capabilities;
using XRL.World.Parts;
using XRL.World.Tinkering;

namespace QudBridge
{
    // Tinkering without the tinkering tab of the status screens (Qud.UI/TinkeringStatusScreen.cs): GET /tinkering
    // lists the bits and known recipes the way the tab does, and tinker {build} / {mod, item|slot} runs the same
    // TinkeringScreen.PerformUITinkerBuild / PerformUITinkerMod the tab calls (TinkeringStatusScreen.cs:454).
    // Game thread only.
    public static class Tinkering
    {
        public static object Describe()
        {
            GameObject player = The.Player;
            BitLocker locker = Locker(player);
            var bits = new Dictionary<string, int>();
            if (locker != null)
            {
                foreach (char c in BitType.BitOrder)
                {
                    int n = locker.GetBitCount(c);
                    if (n > 0)
                    {
                        bits[BitName(c)] = n;
                    }
                }
            }
            var build = new List<object>();
            var mods = new List<object>();
            foreach (TinkerData data in TinkerData.KnownRecipes)
            {
                if (data.Type == "Build")
                {
                    BitCost cost = BuildCost(player, data);
                    build.Add(new
                    {
                        name = Name(data),
                        tier = data.Tier,
                        cost = Cost(cost),
                        ingredient = NullIfEmpty(IngredientText(data)),
                        why = BuildWhyNot(player, data, locker, cost),
                    });
                }
                else if (data.Type == "Mod")
                {
                    var targets = new List<object>();
                    foreach (GameObject item in ModTargets(player, data))
                    {
                        BodyPart part = player.FindEquippedObject(item);
                        targets.Add(new
                        {
                            item = StateBuilder.ItemId(item),
                            slot = part == null ? null : StateBuilder.Clean(part.GetCardinalDescription()),
                            name = StateBuilder.Clean(item.DisplayName),
                            cost = Cost(ModCost(data, item)),
                        });
                    }
                    mods.Add(new
                    {
                        name = Name(data),
                        tier = data.Tier,
                        ingredient = NullIfEmpty(IngredientText(data)),
                        // Items in your inventory or equipped that this mod can go on, as the tab lists them.
                        targets,
                    });
                }
            }
            return new
            {
                // The skill the tab needs; without it the game won't let you build or mod (Tinkering_Tinker1 etc.).
                skill = player.HasSkill("Tinkering_Tinker3") ? "Tinker III" : player.HasSkill("Tinkering_Tinker2") ? "Tinker II"
                    : player.HasSkill("Tinkering_Tinker1") ? "Tinker I" : null,
                bits,
                build,
                mods,
            };
        }

        // tinker {"build": name} or {"mod": name, "item": i | "slot": s}. Validates here so mistakes come back as
        // errors; the game's own checks (bits, ingredient, hostiles nearby) still run and show their popups.
        public static void Tinker(JObject body)
        {
            GameObject player = The.Player;
            string buildName = (string)body["build"], modName = (string)body["mod"];
            if (buildName == null && modName == null)
            {
                throw new ActionRejectedException(400, "give \"build\" (a recipe from GET /tinkering build[].name) or \"mod\" plus \"item\"/\"slot\"");
            }
            if (buildName != null)
            {
                TinkerData data = Find("Build", buildName);
                ActionHandler.Posted(() =>
                {
                    TinkeringScreen.PerformUITinkerBuild(player, data, new Event("InterfaceTest"));
                });
                return;
            }
            TinkerData mod = Find("Mod", modName);
            GameObject target = ActionHandler.ResolveItem(player, body);
            if (!ModTargets(player, mod).Contains(target))
            {
                throw new ActionRejectedException(409, Name(mod) + " can't be applied to " + StateBuilder.Clean(target.DisplayName)
                    + " (see GET /tinkering mods[].targets)");
            }
            BitCost modCost = ModCost(mod, target);
            ActionHandler.Posted(() =>
            {
                bool didMod = false;
                TinkeringScreen.PerformUITinkerMod(player, target, mod, modCost, new Event("InterfaceTest"), ref didMod);
            });
        }

        private static TinkerData Find(string type, string name)
        {
            var names = new List<string>();
            foreach (TinkerData data in TinkerData.KnownRecipes)
            {
                if (data.Type != type)
                {
                    continue;
                }
                string n = Name(data);
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals(data.Blueprint, name, StringComparison.OrdinalIgnoreCase))
                {
                    return data;
                }
                names.Add(n);
            }
            throw new ActionRejectedException(400, "no known " + type.ToLowerInvariant() + " recipe \"" + name + "\"", new { known = names });
        }

        // Same as the tab (UpdateBitlocker, TinkeringStatusScreen.cs:419).
        private static BitLocker Locker(GameObject player)
        {
            return player.GetPart<XRL.World.Parts.Skill.Tinkering>() == null ? player.GetPart<BitLocker>() : player.RequirePart<BitLocker>();
        }

        // Inventory and equipped items with a mod slot the recipe fits (TinkeringStatusScreen.cs:280).
        private static List<GameObject> ModTargets(GameObject player, TinkerData data)
        {
            var list = new List<GameObject>();
            Action<GameObject> check = obj =>
            {
                string key = ItemModding.ModKey(obj);
                if (key != null && obj.Understood() && data.CanMod(key) && ItemModding.ModificationApplicable(data.PartName, obj, player))
                {
                    list.Add(obj);
                }
            };
            player.Inventory?.ForeachObject(check);
            player.Body?.ForeachEquippedObject(check);
            return list;
        }

        // TinkeringLineData.cost (Qud.UI/TinkeringLineData.cs:80).
        private static BitCost BuildCost(GameObject player, TinkerData data)
        {
            var cost = new BitCost();
            cost.Import(TinkerItem.GetBitCostFor(data.Blueprint));
            ModifyBitCostEvent.Process(player, cost, "Build");
            return cost;
        }

        private static BitCost ModCost(TinkerData data, GameObject item)
        {
            var cost = new BitCost();
            cost.Increment(BitType.TierBits[Tier.Constrain(data.Tier)]);
            cost.Increment(BitType.TierBits[Tier.Constrain(item.GetModificationSlotsUsed() - item.GetIntProperty("NoCostMods") + item.GetTechTier())]);
            ModifyBitCostEvent.Process(item, cost, "Mod");
            return cost;
        }

        // The first check PerformUITinkerBuild would fail (TinkeringScreen.cs:751), except hostiles nearby.
        private static string BuildWhyNot(GameObject player, TinkerData data, BitLocker locker, BitCost cost)
        {
            if (!player.HasSkill(DataDisk.GetRequiredSkill(data.Tier)))
            {
                return "needs " + DataDisk.GetRequiredSkillHumanReadable(data.Tier);
            }
            if (!string.IsNullOrEmpty(data.Ingredient))
            {
                bool found = false;
                foreach (string bp in data.Ingredient.Split(','))
                {
                    if (player.Inventory.FindObjectByBlueprint(bp) != null)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return "missing ingredient";
                }
            }
            if (locker == null || !locker.HasBits(cost))
            {
                return "not enough bits";
            }
            return null;
        }

        private static string IngredientText(TinkerData data)
        {
            if (string.IsNullOrEmpty(data.Ingredient))
            {
                return null;
            }
            var names = new List<string>();
            foreach (string bp in data.Ingredient.Split(','))
            {
                names.Add(StateBuilder.Clean(TinkeringHelpers.TinkeredItemShortDisplayName(bp)));
            }
            return string.Join(" or ", names);
        }

        private static Dictionary<string, int> Cost(BitCost cost)
        {
            var result = new Dictionary<string, int>();
            foreach (var kv in cost)
            {
                if (kv.Value > 0)
                {
                    result[BitName(kv.Key)] = kv.Value;
                }
            }
            return result;
        }

        private static string BitName(char c)
        {
            return BitType.BitMap.TryGetValue(c, out BitType t) ? StateBuilder.Clean(t.Description) : c.ToString();
        }

        private static string Name(TinkerData data)
        {
            return StateBuilder.Clean(data.Type == "Build" ? data.LongDisplayName : data.DisplayName);
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
