using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Qud.UI;
using XRL.UI;
using XRL.UI.Framework;

namespace QudBridge
{
    // The modern full-screen windows that aren't popups: books (Qud.UI/BookScreen.cs), computer terminals
    // (Qud.UI/CyberneticsTerminalScreen.cs, used for both cybernetics and generic terminals) and the gamepad
    // number prompt (Qud.UI/AskNumberScreen.cs). Each blocks the game thread on its task (BookUI.cs:429,
    // CyberneticsTerminal.cs:208, Popup.cs:1331), so like PopupBridge everything here runs on the UI thread.
    public static class ScreenBridge
    {
        private static bool Visible(WindowBase w)
        {
            return w != null && w.isActiveAndEnabled && w.canvas != null && w.canvas.enabled;
        }

        public static object Describe()
        {
            BookScreen book = Book();
            if (book != null)
            {
                return DescribeBook(book);
            }
            CyberneticsTerminalScreen terminal = Terminal();
            if (terminal != null)
            {
                return DescribeTerminal();
            }
            AskNumberScreen number = Number();
            if (number != null)
            {
                return DescribeNumber(number);
            }
            StatusScreensScreen status = Status();
            if (status != null)
            {
                return DescribeStatus(status);
            }
            return null;
        }

        // Answers whichever of these screens is up; returns null if none is.
        public static string Choose(JObject body)
        {
            BookScreen book = Book();
            if (book != null)
            {
                return ChooseBook(book, body);
            }
            CyberneticsTerminalScreen terminal = Terminal();
            if (terminal != null)
            {
                return ChooseTerminal(terminal, body);
            }
            AskNumberScreen number = Number();
            if (number != null)
            {
                return ChooseNumber(number, body);
            }
            StatusScreensScreen status = Status();
            if (status != null)
            {
                string button = ((string)body["button"])?.ToLowerInvariant();
                if (button != "close" && button != "cancel")
                {
                    throw new ActionRejectedException(400, "this screen can only be closed: {\"button\":\"close\"}", new { prompt = DescribeStatus(status) });
                }
                // The screen's close button / Cancel (StatusScreensScreen.Exit, line 225).
                status.Exit();
                return "close " + TabName(status);
            }
            return null;
        }

        // ---- Status screens (character, inventory, skills, tinkering, journal, quests, factions, message log) ----
        // Not read: the API has the same information (GET /state, /character, /tinkering). Reported so a client
        // that finds the player here (e.g. the player opened it by hand) can close it.

        private static StatusScreensScreen Status()
        {
            StatusScreensScreen screen = SingletonWindowBase<StatusScreensScreen>.instance;
            return GameManager.Instance?.CurrentGameView == "StatusScreensScreen" && Visible(screen) ? screen : null;
        }

        private static string TabName(StatusScreensScreen screen)
        {
            if (screen.Screens == null || screen.CurrentScreen < 0 || screen.CurrentScreen >= screen.Screens.Count)
            {
                return null;
            }
            return StateBuilder.Clean(screen.Screens[screen.CurrentScreen]?.GetComponent<IStatusScreen>()?.GetTabString());
        }

        private static object DescribeStatus(StatusScreensScreen screen)
        {
            return new
            {
                kind = "statusScreen",
                tab = TabName(screen),
                text = "This screen isn't read; GET /state, /character and /tinkering have the same information.",
                buttons = new[] { "close" },
            };
        }

        // ---- Books ----

        private static BookScreen Book()
        {
            BookScreen screen = SingletonWindowBase<BookScreen>.instance;
            return GameManager.Instance?.CurrentGameView == "Book" && Visible(screen) ? screen : null;
        }

        private static readonly System.Reflection.FieldInfo BookPage =
            HarmonyLib.AccessTools.Field(typeof(BookScreen), "CurrentPage");

        private static int PageCount(BookScreen screen)
        {
            if (screen.Book != null)
            {
                return screen.Book.Pages.Count;
            }
            return screen.BookID != null && BookUI.Books.TryGetValue(screen.BookID, out BookInfo info) ? info.Pages.Count : 0;
        }

        private static object DescribeBook(BookScreen screen)
        {
            int page = (int)BookPage.GetValue(screen);
            int pages = PageCount(screen);
            var buttons = new List<string>();
            if (page > 0)
            {
                buttons.Add("prev");
            }
            if (page < pages - 1)
            {
                buttons.Add("next");
            }
            buttons.Add("close");
            return new
            {
                kind = "book",
                title = StateBuilder.Clean(screen.titleText?.text),
                page = page + 1,
                pages,
                text = StateBuilder.Clean(screen.RenderCurrentPage())?.Trim(),
                buttons,
            };
        }

        private static string ChooseBook(BookScreen screen, JObject body)
        {
            string button = ((string)body["button"])?.ToLowerInvariant();
            int page = (int)BookPage.GetValue(screen);
            if ((button == "next" && page >= PageCount(screen) - 1) || (button == "prev" && page <= 0))
            {
                throw new ActionRejectedException(409, button == "next" ? "already on the last page" : "already on the first page", new { prompt = DescribeBook(screen) });
            }
            switch (button)
            {
                // The screen's own hotkey bar actions (BookScreen.HandleMenuOption); they also run the page callbacks
                // some books have (e.g. cookbooks).
                case "next":
                    screen.HandleMenuOption(BookScreen.NEXT_PAGE);
                    return "next page";
                case "prev":
                    screen.HandleMenuOption(BookScreen.PREV_PAGE);
                    return "previous page";
                case "close":
                case "cancel":
                    screen.Cancel();
                    return "close book";
            }
            throw new ActionRejectedException(400, "give \"button\": \"next\", \"prev\" or \"close\"", new { prompt = DescribeBook(screen) });
        }

        // ---- Terminals ----

        private static CyberneticsTerminalScreen Terminal()
        {
            CyberneticsTerminalScreen screen = SingletonWindowBase<CyberneticsTerminalScreen>.instance;
            if (GameManager.Instance?.CurrentGameView != "CyberneticsTerminalScreen" || !Visible(screen))
            {
                return null;
            }
            return CyberneticsTerminal.Instance?.CurrentScreen != null || GenericTerminal.instance?.currentScreen != null ? screen : null;
        }

        private static object DescribeTerminal()
        {
            TerminalScreen cyber = CyberneticsTerminal.Instance?.CurrentScreen;
            GenericTerminalScreen generic = cyber == null ? GenericTerminal.instance?.currentScreen : null;
            List<string> list = cyber != null ? cyber.Options : generic.Options;
            var options = new List<object>();
            for (int i = 0; i < list.Count; i++)
            {
                options.Add(new { i, text = StateBuilder.Clean(list[i])?.Trim() });
            }
            return new
            {
                kind = "terminal",
                text = StateBuilder.Clean(cyber != null ? cyber.RenderedTextForModernUI : generic.RenderedTextForModernUI)?.Trim(),
                options,
                // Cybernetics terminals mark the hacking option (shown in red; Ctrl+Enter on the old UI).
                hackOption = cyber != null && cyber.HackOption >= 0 ? cyber.HackOption : (int?)null,
                buttons = new[] { "quit" },
            };
        }

        private static string ChooseTerminal(CyberneticsTerminalScreen screen, JObject body)
        {
            string button = ((string)body["button"])?.ToLowerInvariant();
            if (button == "quit" || button == "cancel")
            {
                // The screen's Cancel/back handler (CyberneticsTerminalScreen.Exit).
                screen.Exit();
                return "quit terminal";
            }
            TerminalScreen cyber = CyberneticsTerminal.Instance?.CurrentScreen;
            GenericTerminalScreen generic = cyber == null ? GenericTerminal.instance?.currentScreen : null;
            int count = cyber != null ? cyber.Options.Count : generic.Options.Count;
            JToken option = body["option"];
            if (option == null || option.Type != JTokenType.Integer || (int)option < 0 || (int)option >= count)
            {
                throw new ActionRejectedException(400, "give \"option\" (0-" + (count - 1) + ", prompt.options[].i) or \"button\": \"quit\"", new { prompt = DescribeTerminal() });
            }
            Task textDone = FinishTyping(screen);
            SelectTerminalOption(screen, textDone, (int)option);
            return "option " + (int)option;
        }

        // The screen types its text out; a keypress shows it all at once and the last line then calls
        // HandleTextComplete (CyberneticsTerminalRow.Update), which runs the screen's TextComplete: an implant is
        // installed there (CyberneticsInstallResult.cs:40). Do the same so an answer never skips it.
        private static Task FinishTyping(CyberneticsTerminalScreen screen)
        {
            bool finished = false;
            foreach (FrameworkDataElement e in screen.displayScroller.choices)
            {
                CyberneticsTerminalRow row = (e as CyberneticsTerminalLineData)?.row;
                if (row != null && !row.cursorDone)
                {
                    row.description.SetText(((CyberneticsTerminalLineData)e).Text);
                    row.currentCursor = false;
                    row.cursorDone = true;
                    finished = true;
                }
            }
            return finished ? screen.HandleTextComplete() : Task.CompletedTask;
        }

        // CyberneticsTerminalScreen.HandleSelect (line 290) without its typing-done check: Activate the option on a
        // worker (APIDispatch, as the screen does), then close or redraw.
        private static async void SelectTerminalOption(CyberneticsTerminalScreen screen, Task textDone, int option)
        {
            try
            {
                await textDone;
                CyberneticsTerminal cyber = CyberneticsTerminal.Instance;
                GenericTerminal generic = GenericTerminal.instance;
                if (cyber != null)
                {
                    cyber.Selected = option;
                    await APIDispatch.RunAndWaitAsync(delegate
                    {
                        cyber.CurrentScreen.Activate();
                    });
                    if (cyber.CurrentScreen == null)
                    {
                        screen.completionSource?.TrySetResult(result: false);
                        return;
                    }
                    screen.FooterText = "";
                    CyberneticsTerminalScreen.initReady = false;
                    await APIDispatch.RunAndWaitAsync(delegate
                    {
                        cyber.CurrentScreen.BeforeRender(null, ref screen.FooterText);
                    });
                }
                else if (generic != null)
                {
                    generic.nSelected = option;
                    await APIDispatch.RunAndWaitAsync(delegate
                    {
                        generic.currentScreen.Activate();
                    });
                    if (generic.currentScreen == null)
                    {
                        screen.completionSource?.TrySetResult(result: false);
                        return;
                    }
                    screen.FooterText = "";
                    CyberneticsTerminalScreen.initReady = false;
                    await APIDispatch.RunAndWaitAsync(delegate
                    {
                        generic.currentScreen.BeforeRender(null, ref screen.FooterText);
                    });
                }
                else
                {
                    return;
                }
                screen.Show();
                CyberneticsTerminalScreen.initReady = true;
            }
            catch (Exception ex)
            {
                MetricsManager.LogError("QudBridge: terminal option failed", ex);
            }
        }

        // ---- Gamepad number prompt (AskNumberAsync with a controller; keyboard uses a popup with an input box) ----

        private static AskNumberScreen Number()
        {
            AskNumberScreen screen = SingletonWindowBase<AskNumberScreen>.instance;
            return Visible(screen) && screen.isCurrentWindow() ? screen : null;
        }

        private static object DescribeNumber(AskNumberScreen screen)
        {
            return new
            {
                kind = "number",
                text = StateBuilder.Clean(screen.messageText?.text),
                value = screen.Current,
                min = screen.Min,
                max = screen.Max,
                buttons = new[] { "cancel" },
            };
        }

        private static string ChooseNumber(AskNumberScreen screen, JObject body)
        {
            if ((string)body["button"] == "cancel")
            {
                screen.Cancel();
                return "cancel";
            }
            JToken value = body["value"] ?? body["text"];
            if (value == null || !int.TryParse(value.ToString(), out int n))
            {
                throw new ActionRejectedException(400, "give \"value\" (a number from min to max) or \"button\": \"cancel\"", new { prompt = DescribeNumber(screen) });
            }
            n = Math.Max(screen.Min, Math.Min(screen.Max, n));
            // Accept reads the number back from the text (AskNumberScreen.cs:213).
            screen.Current = n;
            screen.numberText.SetText(n.ToString());
            screen.Accept();
            return "number " + n;
        }
    }
}
