using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using XRL;
using XRL.UI;
using XRL.World;
using XRL.World.Parts;

namespace QudBridge
{
    // look {"x","y"}: what the looker (the "l" key) shows for the visible objects on a cell: the tooltip text of
    // Look.GenerateTooltipInformation (XRL.UI/Look.cs:254), the same text the hover tooltip and the "look" item
    // action show. Takes no time. Game thread only.
    public static class Looking
    {
        private static readonly LookSorter Sorter = new LookSorter();

        // The objects the looker can show on this cell, in its order (Look.QueueLookerTooltip, Look.cs:330):
        // ones with a Description that the player can see (in a solid cell, e.g. a wall, only what's
        // reachable in it).
        private static List<GameObject> Lookable(Cell cell, GameObject player)
        {
            bool solid = cell.IsSolid();
            var list = new List<GameObject>(cell.Objects);
            list.Sort(Sorter);
            return list.FindAll(o => o.HasPart<Description>() && o.IsVisible()
                && (!solid || o.CanInteractInCellWithSolid(player)));
        }

        // Returns the descriptions; the events the game fires after a look (secrets and observations noted,
        // which can show a popup) are posted as a separate task, so Settle reports any popup they raise.
        public static List<object> At(JObject body)
        {
            GameObject player = The.Player;
            JToken jx = body["x"], jy = body["y"];
            if (jx == null || jy == null || jx.Type != JTokenType.Integer || jy.Type != JTokenType.Integer)
            {
                throw new ActionRejectedException(400, "give the cell as \"x\" and \"y\" (e.g. a creature's position from state.creatures)");
            }
            Zone zone = player.CurrentZone;
            Cell cell = zone?.GetCell((int)jx, (int)jy);
            if (cell == null)
            {
                throw new ActionRejectedException(400, "(" + jx + "," + jy + ") is outside the map");
            }
            List<GameObject> objects = Lookable(cell, player);
            if (objects.Count == 0)
            {
                throw new ActionRejectedException(409, cell.IsVisible() && cell.IsLit()
                    ? "there's nothing there to look at" : "you can't see that cell");
            }
            var result = new List<object>();
            foreach (GameObject o in objects)
            {
                Look.TooltipInformation info = Look.GenerateTooltipInformation(o);
                result.Add(new
                {
                    name = StateBuilder.Clean(info.DisplayName),
                    // Creatures only: "Hostile"/"Neutral"/"Friendly" and the game's difficulty rating
                    // ("Average", "Tough", "Impossible", ...) relative to the player.
                    feeling = NullIfEmpty(StateBuilder.Clean(info.FeelingText)),
                    difficulty = NullIfEmpty(StateBuilder.Clean(info.DifficultyText)),
                    health = NullIfEmpty(StateBuilder.Clean(info.WoundLevel)),
                    description = NullIfEmpty(StateBuilder.Clean(info.LongDescription)?.Trim()),
                });
            }
            // As the looker does when its tooltip closes (Look.cs:300).
            ActionHandler.Posted(() =>
            {
                foreach (GameObject o in objects)
                {
                    GameObject obj = o;
                    if (GameObject.Validate(ref obj))
                    {
                        obj.FireEvent(Event.New("AfterLookedAt", "Looker", player));
                        player.FireEvent(Event.New("LookedAt", "Object", obj));
                    }
                }
            });
            return result;
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
