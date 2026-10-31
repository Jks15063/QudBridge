using System.Threading;
using Qud.UI;

namespace QudBridge
{
    // Death / game over. When the player dies the game shows the "You died." popup (id "DeathMessage",
    // XRL.World/GameObject.cs:15066; an ordinary popup), sets Game.Running = false, then shows the end screen
    // (Qud.UI/GameSummaryScreen.cs, view "GameSummaryScreen", pushed by XRL.UI/GameSummaryUI.cs:31) and finally
    // returns to the main menu. The end screen is exposed as a "gameover" prompt; its contents are remembered so
    // that requests made after it closes can still say how the last game ended.
    public static class GameOver
    {
        public const string SummaryView = "GameSummaryScreen";

        private static object last;

        // The last game's end, or null if no game has ended since the game was launched.
        public static object Last => Volatile.Read(ref last);

        // UI thread. The end screen, if it is up and waiting to be closed.
        private static GameSummaryScreen Screen()
        {
            GameSummaryScreen s = SingletonWindowBase<GameSummaryScreen>.instance;
            if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy)
            {
                return null;
            }
            var done = s.completionSource;
            return done == null || done.Task.IsCompleted ? null : s;
        }

        // UI thread.
        public static object Describe()
        {
            GameSummaryScreen s = Screen();
            if (s == null)
            {
                return null;
            }
            var record = new
            {
                name = StateBuilder.Clean(s.Name),
                cause = StateBuilder.Clean(s.causeText != null ? s.causeText.text : null),
                // The score breakdown the game shows (and saves as a tombstone file).
                details = Glyphs(StateBuilder.Clean(s.Details)),
            };
            // Errors after the game ends carry name and cause only; the details are ~2-3 KB.
            Volatile.Write(ref last, new { record.name, record.cause });
            return new { kind = "gameover", record.name, record.cause, record.details, buttons = new[] { "exit" } };
        }

        // The summary decorates with CP437 glyph codes (XRL.Core/XRLCore.cs ConstructScore): 0xEA (an ornament
        // around headings) and 0xFA (a bullet).
        private static string Glyphs(string s)
        {
            return s?.Replace("\u00ea ", "").Replace(" \u00ea", "").Replace("\u00ea", "").Replace('\u00fa', '-');
        }

        // UI thread. Closes the end screen (its Cancel/"Exit" handler); the game then goes to the main menu.
        public static bool TryClose()
        {
            GameSummaryScreen s = Screen();
            if (s == null)
            {
                return false;
            }
            s.Exit();
            return true;
        }
    }
}
