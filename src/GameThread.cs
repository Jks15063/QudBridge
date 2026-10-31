using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using ConsoleLib.Console;
using XRL.Core;

namespace QudBridge
{
    public class GameThreadUnavailableException : Exception
    {
        public GameThreadUnavailableException(string message) : base(message) { }
    }

    public class GameThreadTimeoutException : Exception
    {
        public GameThreadTimeoutException(string message) : base(message) { }
    }

    // Runs work on the game's core thread ("XRLCore Thread", XRL.Core/XRLCore.cs:3557) and blocks the
    // caller until it finishes or times out. Rather than add our own per-frame hook, this reuses the
    // game's GameManager.gameQueue (GameManager.cs:150), which the core thread already drains:
    //  - every iteration of the player-turn loop, via XRLCore.RenderBase (XRL.Core/XRLCore.cs:2621-2624),
    //  - while waiting on a key with pumpActions (ConsoleLib.Console/Keyboard.cs:1038), used by
    //    Popup (XRL.UI/Popup.cs:707), TradeUI, InventoryScreen and the save picker.
    // It is not drained in menus that read keys without pumpActions, nor while unfocused in normal play
    // (XRLCore.cs:756; see FocusPatch.cs); those requests time out.
    public static class GameThread
    {
        public const int DefaultTimeoutMs = 5000;

        public static T Invoke<T>(Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            if (XRLCore.IsCoreThread)
            {
                return work();
            }
            var queue = GameManager.Instance?.gameQueue;
            if (queue == null || XRLCore.CoreThread == null)
            {
                throw new GameThreadUnavailableException("game thread not started");
            }
            var item = new WorkItem<T>(work);
            queue.queueTask(item.Run);
            // In normal play the core thread sits in Keyboard.IdleWait (Keyboard.cs:926), which with
            // DisableAllIdleTileAnimations waits on KeyEvent with no timeout. Set it so the loop comes
            // round to RenderBase and drains the queue; WorkItem.Run resets it afterwards.
            Keyboard.KeyEvent.Set();
            if (!item.Done.Wait(timeoutMs))
            {
                if (item.TryCancel())
                {
                    throw new GameThreadTimeoutException("game thread did not pick up the request within " + timeoutMs +
                        "ms (no game loaded, game paused in the background, or a menu that doesn't pump the game queue)");
                }
                // Already running: it will finish on the game thread, but we stop waiting.
                if (!item.Done.Wait(timeoutMs))
                {
                    throw new GameThreadTimeoutException("request started on the game thread but did not finish within " +
                        (2 * timeoutMs) + "ms; it will complete in the background");
                }
            }
            if (item.Error != null)
            {
                ExceptionDispatchInfo.Capture(item.Error).Throw();
            }
            return item.Result;
        }

        private class WorkItem<T>
        {
            private const int Pending = 0, Running = 1, Cancelled = 2;

            private readonly Func<T> Work;
            private int State = Pending;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public T Result;
            public Exception Error;

            public WorkItem(Func<T> work)
            {
                Work = work;
            }

            // A timed-out request is cancelled so it never runs late (important once requests have side effects).
            public bool TryCancel()
            {
                return Interlocked.CompareExchange(ref State, Cancelled, Pending) == Pending;
            }

            public void Run()
            {
                try
                {
                    if (Interlocked.CompareExchange(ref State, Running, Pending) != Pending)
                    {
                        return;
                    }
                    try
                    {
                        Result = Work();
                    }
                    catch (Exception ex)
                    {
                        Error = ex;
                    }
                    Done.Set();
                }
                finally
                {
                    // Undo our KeyEvent.Set() so IdleWait blocks again instead of spinning. This is the
                    // game's own reset (Keyboard.cs:334); it is a no-op if real keys are queued.
                    Keyboard.ResetIfQueuesEmpty();
                }
            }
        }
    }
}
