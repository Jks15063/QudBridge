using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Qud.UI;
using XRL;
using XRL.UI;
using XRL.UI.Framework;

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

        // Non-zero while an offer is being carried out. The trade screen stays up, unchanged, between the
        // confirmation popup closing and "Trade complete!" opening (items move in between), so it isn't reported
        // as a prompt then; popups raised during the offer still are (PopupBridge.DescribePrompt checks them first).
        public static int Offering;

        private static readonly FieldInfo MenuComplete = typeof(TradeScreen).GetField("menucomplete", BindingFlags.Instance | BindingFlags.NonPublic);

        // TradeScreen.HandleMenuOption's OFFER_TRADE branch (Qud.UI/TradeScreen.cs:840), copied so we know when it
        // ends: the original is async void. Started on the UI thread, so the continuation runs there too.
        private static async void Offer(TradeScreen screen)
        {
            try
            {
                screen.UpdateTotals();
                int difference = TradeUI.CalculateTrade(screen.Totals[0], screen.Totals[1]);
                TutorialManager.OnTradeOffer();
                TradeUI.TradeScreenMode mode = screen.mode;
                List<TradeEntry>[] entries = screen.tradeEntries;
                TradeUI.OfferStatus status = await APIDispatch.RunAndWaitAsync(() =>
                    TradeUI.PerformOffer(difference, forceComplete: false, TradeScreen.Trader, mode, entries, TradeScreen.NumberSelected));
                TutorialManager.OnTradeComplete();
                switch (status)
                {
                    case TradeUI.OfferStatus.NEXT:
                        return;
                    case TradeUI.OfferStatus.REFRESH:
                    case TradeUI.OfferStatus.TOP:
                        screen.ClearAndSetupTradeUI();
                        screen.UpdateViewFromData();
                        return;
                    case TradeUI.OfferStatus.CLOSE:
                        screen.Hide();
                        break;
                }
                screen.Cleanup();
                (MenuComplete?.GetValue(screen) as TaskCompletionSource<TradeUI.OfferStatus>)?.TrySetResult(status);
            }
            catch (System.Exception e)
            {
                MetricsManager.LogError("QudBridge: trade offer failed", e);
            }
            finally
            {
                Interlocked.Decrement(ref Offering);
            }
        }

        public static string Choose(TradeScreen screen, JObject body)
        {
            string button = ((string)body["button"])?.ToLowerInvariant();
            if (button == "offer")
            {
                Interlocked.Increment(ref Offering);
                Offer(screen);
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
