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

        // Lines after seq. Runs of the same line are merged into one "line (xN)": auto-explore and travel log
        // "You pass by a watervine." on most steps. If older lines have already been dropped, says how many.
        public static List<string> Since(long seq)
        {
            var list = new List<string>();
            string last = null;
            int repeats = 0;
            lock (Lock)
            {
                long first = Lines.Count > 0 ? Lines.Peek().Key : Seq + 1;
                if (first > seq + 1)
                {
                    list.Add("(" + (first - seq - 1) + " earlier lines not kept)");
                }
                foreach (var entry in Lines)
                {
                    if (entry.Key <= seq)
                    {
                        continue;
                    }
                    string line = StateBuilder.Clean(entry.Value);
                    if (line == last)
                    {
                        repeats++;
                        continue;
                    }
                    Flush(list, last, repeats);
                    last = line;
                    repeats = 1;
                }
            }
            Flush(list, last, repeats);
            return list;
        }

        private static void Flush(List<string> list, string line, int repeats)
        {
            if (line != null)
            {
                list.Add(repeats > 1 ? line + " (x" + repeats + ")" : line);
            }
        }
    }
}
