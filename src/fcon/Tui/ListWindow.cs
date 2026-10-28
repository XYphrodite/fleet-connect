namespace FleetConnect.Tui;

// Computes which slice of a long list fits inside a fixed-height panel while
// keeping the selected row visible and reserving rows for "↑ N more" / "↓ N more".
internal static class ListWindow
{
    public static (int First, int Last) Around(int selected, int count, int capacity)
    {
        if (count <= 0)
            return (0, -1);

        selected = Math.Clamp(selected, 0, count - 1);
        if (capacity >= count)
            return (0, count - 1);

        capacity = Math.Max(1, capacity);

        int window = Math.Max(1, capacity - 1);
        int first = Math.Clamp(selected - (window / 2), 0, count - window);
        if (first > 0)
        {
            window = Math.Max(1, capacity - 2);
            first = Math.Clamp(selected - (window / 2), 0, count - window);
        }

        if (first + window >= count)
        {
            window = Math.Max(1, capacity - (first > 0 ? 1 : 0));
            first = Math.Max(0, count - window);
        }

        return (first, Math.Min(count - 1, first + window - 1));
    }
}
