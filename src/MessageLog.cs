using System.Collections.Generic;

namespace QudBridge
{
    // Our own numbered copy of recent message-log lines, fed by XRLCore's new-message callback
    // (registered in ActionHandler.RegisterHooks). Lets a request report the lines it caused without
    // reading game state, e.g. while the game thread is blocked in a popup.
    public static class MessageLog
    {
        private const int Keep = 200;
        private static readonly object Lock = new object();
        private static readonly Queue<KeyValuePair<long, string>> Lines = new Queue<KeyValuePair<long, string>>();
        private static long Seq;

        public static long LastSeq
        {
            get { lock (Lock) return Seq; }
        }

        public static void Add(string line)
        {
            if (line == null || line == "!clear")
            {
                return;
            }
            lock (Lock)
            {
                Lines.Enqueue(new KeyValuePair<long, string>(++Seq, line));
                while (Lines.Count > Keep)
                {
                    Lines.Dequeue();
                }
            }
        }

        public static List<string> Since(long seq)
        {
            var list = new List<string>();
            lock (Lock)
            {
                foreach (var entry in Lines)
                {
                    if (entry.Key > seq)
                    {
                        list.Add(StateBuilder.Clean(entry.Value));
                    }
                }
            }
            return list;
        }
    }
}
