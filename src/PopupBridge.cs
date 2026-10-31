using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using Qud.UI;

namespace QudBridge
{
    // Reads and answers the game's modern popup window, Qud.UI.PopupMessage. It backs Popup.Show/ShowYesNo/
    // PickOption (XRL.UI/Popup.cs:751 NewPopupMessageAsync), conversations (Popup.ShowConversation,
    // Popup.cs:1571) and keyboard number/text prompts (AskNumberAsync/AskStringAsync, Popup.cs:1349/1396).
    // Everything here runs on the UI thread (UiThread.Invoke): the window is a Unity object, and while it is
    // up the game thread is blocked waiting for its answer (Popup.cs:899).
    public static class PopupBridge
    {
        // The popup waiting for an answer: active, interactable (WindowBase.Show sets it, PopupMessage.Hide
        // clears it, Qud.UI/WindowBase.cs:215, PopupMessage.cs:1092) and with a callback still pending.
        // Several can be stacked; ShowPopup calls SetAsLastSibling (PopupMessage.cs:778), so the topmost wins.
        public static PopupMessage Top()
        {
            PopupMessage top = null;
            foreach (PopupMessage p in UnityEngine.Object.FindObjectsOfType<PopupMessage>())
            {
                if (!p.isActiveAndEnabled || p.canvasGroup == null || !p.canvasGroup.interactable)
                {
                    continue;
                }
                if (p.commandCallback == null && p.selectCallback == null)
                {
                    continue;
                }
                if (top == null || p.transform.GetSiblingIndex() > top.transform.GetSiblingIndex())
                {
                    top = p;
                }
            }
            return top;
        }

        // The direction picker (XRL.UI/PickDirection.cs:28) isn't a popup window: it runs on the game thread
        // and reads keys. It shows as view "PickTarget" in PickDirection mode with its label in currentText.
        public static bool IsDirectionPrompt()
        {
            return GameManager.Instance?.CurrentGameView == "PickTarget"
                && PickTargetWindow.currentMode == PickTargetWindow.TargetMode.PickDirection;
        }

        // Any other PickTarget mode: a cell picker (XRL.UI/PickTarget.cs ShowPicker / PickCells).
        public static bool IsTargetPrompt()
        {
            return GameManager.Instance?.CurrentGameView == "PickTarget"
                && PickTargetWindow.currentMode != PickTargetWindow.TargetMode.PickDirection;
        }

        // Answer it the way the on-screen control panel does off-stage: push the keypad key
        // (GameManager.cs:1305-1340). PickDirection maps NumPad keys to directions and 5 to self (PickDirection.cs:56-110).
        public static void AnswerDirection(string dir)
        {
            UnityEngine.KeyCode key;
            switch (dir?.ToUpperInvariant())
            {
                case "N": key = UnityEngine.KeyCode.Keypad8; break;
                case "S": key = UnityEngine.KeyCode.Keypad2; break;
                case "E": key = UnityEngine.KeyCode.Keypad6; break;
                case "W": key = UnityEngine.KeyCode.Keypad4; break;
                case "NE": key = UnityEngine.KeyCode.Keypad9; break;
                case "NW": key = UnityEngine.KeyCode.Keypad7; break;
                case "SE": key = UnityEngine.KeyCode.Keypad3; break;
                case "SW": key = UnityEngine.KeyCode.Keypad1; break;
                case "SELF": case ".": key = UnityEngine.KeyCode.Keypad5; break;
                case "CANCEL": key = UnityEngine.KeyCode.Escape; break;
                default:
                    throw new ActionRejectedException(400, "\"dir\" must be one of N,S,E,W,NE,NW,SE,SW, self or cancel");
            }
            ConsoleLib.Console.Keyboard.PushKey(new ConsoleLib.Console.Keyboard.XRLKeyEvent(key));
        }

        // The item picker (Qud.UI/PickGameObjectScreen.cs), used for picking up from a cell or container
        // (PickItem.ShowPicker, XRL.UI/PickItem.cs:285). A singleton window, shown under view "PickGameObject".
        private static PickGameObjectScreen ItemPicker()
        {
            if (GameManager.Instance?.CurrentGameView != "PickGameObject")
            {
                return null;
            }
            PickGameObjectScreen screen = SingletonWindowBase<PickGameObjectScreen>.instance;
            return screen != null && screen.isActiveAndEnabled ? screen : null;
        }

        private static object DescribeItemPicker(PickGameObjectScreen screen)
        {
            var options = new List<object>();
            for (int i = 0; i < screen.rawGameObjects.Count; i++)
            {
                options.Add(new { i, text = Text(screen.rawGameObjects[i].DisplayName) });
            }
            // Same menu as the screen's hotkey bar (PickGameObjectScreen.yieldMenuOptions, line 413).
            var buttons = new List<string>();
            if (screen.style == XRL.UI.PickItem.PickItemDialogStyle.GetItemDialog)
            {
                buttons.Add("take all");
                if (PickGameObjectScreen.Actor != null && PickGameObjectScreen.Container != null)
                {
                    buttons.Add("store");
                }
            }
            buttons.Add("cancel");
            return new
            {
                kind = "pick",
                title = NullIfEmpty(Text(screen.titleText?.text)),
                options,
                buttons,
            };
        }

        private static readonly System.Reflection.FieldInfo PickerResult =
            HarmonyLib.AccessTools.Field(typeof(PickGameObjectScreen), "menucomplete");

        private static string ChooseItemPicker(PickGameObjectScreen screen, JObject body)
        {
            JToken option = body["option"];
            string button = ((string)body["button"])?.ToLowerInvariant();
            if (option != null)
            {
                if (option.Type != JTokenType.Integer || (int)option < 0 || (int)option >= screen.rawGameObjects.Count)
                {
                    throw new ActionRejectedException(400, "\"option\" must be an index into prompt.options", new { prompt = DescribeItemPicker(screen) });
                }
                XRL.World.GameObject go = screen.rawGameObjects[(int)option];
                // What HandleSelectItem does for an item line (PickGameObjectScreen.cs:588). For "get" pickers the
                // game then opens the item's action menu (PickItem.cs:292), which comes back as a popup prompt.
                var complete = (System.Threading.Tasks.TaskCompletionSource<PickGameObjectScreen.Result>)PickerResult.GetValue(screen);
                complete.TrySetResult(new PickGameObjectScreen.Result { item = go });
                return "option " + (int)option;
            }
            switch (button)
            {
                case "take all":
                    screen.HandleMenuOption(PickGameObjectScreen.TAKE_ALL);
                    return "take all";
                case "store":
                    screen.HandleMenuOption(PickGameObjectScreen.STORE_ITEM);
                    return "store";
                case "cancel":
                    screen.Cancel();
                    return "cancel";
            }
            throw new ActionRejectedException(400, "give \"option\" or \"button\" (one of prompt.buttons)", new { prompt = DescribeItemPicker(screen) });
        }

        // What /state and action results show for whatever is waiting on the player: a popup, a direction
        // prompt, the item picker, the trade screen, a book or terminal (ScreenBridge), or the end screen.
        public static object DescribePrompt()
        {
            if (IsDirectionPrompt())
            {
                return new { kind = "direction", text = Text(PickTargetWindow.currentText) };
            }
            if (IsTargetPrompt())
            {
                // Cell pickers (abilities like Charge, throwing): answer with choose {"x","y"} or cancel.
                return new { kind = "target", text = Text(PickTargetWindow.currentText), mode = PickTargetWindow.currentMode.ToString(), buttons = new[] { "cancel" } };
            }
            // A popup can sit on top of the picker (e.g. during "take all"), so it wins.
            PopupMessage p = Top();
            if (p != null)
            {
                return Describe(p);
            }
            PickGameObjectScreen picker = ItemPicker();
            if (picker != null)
            {
                return DescribeItemPicker(picker);
            }
            TradeScreen trade = TradeBridge.Screen();
            if (trade != null)
            {
                if (Volatile.Read(ref TradeBridge.Offering) > 0)
                {
                    return null;
                }
                return TradeBridge.Describe(trade);
            }
            // Books, terminals, the gamepad number prompt.
            object screen = ScreenBridge.Describe();
            if (screen != null)
            {
                return screen;
            }
            return GameOver.Describe();
        }

        public static object Describe(PopupMessage p)
        {
            var options = new List<object>();
            List<QudMenuItem> items = p.controller.menuData ?? new List<QudMenuItem>();
            for (int i = 0; i < items.Count; i++)
            {
                options.Add(new { i, text = OptionText(items[i].text) });
            }
            var buttons = new List<object>();
            List<QudMenuItem> bottom = p.controller.bottomContextOptions ?? new List<QudMenuItem>();
            for (int i = 0; i < bottom.Count; i++)
            {
                buttons.Add(new { i, text = Text(bottom[i].text), command = bottom[i].command });
            }
            bool hasInput = p.inputBox != null && p.inputBox.gameObject.activeInHierarchy;
            return new
            {
                kind = "popup",
                // PopupID is set by some callers, e.g. "Conversation:<title>" (Popup.cs:1617).
                id = NullIfEmpty(Text(p.PopupID)),
                // Conversations pass the speaker as contextTitle, not title (Popup.cs:1617, PopupMessage.cs:852).
                title = p.TitleContainer != null && p.TitleContainer.activeSelf ? NullIfEmpty(Text(p.Title.text))
                    : p.contextText != null && p.contextText.gameObject.activeInHierarchy ? NullIfEmpty(Text(p.contextText.text)) : null,
                text = Text(p.Message.text),
                options,
                buttons,
                input = hasInput ? new { value = p.inputBox.text, numeric = p.RestrictChars == "0123456789" || p.RestrictChars == "0123456789-" } : null,
                // No buttons or options: the game closes it on any key (PopupMessage.cs:1035).
                anyKey = items.Count == 0 && bottom.Count == 0 ? true : (bool?)null,
            };
        }

        // A cell picker (PickTarget.ShowPicker, XRL.UI/PickTarget.cs:378) answered the way the mouse does: PointerOver
        // moves its cursor (GameManager.cs:1190) and LeftClick selects (PickTarget.cs:965). The picker ignores the
        // first PointerOver it sees (flag3, PickTarget.cs:402/794), so send it twice. Its own checks still apply
        // ("You may only select a visible square!" comes back as a popup, then the picker again).
        // PickField (several cells, e.g. walls) isn't supported.
        private static string ChooseCell(JObject body)
        {
            if (PickTargetWindow.currentMode != PickTargetWindow.TargetMode.PickCells)
            {
                throw new ActionRejectedException(409, "this picker selects several cells (" + PickTargetWindow.currentMode + "), which isn't supported; cancel with {\"button\":\"cancel\"}");
            }
            JToken jx = body["x"], jy = body["y"];
            if (jx == null || jy == null || jx.Type != JTokenType.Integer || jy.Type != JTokenType.Integer)
            {
                throw new ActionRejectedException(400, "give the target cell as \"x\" and \"y\", or cancel with {\"button\":\"cancel\"}");
            }
            int x = (int)jx, y = (int)jy;
            if (x < 0 || y < 0 || x >= 80 || y >= 25)
            {
                throw new ActionRejectedException(400, "(" + x + "," + y + ") is outside the map");
            }
            ConsoleLib.Console.Keyboard.PushMouseEvent("PointerOver", x, y);
            ConsoleLib.Console.Keyboard.PushMouseEvent("PointerOver", x, y);
            ConsoleLib.Console.Keyboard.PushMouseEvent("LeftClick", x, y);
            return "cell " + x + "," + y;
        }

        // Answer the top popup the way input would. Returns a short description of what was chosen.
        public static string Choose(JObject body)
        {
            if (IsDirectionPrompt())
            {
                string dir = (string)body["dir"];
                AnswerDirection(dir);
                return "direction " + dir;
            }
            if (IsTargetPrompt())
            {
                if ((string)body["button"] == "cancel" || string.Equals((string)body["dir"], "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleLib.Console.Keyboard.PushKey(new ConsoleLib.Console.Keyboard.XRLKeyEvent(UnityEngine.KeyCode.Escape));
                    return "cancel target";
                }
                return ChooseCell(body);
            }
            PopupMessage p = Top();
            if (p == null)
            {
                PickGameObjectScreen picker = ItemPicker();
                if (picker != null)
                {
                    return ChooseItemPicker(picker, body);
                }
                TradeScreen trade = TradeBridge.Screen();
                if (trade != null)
                {
                    return TradeBridge.Choose(trade, body);
                }
                string answered = ScreenBridge.Choose(body);
                if (answered != null)
                {
                    return answered;
                }
                if (GameOver.TryClose())
                {
                    return "exit end screen";
                }
                throw new ActionRejectedException(409, "no popup is open");
            }
            List<QudMenuItem> items = p.controller.menuData ?? new List<QudMenuItem>();
            List<QudMenuItem> bottom = p.controller.bottomContextOptions ?? new List<QudMenuItem>();
            JToken option = body["option"];
            JToken button = body["button"];
            string text = (string)body["text"];

            if (text != null)
            {
                if (p.inputBox == null || !p.inputBox.gameObject.activeInHierarchy)
                {
                    throw new ActionRejectedException(400, "this popup has no text input", new { prompt = Describe(p) });
                }
                // Same as pressing Enter in the box: OnInputSubmit selects the Submit/Accept button
                // (PopupMessage.cs:946), and the caller reads the box's text (Popup.cs:783).
                p.inputBox.text = text;
                p.OnInputSubmit(text);
                return "submitted \"" + text + "\"";
            }
            if (option != null)
            {
                if (option.Type != JTokenType.Integer || (int)option < 0 || (int)option >= items.Count)
                {
                    throw new ActionRejectedException(400, "\"option\" must be an index into prompt.options", new { prompt = Describe(p) });
                }
                QudMenuItem item = items[(int)option];
                // The menu controller routes item choices to OnSelect (selectHandlers, QudBaseMenuController.cs:113);
                // NewPopupMessageAsync gives both callbacks the same completion (Popup.cs:763-775).
                if (p.selectCallback != null)
                {
                    p.OnSelect(item);
                }
                else
                {
                    p.OnActivateCommand(item);
                }
                return "option " + (int)option + " (" + OptionText(item.text) + ")";
            }
            if (button != null)
            {
                int index = FindButton(bottom, button);
                if (index < 0)
                {
                    throw new ActionRejectedException(400, "no such button (give an index, command or text from prompt.buttons)", new { prompt = Describe(p) });
                }
                p.OnActivateCommand(bottom[index]);
                return "button " + index + " (" + Text(bottom[index].text) + ")";
            }
            if (items.Count == 0 && bottom.Count == 0)
            {
                // "Press any key" popup: what Update does on a keypress (PopupMessage.cs:1035-1040).
                p.OnActivateCommand(default(QudMenuItem));
                return "dismissed";
            }
            throw new ActionRejectedException(400, "give \"option\", \"button\" or \"text\"", new { prompt = Describe(p) });
        }

        private static int FindButton(List<QudMenuItem> buttons, JToken which)
        {
            if (which.Type == JTokenType.Integer)
            {
                int i = (int)which;
                return i >= 0 && i < buttons.Count ? i : -1;
            }
            string s = (string)which;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (string.Equals(buttons[i].command, s, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(buttons[i].hotkey, s, StringComparison.OrdinalIgnoreCase)
                    || Text(buttons[i].text).EndsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static string Text(string s)
        {
            return (StateBuilder.Clean(s) ?? "").Trim();
        }

        // Conversation options carry a "[1] " hotkey prefix and trailing blank lines (Popup.cs:1589).
        private static readonly Regex HotkeyPrefix = new Regex(@"^\[[0-9a-zA-Z]\]\s*");

        private static string OptionText(string s)
        {
            return HotkeyPrefix.Replace(Text(s), "");
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
