using System;
using System.Collections.Generic;
using XRL;
using XRL.Core;
using XRL.Rules;
using XRL.UI;
using XRL.World;
using XRL.World.Anatomy;
using XRL.World.Parts;

namespace QudBridge
{
    public class NoGameException : Exception
    {
        public NoGameException(string message) : base(message) { }
    }

    // Builds the GET /state snapshot. Must run on the game thread (via GameThread.Invoke). Read-only:
    // avoids GameObject.ID, which assigns and stores an id on first read (XRL.World/GameObject.cs:436).
    public static class StateBuilder
    {
        public const int DefaultMessages = 15;
        public const int MaxListed = 40;

        // Creature keys on the map, in list order (nearest first).
        private const string CreatureKeys = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public static object Build(int messageCount, bool includeLegend)
        {
            XRLGame game = The.Game;
            GameObject player = The.Player;
            string screen = GameManager.Instance?.CurrentGameView;
            if (game == null || !game.Running || player == null)
            {
                throw new NoGameException("no game in progress (screen: " + (screen ?? "unknown") + ")");
            }
            Cell playerCell = player.CurrentCell;
            Zone zone = playerCell?.ParentZone;

            var creatures = new List<GameObject>();
            var items = new List<GameObject>();
            var features = new List<GameObject>();
            Dictionary<string, string> worldLegend = null;
            string[] rows = null;
            if (zone != null && zone.IsWorldMap())
            {
                rows = BuildWorldMap(zone, player, out worldLegend);
            }
            else if (zone != null)
            {
                rows = BuildMap(zone, player, creatures, items, features);
            }

            return new
            {
                // GameManager.CurrentGameView (GameManager.cs:469); "Stage" (Options.StageViewID) is normal play.
                screen,
                turn = game.Turns,
                time = Calendar.GetTime(),
                player = BuildPlayer(player, zone),
                map = rows == null ? null : new
                {
                    w = zone.Width,
                    h = zone.Height,
                    // The world map's symbols are assigned per response, so its legend is always included.
                    legend = worldLegend ?? (includeLegend ? Legend : null),
                    rows,
                },
                creatures = BuildCreatures(creatures, player),
                items = items.ConvertAll(i => (object)new
                {
                    name = Clean(i.DisplayName),
                    x = i.CurrentCell.X,
                    y = i.CurrentCell.Y,
                    dist = i.DistanceTo(player),
                }),
                features = features.ConvertAll(f => (object)new
                {
                    name = Clean(f.DisplayName),
                    x = f.CurrentCell.X,
                    y = f.CurrentCell.Y,
                }),
                inventory = BuildInventory(player),
                equipped = BuildEquipped(player),
                messages = BuildMessages(game, messageCount),
            };
        }

        private static object BuildPlayer(GameObject player, Zone zone)
        {
            Cell cell = player.CurrentCell;
            Stomach stomach = player.GetPart<Stomach>();
            int level = player.Level;
            return new
            {
                // Same name source as the status bar (Qud.UI/PlayerStatusBar.cs:328).
                name = Clean(player.DisplayNameOnlyDirect),
                hp = player.hitpoints,
                maxHp = player.baseHitpoints,
                level,
                xp = player.Stat("XP"),
                // Leveler.GetXPForLevel (XRL.World.Parts/Leveler.cs:98), as the XP bar does (PlayerStatusBar.cs:448).
                xpNext = Leveler.GetXPForLevel(level + 1),
                stats = new
                {
                    str = player.Stat("Strength"),
                    agi = player.Stat("Agility"),
                    tou = player.Stat("Toughness"),
                    @int = player.Stat("Intelligence"),
                    wil = player.Stat("Willpower"),
                    ego = player.Stat("Ego"),
                    // XRL.Rules/Stats.cs:20/39
                    av = Stats.GetCombatAV(player),
                    dv = Stats.GetCombatDV(player),
                    qn = player.Stat("Speed"),
                    ms = player.Stat("MoveSpeed"),
                },
                // Unspent skill / attribute / mutation points (spend with learn / raise / mutate; see GET /character).
                points = new { sp = player.Stat("SP"), ap = player.Stat("AP"), mp = player.Stat("MP") },
                abilities = Abilities.Describe(player),
                // Stomach.FoodStatus/WaterStatus (XRL.World.Parts/Stomach.cs:87/104).
                food = Clean(stomach?.FoodStatus()),
                water = Clean(stomach?.WaterStatus()),
                weight = player.GetCarriedWeight(),
                maxWeight = player.GetMaxCarriedWeight(),
                zone = zone == null ? null : new
                {
                    id = zone.ZoneID,
                    name = Clean(zone.DisplayName),
                    worldMap = zone.IsWorldMap(),
                },
                x = cell?.X,
                y = cell?.Y,
                effects = BuildEffects(player),
            };
        }

        // Same filter as the stage's active-effects line (Qud.UI/AbilityBar.cs:295-312).
        private static List<string> BuildEffects(GameObject player)
        {
            var list = new List<string>();
            foreach (Effect effect in player.Effects)
            {
                if (effect == null || effect.SuppressInStageDisplay())
                {
                    continue;
                }
                string description = effect.GetDescription();
                if (string.IsNullOrEmpty(description))
                {
                    continue;
                }
                description = Clean(description);
                if (effect.Duration > 0 && effect.Duration != Effect.DURATION_INDEFINITE)
                {
                    description += " (" + effect.Duration + ")";
                }
                list.Add(description);
            }
            return list;
        }

        public static readonly Dictionary<string, string> Legend = new Dictionary<string, string>
        {
            { "@", "you" },
            { "a-zA-Z", "creature (key in creatures)" },
            { "?", "creature (too many to list)" },
            { ".", "floor (in view)" },
            { ",", "floor (remembered, not in view)" },
            { " ", "unexplored" },
            { "#", "wall or other solid terrain" },
            { "+", "closed door" },
            { "'", "open door" },
            { "<", "stairs up" },
            { ">", "stairs down" },
            { "~", "liquid" },
            { "*", "item(s) (see items)" },
            { "&", "furniture/container/other object (see features)" },
        };

        // One char per cell. "In view" mirrors what the renderer shows: the cell is in line of sight
        // (Cell.IsVisible, XRL.World/Cell.cs:3490) and lit (Cell.IsLit, Cell.cs:3485); objects additionally
        // pass GameObject.IsVisible (GameObject.cs:10465), which handles hidden/invisible things.
        // Remembered cells only show static terrain.
        private static string[] BuildMap(Zone zone, GameObject player, List<GameObject> creatures,
            List<GameObject> items, List<GameObject> features)
        {
            int w = zone.Width, h = zone.Height;
            var grid = new char[h][];
            for (int y = 0; y < h; y++)
            {
                grid[y] = new char[w];
                for (int x = 0; x < w; x++)
                {
                    Cell cell = zone.GetCell(x, y);
                    if (cell == null || !cell.IsExplored())
                    {
                        grid[y][x] = ' ';
                    }
                    else if (cell.IsVisible() && cell.IsLit())
                    {
                        grid[y][x] = InViewChar(cell, player, creatures, items, features);
                    }
                    else
                    {
                        grid[y][x] = RememberedChar(cell);
                    }
                }
            }

            // Nearest first, so keys and the item/feature lists favour what matters; then cap list sizes.
            Comparison<GameObject> byDistance = (a, b) => a.DistanceTo(player).CompareTo(b.DistanceTo(player));
            creatures.Sort(byDistance);
            items.Sort(byDistance);
            features.Sort(byDistance);
            // Iterate backwards so where creatures share a cell, the nearest one's key is shown.
            for (int i = creatures.Count - 1; i >= 0; i--)
            {
                Cell c = creatures[i].CurrentCell;
                grid[c.Y][c.X] = i < CreatureKeys.Length ? CreatureKeys[i] : '?';
            }
            Truncate(creatures, CreatureKeys.Length);
            Truncate(items, MaxListed);
            Truncate(features, MaxListed);

            var rows = new string[h];
            for (int y = 0; y < h; y++)
            {
                rows[y] = new string(grid[y]);
            }
            return rows;
        }

        // Symbols for world-map terrain, handed out most-common-first. Excludes '@', space, and '<' '>' '#'
        // (stairs and walls on the local map).
        private const string WorldSymbols = ".,:;'\"`~^-=+%&$!|/\\()[]{}*?0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        // On the world map each cell holds one terrain object: a blueprint inheriting "Terrain"
        // (ObjectBlueprints/WorldTerrain.xml:11), e.g. "salt marsh", "River Svy", "Joppa". Regions and named
        // places are both terrain (both can be proper nouns), so every distinct name gets its own symbol.
        private static string[] BuildWorldMap(Zone zone, GameObject player, out Dictionary<string, string> legend)
        {
            int w = zone.Width, h = zone.Height;
            var names = new string[h, w];
            var counts = new Dictionary<string, int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Cell cell = zone.GetCell(x, y);
                    string name = null;
                    if (cell != null && cell.IsExplored())
                    {
                        foreach (GameObject obj in cell.Objects)
                        {
                            if (obj.GetBlueprint(UseDefault: false)?.InheritsFrom("Terrain") == true)
                            {
                                name = Clean(obj.DisplayNameOnly);
                                break;
                            }
                        }
                        name = name ?? "unknown";
                    }
                    names[y, x] = name;
                    if (name != null)
                    {
                        counts.TryGetValue(name, out int n);
                        counts[name] = n + 1;
                    }
                }
            }
            var order = new List<string>(counts.Keys);
            order.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : string.CompareOrdinal(a, b));
            var symbol = new Dictionary<string, char>();
            legend = new Dictionary<string, string> { { "@", "you" }, { " ", "unexplored" } };
            for (int i = 0; i < order.Count; i++)
            {
                // More terrain kinds than symbols is not expected; the overflow shares '?'.
                char c = i < WorldSymbols.Length ? WorldSymbols[i] : '?';
                symbol[order[i]] = c;
                string key = c.ToString();
                legend[key] = legend.TryGetValue(key, out string existing) ? existing + ", " + order[i] : order[i];
            }
            Cell here = player.CurrentCell;
            if (here != null && names[here.Y, here.X] != null)
            {
                // '@' hides the tile underneath (often a named place, e.g. Joppa), so name it.
                legend["@"] = "you (on " + names[here.Y, here.X] + ")";
            }
            var rows = new string[h];
            var line = new char[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    line[x] = here != null && here.X == x && here.Y == y ? '@'
                        : names[y, x] == null ? ' ' : symbol[names[y, x]];
                }
                rows[y] = new string(line);
            }
            return rows;
        }

        private static char InViewChar(Cell cell, GameObject player, List<GameObject> creatures,
            List<GameObject> items, List<GameObject> features)
        {
            // Highest priority wins: @ > creature > stairs > door > item > feature > wall > liquid > floor.
            int best = 0;
            char ch = '.';
            bool hasItem = false;
            foreach (GameObject obj in cell.Objects)
            {
                if (obj.IsPlayer())
                {
                    Take(ref best, ref ch, 9, '@');
                    continue;
                }
                if (!obj.IsVisible())
                {
                    continue;
                }
                if (obj.IsCombat)
                {
                    // Placeholder; replaced by the creature's key once the list is sorted.
                    creatures.Add(obj);
                    Take(ref best, ref ch, 8, 'c');
                }
                else if (obj.HasPart<StairsUp>())
                {
                    Take(ref best, ref ch, 7, '<');
                }
                else if (obj.HasPart<StairsDown>())
                {
                    Take(ref best, ref ch, 7, '>');
                }
                else if (obj.IsDoor())
                {
                    Door door = obj.GetPart<Door>();
                    Take(ref best, ref ch, 6, door != null && door.Open ? '\'' : '+');
                }
                else if (obj.IsWall())
                {
                    Take(ref best, ref ch, 3, '#');
                }
                else if (IsFeature(obj))
                {
                    features.Add(obj);
                    Take(ref best, ref ch, 4, '&');
                }
                else if (obj.Takeable)
                {
                    items.Add(obj);
                    hasItem = true;
                }
                else if (obj.IsOpenLiquidVolume())
                {
                    Take(ref best, ref ch, 2, '~');
                }
            }
            if (hasItem)
            {
                Take(ref best, ref ch, 5, '*');
            }
            return ch;
        }

        // Furniture (chests, hammocks, signs: takeable but not loot; ObjectBlueprints/Furniture.xml:36),
        // containers, and solid non-wall terrain such as statues and machines.
        private static bool IsFeature(GameObject obj)
        {
            return obj.GetBlueprint(UseDefault: false)?.InheritsFrom("Furniture") == true
                || obj.Inventory != null
                || obj.Physics != null && obj.Physics.Solid;
        }

        // Display strings embed CP437 control-range glyphs for item stats (e.g. "lead slug x251 \u001a4 \u00031d2").
        // Spell the stat ones out; map any other control char to its CP437 glyph. Newlines are kept.
        private const string Cp437Low = " ☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼";

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            s = StripMarkup(s);
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\u001a': sb.Append("PV"); break;  // → penetration
                    case '\u0003': sb.Append("DMG"); break; // ♥ damage
                    case '\u0004': sb.Append("AV"); break;  // ♦ armor value
                    case '\t': sb.Append("DV"); break;      // ○ dodge value
                    case '\n': sb.Append(c); break;
                    case '\r': break;                       // conversation text uses \r\n
                    default: sb.Append(c < ' ' ? Cp437Low[c] : c); break;
                }
            }
            return sb.ToString();
        }

        // Removes Qud markup, following ColorUtility.GetNext (ConsoleLib.Console/ColorUtility.cs): "{{spec|text}}"
        // (nestable), "&X" foreground and "^X" background codes, with "&&"/"^^" as literal characters. Our own
        // copy because ColorUtility.StripFormatting shares one static StringBuilder (ColorUtility.cs:179) and
        // isn't safe to call off the game's threads. One addition: a "{{[W|" spec draws a '[' (hotkey labels,
        // e.g. "{{[W|Tab]}} Trade", Qud.UI/PopupMessage.cs:697), which StripFormatting drops.
        private static string StripMarkup(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int depth = 0;
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (i + 1 < n && c == '{' && text[i + 1] == '{')
                {
                    depth++;
                    i += 2;
                    if (i < n && text[i] == '[')
                    {
                        sb.Append('[');
                    }
                    int bar = text.IndexOf('|', i);
                    i = bar < 0 ? n : bar + 1;
                    continue;
                }
                if (depth > 0 && i + 1 < n && c == '}' && text[i + 1] == '}')
                {
                    depth--;
                    i += 2;
                    continue;
                }
                if ((c == '&' || c == '^') && i + 1 < n)
                {
                    if (text[i + 1] == c)
                    {
                        sb.Append(c);
                    }
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static char RememberedChar(Cell cell)
        {
            char ch = ',';
            foreach (GameObject obj in cell.Objects)
            {
                if (obj.HasPart<StairsUp>())
                {
                    return '<';
                }
                if (obj.HasPart<StairsDown>())
                {
                    return '>';
                }
                if (obj.IsDoor())
                {
                    Door door = obj.GetPart<Door>();
                    ch = door != null && door.Open ? '\'' : '+';
                }
                else if (ch == ',' && obj.IsWall())
                {
                    ch = '#';
                }
            }
            return ch;
        }

        private static void Take(ref int best, ref char ch, int priority, char candidate)
        {
            if (priority > best)
            {
                best = priority;
                ch = candidate;
            }
        }

        private static List<object> BuildCreatures(List<GameObject> creatures, GameObject player)
        {
            var list = new List<object>();
            for (int i = 0; i < creatures.Count; i++)
            {
                GameObject c = creatures[i];
                list.Add(new
                {
                    key = CreatureKeys[i].ToString(),
                    name = Clean(c.DisplayNameOnly),
                    x = c.CurrentCell.X,
                    y = c.CurrentCell.Y,
                    dist = c.DistanceTo(player),
                    attitude = Attitude(c, player),
                    // Strings.WoundLevel (XRL.Rules/Strings.cs:83): exact HP only with scanning, else "Wounded" etc.
                    health = NullIfEmpty(Clean(Strings.WoundLevel(c))),
                    // Flyers can't be hit in melee by a non-flyer (Cell.GetCombatTarget skips them); shoot them.
                    flying = c.IsFlying ? true : (bool?)null,
                });
            }
            return list;
        }

        private static string Attitude(GameObject creature, GameObject player)
        {
            if (creature.IsHostileTowards(player))
            {
                return "hostile";
            }
            if (creature.IsPlayerLed() || creature.IsAlliedTowards(player))
            {
                return "friendly";
            }
            return "neutral";
        }

        // Inventory.Objects (XRL.World.Parts/Inventory.cs:21): carried, not equipped. Names as the inventory
        // screen shows them (XRL.UI/InventoryScreen.cs:421), uncoloured. Index is the position in this list.
        private static List<object> BuildInventory(GameObject player)
        {
            var list = new List<object>();
            Inventory inventory = player.Inventory;
            if (inventory == null)
            {
                return list;
            }
            for (int i = 0; i < inventory.Objects.Count; i++)
            {
                list.Add(new { i, name = Clean(inventory.Objects[i].DisplayName) });
            }
            return list;
        }

        // Body parts with something equipped, slot labelled as on the equipment screen
        // (XRL.UI/EquipmentScreen.cs:228). Multi-slot items appear once, at their first slot.
        private static List<object> BuildEquipped(GameObject player)
        {
            var list = new List<object>();
            Body body = player.Body;
            if (body == null)
            {
                return list;
            }
            foreach (BodyPart part in body.GetParts())
            {
                if (part.Equipped != null && part.IsFirstSlotForEquipped())
                {
                    list.Add(new { slot = Clean(part.GetCardinalDescription()), name = Clean(part.Equipped.DisplayName) });
                }
            }
            return list;
        }

        // GamePlayer.Messages (XRL.World/GamePlayer.cs:18) holds every message line, oldest first.
        private static List<string> BuildMessages(XRLGame game, int count)
        {
            var all = game.Player.Messages.Messages;
            var list = new List<string>();
            for (int i = Math.Max(0, all.Count - count); i < all.Count; i++)
            {
                list.Add(Clean(all[i]));
            }
            return list;
        }

        private static void Truncate(List<GameObject> list, int max)
        {
            if (list.Count > max)
            {
                list.RemoveRange(max, list.Count - max);
            }
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
