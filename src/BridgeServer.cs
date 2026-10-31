using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

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
                WriteJson(context, 404, new { error = "not found", path = request.Url.AbsolutePath });
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

        private static void WriteJson(HttpListenerContext context, int status, object body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
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
