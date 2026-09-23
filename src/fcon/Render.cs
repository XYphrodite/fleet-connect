// List rendering and coloured one-liners. Same layout as Format-PcLines,
// including the ssh-alias/port columns added to end the 'ssh fcon-xeon'
// surprise: an alias is used whole, so it is shown.
namespace FleetConnect;

static class Render
{
    public static void Fail(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  " + text);
        Console.ResetColor();
    }

    public static void Note(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  " + text);
        Console.ResetColor();
    }

    public static void Paint(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static List<string> FormatPcLines(List<PcModel> pcs)
    {
        int wName = 4, wHost = 4, wUser = 0;
        foreach (var pc in pcs)
        {
            wName = Math.Max(wName, pc.Name.Length);
            wHost = Math.Max(wHost, pc.Address.Length);
            wUser = Math.Max(wUser, pc.User.Length);
        }
        wName = Math.Min(wName, 20);
        wHost = Math.Min(wHost, 38);
        wUser = Math.Min(wUser, 18);

        bool anyStatus = pcs.Any(p => p.Online != "");
        var lines = new List<string>();
        foreach (var pc in pcs)
        {
            string mark = "";
            if (anyStatus)
                mark = pc.Online == "up" ? "* " : pc.Online == "down" ? ". " : "? ";
            var tail = new List<string> { Protocols.ToName(pc.Protocol).PadRight(3) };
            if (wUser > 0)
                tail.Add(pc.User.PadRight(wUser));
            // SSH through an alias ignores User/Host/Port from this row.
            if (pc.SshAlias != "")
                tail.Add("alias " + pc.SshAlias);
            if (pc.Port != "")
                tail.Add("port " + pc.Port);
            if (pc.Note != "")
                tail.Add(pc.Note);
            lines.Add((mark + pc.Name.PadRight(wName) + "  " +
                       pc.Address.PadRight(wHost) + "  " +
                       string.Join("  ", tail)).TrimEnd());
        }
        return lines;
    }
}
