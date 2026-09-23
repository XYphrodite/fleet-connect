// Interactive picker. Same keys as Show-Picker: arrows move, letters jump
// (repeated presses walk the matches), digits pick, Enter connects,
// Esc/q quits. Falls back to a numbered prompt when the list does not fit
// the window or there is no console to read from.
namespace FleetConnect;

static class Picker
{
    public static bool Interactive()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return false;
            _ = Console.WindowWidth;
            _ = Console.CursorTop;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Returns the chosen index, or -1 when the operator backed out.
    public static int Show(string title, List<string> items, List<string> keys,
                           int start = 0, bool selectOnKey = false)
    {
        if (items.Count == 0)
            return -1;
        bool fits = false;
        try { fits = (items.Count + 4) < Console.WindowHeight; } catch { }
        if (!Interactive() || !fits)
            return ReadChoice(title, items);
        if (keys == null || keys.Count != items.Count)
            keys = new List<string>(new string[items.Count]);

        int index = Math.Max(0, Math.Min(start, items.Count - 1));
        int? top = null;
        bool cursorWas = true;
        Console.WriteLine();
        Render.Paint("  " + title, ConsoleColor.Cyan);
        try
        {
            try { cursorWas = Console.CursorVisible; Console.CursorVisible = false; } catch { }
            while (true)
            {
                int width = 40;
                try { width = Math.Max(20, Console.WindowWidth - 1); } catch { }
                if (top.HasValue)
                {
                    try { Console.SetCursorPosition(0, top.Value); } catch { }
                }
                for (int i = 0; i < items.Count; i++)
                {
                    string line = "  " + (i == index ? "> " : "  ") + items[i];
                    if (line.Length > width)
                        line = line.Substring(0, width);
                    line = line.PadRight(width);
                    if (i == index)
                    {
                        Console.BackgroundColor = ConsoleColor.Gray;
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.WriteLine(line);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine(line);
                    }
                }
                // Recomputed on every pass, so a redraw that scrolled the
                // window corrects itself instead of drawing the list twice.
                try { top = Console.CursorTop - items.Count; } catch { top = null; }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                bool handled = true;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        index = (index - 1 + items.Count) % items.Count;
                        break;
                    case ConsoleKey.DownArrow:
                        index = (index + 1) % items.Count;
                        break;
                    case ConsoleKey.Home:
                        index = 0;
                        break;
                    case ConsoleKey.End:
                        index = items.Count - 1;
                        break;
                    case ConsoleKey.Enter:
                        return index;
                    case ConsoleKey.Escape:
                        return -1;
                    default:
                        handled = false;
                        break;
                }
                if (handled)
                    continue;

                char ch = key.KeyChar;
                if (ch == 'q' || ch == 'Q')
                    return -1;
                if (ch >= '0' && ch <= '9')
                {
                    int n = ch - '0';
                    if (n >= 1 && n <= items.Count)
                    {
                        if (selectOnKey)
                            return n - 1;
                        index = n - 1;
                    }
                    continue;
                }
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                {
                    var hits = new List<int>();
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (keys[i] != null && keys[i].StartsWith(ch.ToString(),
                                StringComparison.OrdinalIgnoreCase))
                            hits.Add(i);
                    }
                    if (hits.Count == 1 && selectOnKey)
                        return hits[0];
                    if (hits.Count > 0)
                    {
                        // Repeated presses walk the matches.
                        int next = -1;
                        foreach (int h in hits)
                        {
                            if (h > index)
                            {
                                next = h;
                                break;
                            }
                        }
                        index = next >= 0 ? next : hits[0];
                    }
                }
            }
        }
        finally
        {
            try { Console.CursorVisible = cursorWas; } catch { }
            if (top.HasValue)
            {
                try { Console.SetCursorPosition(0, top.Value + items.Count); } catch { }
            }
            Console.WriteLine();
        }
    }

    static int ReadChoice(string title, List<string> items)
    {
        Console.WriteLine();
        Render.Paint("  " + title, ConsoleColor.Cyan);
        for (int i = 0; i < items.Count; i++)
            Console.WriteLine("  " + (i + 1).ToString().PadLeft(3) + ") " + items[i]);
        if (!Interactive())
        {
            Console.WriteLine();
            Render.Note("No console to read from, so nothing was chosen.");
            return -1;
        }
        Console.Write("  Choose 1-" + items.Count + ", Enter to cancel: ");
        string answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
            return -1;
        if (int.TryParse(answer.Trim(), out int n) && n >= 1 && n <= items.Count)
            return n - 1;
        Render.Fail("Not one of 1-" + items.Count + ".");
        return -1;
    }
}
