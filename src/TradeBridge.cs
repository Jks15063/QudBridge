using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Qud.UI;
using XRL;
using XRL.UI;

namespace QudBridge
{
    // Reads and drives the modern trade screen (Qud.UI/TradeScreen.cs), shown under view "ModernTrade" for
    // trading (TradeUI.ShowTradeScreen) and for storing into containers (TradeScreenMode.Container). Side 0
    // is the trader's/container's goods, side 1 the player's (TradeUI.UpdateTotals, XRL.UI/TradeUI.cs:308).
    // UI thread only, like PopupBridge.
    public static class TradeBridge
    {
        public static TradeScreen Screen()
        {
            if (GameManager.Instance?.CurrentGameView != "ModernTrade")
            {
                return null;
            }
            TradeScreen screen = SingletonWindowBase<TradeScreen>.instance;
            return screen != null && screen.isActiveAndEnabled && screen.tradeEntries != null ? screen : null;
        }

        public static object Describe(TradeScreen screen)
        {
            return new
            {
                kind = "trade",
                mode = screen.mode.ToString().ToLowerInvariant(),
                trader = StateBuilder.Clean(TradeScreen.Trader?.DisplayNameOnly),
                traderDrams = TradeScreen.Trader?.GetFreeDrams(),
                yourDrams = The.Player?.GetFreeDrams(),
                theirs = Side(screen, 0),
                yours = Side(screen, 1),
                // Drams of value each way, and what the offer comes to: positive = you pay that many drams
                // (TradeScreen.UpdateTotals/HandleMenuOption, TradeScreen.cs:727/842).
                totals = new
                {
                    buy = System.Math.Round(screen.Totals[0], 2),
                    sell = System.Math.Round(screen.Totals[1], 2),
                    youPay = TradeUI.CalculateTrade(screen.Totals[0], screen.Totals[1]),
                },
                buttons = new[] { "offer", "cancel" },
            };
        }

        private static List<object> Side(TradeScreen screen, int side)
        {
            var list = new List<object>();
            List<TradeEntry> entries = screen.tradeEntries[side];
            for (int i = 0; i < entries.Count; i++)
            {
                XRL.World.GameObject go = entries[i].GO;
                if (go == null)
                {
                    continue; // category header
                }
                list.Add(new
                {
                    i,
                    name = StateBuilder.Clean(go.DisplayName),
                    count = go.Count,
                    // Per-item price as the trade line shows it (Qud.UI/TradeLine.cs:473).
                    price = System.Math.Round(TradeUI.GetValue(go, side == 0), 2),
                    selected = TradeScreen.NumberSelected[side][i],
                });
            }
            return list;
        }

        public static string Choose(TradeScreen screen, JObject body)
        {
            string button = ((string)body["button"])?.ToLowerInvariant();
            if (button == "offer")
            {
                // Same as the offer hotkey (TradeScreen.HandleOfferTrade, line 754); may raise confirmation popups.
                screen.HandleOfferTrade();
                return "offer";
            }
            if (button == "cancel")
            {
                screen.Cancel();
                return "cancel";
            }
            string sideName = ((string)body["side"])?.ToLowerInvariant();
            int side = sideName == "theirs" ? 0 : sideName == "yours" ? 1 : -1;
            JToken item = body["item"];
            JToken count = body["count"];
            if (side < 0 || item == null || item.Type != JTokenType.Integer || count == null || count.Type != JTokenType.Integer)
            {
                throw new ActionRejectedException(400, "give \"button\" (offer/cancel), or \"side\" (theirs/yours) + \"item\" + \"count\"",
                    new { prompt = Describe(screen) });
            }
            List<TradeEntry> entries = screen.tradeEntries[side];
            int i = (int)item;
            if (i < 0 || i >= entries.Count || entries[i].GO == null)
            {
                throw new ActionRejectedException(400, "\"item\" must be an i from prompt." + sideName, new { prompt = Describe(screen) });
            }
            SetCount(screen, entries[i].GO, (int)count);
            return "select " + (int)count + " of " + sideName + " " + i;
        }

        // TradeScreen.setHowManySelected clamps to the stack size and asks before trading away important items
        // (line 275); then refresh the lines and totals as HandleTradeSome does (line 245).
        private static async void SetCount(TradeScreen screen, XRL.World.GameObject go, int count)
        {
            await screen.setHowManySelected(go, count);
            screen.UpdateViewFromData();
            screen.UpdateTotals();
        }
    }
}
