using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QudBridge
{
    // Localhost HTTP server. Runs on its own background thread; each request is handled on the
    // thread pool. Nothing here may touch game state directly: go through GameThread.Invoke.
    public static class BridgeServer
    {
        public const int Port = 15600;

        private static readonly object Lock = new object();
        private static HttpListener Listener;
        private static Thread AcceptThread;

        public static bool Running
        {
            get { lock (Lock) return Listener != null; }
        }

        // Idempotent: [ModSensitiveCacheInit] can fire more than once per process (config hotload),
        // but ModManager.Init only builds mods once (XRL/ModManager.cs:586), so a static guard suffices.
        public static void Start()
        {
            lock (Lock)
            {
                if (Listener != null)
                {
                    return;
                }
                var listener = new HttpListener();
                // Mono's HttpListener matches the request Host header against the prefix host,
                // so register both spellings of loopback. Never bind */+ (would expose to the LAN).
                listener.Prefixes.Add("http://localhost:" + Port + "/");
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    MetricsManager.LogError("QudBridge: failed to start HTTP server on port " + Port, ex);
                    try { listener.Close(); } catch { }
                    return;
                }
                Listener = listener;
                AcceptThread = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "QudBridge HTTP" };
                AcceptThread.Start();
                // Shut down when Unity quits (same event the game uses, GameManager.cs:857).
                UnityEngine.Application.quitting -= Stop;
                UnityEngine.Application.quitting += Stop;
            }
            MetricsManager.LogInfo("QudBridge: HTTP server listening on http://localhost:" + Port + "/");
        }

        public static void Stop()
        {
            HttpListener listener;
            Thread thread;
            lock (Lock)
            {
                listener = Listener;
                thread = AcceptThread;
                Listener = null;
                AcceptThread = null;
            }
            if (listener == null)
            {
                return;
            }
            try
            {
                // Unblocks GetContext() on the accept thread, which then exits.
                listener.Close();
            }
            catch (Exception ex)
            {
                MetricsManager.LogError("QudBridge: error stopping HTTP server", ex);
            }
            thread?.Join(1000);
            MetricsManager.LogInfo("QudBridge: HTTP server stopped");
        }

        private static void AcceptLoop(HttpListener listener)
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception ex)
                {
                    // Stop() closed the listener. Don't test listener.IsListening: Mono's Close()
                    // leaves it true, and GetContext then throws ObjectDisposedException forever.
                    if (ex is ObjectDisposedException || !IsCurrent(listener))
                    {
                        return;
                    }
                    MetricsManager.LogError("QudBridge: accept failed", ex);
                    Thread.Sleep(100); // Never spin if accept keeps failing.
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private static bool IsCurrent(HttpListener listener)
        {
            lock (Lock) return ReferenceEquals(Listener, listener);
        }

        private static void Handle(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                string path = request.Url.AbsolutePath.TrimEnd('/');
                if (path == "")
                {
                    if (request.HttpMethod != "GET")
                    {
                        WriteJson(context, 405, new { error = "method not allowed" });
                        return;
                    }
                    WriteJson(context, 200, new { status = "ok", mod = "QudBridge", version = QudBridgeMod.Version });
                    return;
                }
                if (path == "/debug/gamethread")
                {
                    if (request.HttpMethod != "GET")
                    {
                        WriteJson(context, 405, new { error = "method not allowed" });
                        return;
                    }
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var body = GameThread.Invoke(() => new
                    {
                        thread = Thread.CurrentThread.Name,
                        isCoreThread = XRL.Core.XRLCore.IsCoreThread,
                        gameRunning = XRL.The.Game?.Running ?? false,
                        turns = XRL.The.Game?.Turns,
                        player = XRL.The.Game?.PlayerName,
                    });
                    WriteJson(context, 200, new { body.thread, body.isCoreThread, body.gameRunning, body.turns, body.player, ms = timer.ElapsedMilliseconds });
                    return;
                }
                if (path == "/state")
                {
                    if (request.HttpMethod != "GET")
                    {
                        WriteJson(context, 405, new { error = "method not allowed" });
                        return;
                    }
                    int messages = StateBuilder.DefaultMessages;
                    string m = request.QueryString["messages"];
                    if (m != null && (!int.TryParse(m, out messages) || messages < 0))
                    {
                        WriteJson(context, 400, new { error = "messages must be a non-negative integer" });
                        return;
                    }
                    bool legend = request.QueryString["legend"] == "1";
                    // Fast path off the game thread: the main menu doesn't pump gameQueue, so without this the
                    // request would sit there until the 504. Only reads a reference and a bool, never mutates;
                    // StateBuilder repeats the check on the game thread.
                    if (!(XRL.The.Game?.Running ?? false))
                    {
                        // After death the end screen is up with the game no longer running (GameOver.cs).
                        if (GameManager.Instance?.CurrentGameView == GameOver.SummaryView)
                        {
                            object summary = ActionHandler.TryDescribePopup();
                            if (summary != null)
                            {
                                WriteJson(context, 200, new { screen = GameOver.SummaryView, gameOver = true, prompt = summary });
                                return;
                            }
                        }
                        NoGame();
                    }
                    // Off normal play the game thread is usually blocked on a UI screen (Popup.cs:899), so read
                    // the popup from the UI thread instead of the game state.
                    string screen = GameManager.Instance?.CurrentGameView;
                    if (screen != XRL.UI.Options.StageViewID)
                    {
                        object prompt = ActionHandler.TryDescribePopup();
                        if (prompt != null)
                        {
                            WriteJson(context, 200, new { screen, prompt });
                            return;
                        }
                        WriteJson(context, 409, new { error = "the game is on a screen this API can't read yet", screen });
                        return;
                    }
                    WriteJson(context, 200, GameThread.Invoke(() => StateBuilder.Build(messages, legend)));
                    return;
                }
                if (path == "/character")
                {
                    if (request.HttpMethod != "GET")
                    {
                        WriteJson(context, 405, new { error = "method not allowed" });
                        return;
                    }
                    // Same fast "no game" check as /state; needs normal play because the game thread must be free.
                    if (!(XRL.The.Game?.Running ?? false))
                    {
                        NoGame();
                    }
                    bool details = request.QueryString["details"] == "1";
                    WriteJson(context, 200, GameThread.Invoke(() => Advancement.Describe(details)));
                    return;
                }
                if (path == "/action")
                {
                    if (request.HttpMethod != "POST")
                    {
                        WriteJson(context, 405, new { error = "method not allowed" });
                        return;
                    }
                    JObject body;
                    try
                    {
                        string text;
                        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                        {
                            text = reader.ReadToEnd();
                        }
                        body = JObject.Parse(text);
                    }
                    catch (JsonException ex)
                    {
                        WriteJson(context, 400, new { error = "body must be a JSON object: " + ex.Message });
                        return;
                    }
                    bool closingSummary = GameManager.Instance?.CurrentGameView == GameOver.SummaryView
                        && string.Equals((string)body["type"], "choose", StringComparison.OrdinalIgnoreCase);
                    if (!(XRL.The.Game?.Running ?? false) && !closingSummary)
                    {
                        NoGame();
                    }
                    // Also off-thread: modern popups don't pump gameQueue (XRL.UI/Popup.cs:899), so this would
                    // otherwise wait out the 5s dispatch timeout. RequirePlayerTurn repeats it on the game thread.
                    string view = GameManager.Instance?.CurrentGameView;
                    bool choosing = string.Equals((string)body["type"], "choose", StringComparison.OrdinalIgnoreCase);
                    if (view != XRL.UI.Options.StageViewID && !choosing)
                    {
                        throw new ActionRejectedException(409, "not in normal play (screen: " + view + ")");
                    }
                    WriteJson(context, 200, ActionHandler.Handle(body, StateBuilder.DefaultMessages));
                    return;
                }
                WriteJson(context, 404, new { error = "not found", path = request.Url.AbsolutePath });
            }
            catch (ActionRejectedException ex)
            {
                var error = ex.Extra != null ? JObject.FromObject(ex.Extra) : new JObject();
                error.AddFirst(new JProperty("error", ex.Message));
                WriteJson(context, ex.Status, error);
            }
            catch (NoGameException ex)
            {
                WriteJson(context, 409, new { error = ex.Message });
            }
            catch (GameThreadUnavailableException ex)
            {
                WriteJson(context, 503, new { error = ex.Message });
            }
            catch (GameThreadTimeoutException ex)
            {
                WriteJson(context, 504, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                MetricsManager.LogError("QudBridge: request failed", ex);
                try { WriteJson(context, 500, new { error = ex.Message }); } catch { }
            }
        }

        // "No game" error; once a game has ended it also says how (GameOver.Last), so a client that missed the
        // end screen can still tell death from never having started.
        private static void NoGame()
        {
            string screen = GameManager.Instance?.CurrentGameView ?? "unknown";
            object last = GameOver.Last;
            throw new ActionRejectedException(409, "no game in progress (screen: " + screen + ")",
                last == null ? null : new { gameOver = true, lastGame = last });
        }

        // Omit nulls to keep responses compact (they're fed to an LLM).
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        private static void WriteJson(HttpListenerContext context, int status, object body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body, JsonSettings));
            var response = context.Response;
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            using (Stream output = response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
            }
            response.Close();
        }
    }
}
