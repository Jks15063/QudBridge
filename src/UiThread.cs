using System;

namespace QudBridge
{
    // Runs work on Unity's main thread via GameManager.uiQueue (GameManager.cs:148), which
    // GameManager.OnUpdate drains every frame (GameManager.cs:2955). Modern popups and screens live here,
    // and keep updating in the background with "Keep Caves of Qud active in background" on. The game
    // thread can't be used for them: it blocks on the UI task without pumping gameQueue (XRL.UI/Popup.cs:899).
    public static class UiThread
    {
        public const int DefaultTimeoutMs = 2000;

        public static T Invoke<T>(Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            var queue = GameManager.Instance?.uiQueue;
            if (queue == null)
            {
                throw new GameThreadUnavailableException("UI thread not started");
            }
            if (System.Threading.Thread.CurrentThread == queue.threadContext)
            {
                return work();
            }
            var item = new QueuedWork<T>(work);
            queue.queueTask(item.Run);
            return item.Await(timeoutMs, "UI thread");
        }
    }
}
