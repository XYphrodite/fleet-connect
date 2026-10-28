namespace FleetConnect.Tui;

// Thread-safe timestamped ring buffer rendered in the bottom log panel.
internal sealed class LogBuffer
{
    readonly int capacity;
    readonly Queue<string> lines = new Queue<string>();
    readonly object sync = new object();
    long version;

    public LogBuffer(int capacity = 400)
    {
        this.capacity = Math.Max(10, capacity);
    }

    public long Version
    {
        get { lock (sync) return version; }
    }

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (sync)
        {
            lines.Enqueue(DateTimeOffset.Now.ToString("HH:mm:ss") + "  " + line.Trim());
            while (lines.Count > capacity)
                lines.Dequeue();
            version++;
        }
    }

    public IReadOnlyList<string> Tail(int count)
    {
        lock (sync)
        {
            int take = Math.Max(1, count);
            if (lines.Count <= take)
                return lines.ToArray();
            return lines.Skip(lines.Count - take).ToArray();
        }
    }
}
