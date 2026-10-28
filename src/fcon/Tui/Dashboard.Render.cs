using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FleetConnect.Tui;

internal sealed partial class Dashboard
{
    internal IRenderable BuildLayout()
    {
        int width = Math.Max(72, console.Profile.Width);
        int height = Math.Max(22, console.Profile.Height);
        int logRows = Math.Clamp(height - 20, 4, 9);
        int bodyRows = Math.Max(6, height - 3 - (logRows + 2) - 3 - 2);
        int inspectorWidth = Math.Clamp(width * 45 / 100, 34, 52);
        int leftInnerWidth = Math.Max(24, width - inspectorWidth - 6);
        int rightInnerWidth = Math.Max(24, inspectorWidth - 6);

        var layout = new Layout("root").SplitRows(
            new Layout("header").Size(3),
            new Layout("body"),
            new Layout("log").Size(logRows + 2),
            new Layout("footer").Size(3));

        layout["body"].SplitColumns(
            new Layout("machines"),
            new Layout("inspector").Size(inspectorWidth));

        layout["header"].Update(new Panel(new Markup(HeaderMarkup(width - 6)))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .Expand());

        layout["machines"].Update(MachinesPanel(bodyRows, leftInnerWidth));
        layout["inspector"].Update(InspectorPanel(bodyRows, rightInnerWidth));

        int logWidth = Math.Max(20, width - 6);
        var tail = log.Tail(logRows);
        var logRenderables = tail.Count == 0
            ? new IRenderable[] { new Markup("[grey]Ready.[/]") }
            : tail.Select(l => (IRenderable)new Markup("[grey70]" + Markup.Escape(Fit(l, logWidth)) + "[/]")).ToArray();

        layout["log"].Update(new Panel(new Rows(logRenderables))
            .Header("[cyan] activity [/]")
            .Border(BoxBorder.Rounded)
            .Expand());

        layout["footer"].Update(new Panel(new Markup(FooterMarkup()))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(confirm != ConfirmKind.None ? Color.Yellow : Color.Grey35))
            .Expand());

        return layout;
    }

    string HeaderMarkup(int maxWidth)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";
        if (progress != null)
            return $"[bold cyan1]fleet-connect[/] [grey]{Markup.Escape(version)}[/]   {RenderBar(progress, Math.Max(16, maxWidth - 36))}";

        string counts;
        if (pcs.Count == 0)
        {
            counts = "[grey]0 machines[/]";
        }
        else if (Tailnet.NoStatus)
        {
            counts = $"[white]{pcs.Count}[/] [grey]machines (status off)[/]";
        }
        else
        {
            int up = pcs.Count(p => p.Online == "up");
            int down = pcs.Count(p => p.Online == "down");
            counts = (up > 0 || down > 0)
                ? $"[white]{pcs.Count}[/] [grey]machines[/]  [green]{up} up[/]  [grey]{down} down[/]"
                : $"[white]{pcs.Count}[/] [grey]machines[/]";
        }

        string selfTag = tailnet?.Self != null && tailnet.Self.Name != ""
            ? $"  [grey]this:[/] [silver]{Markup.Escape(tailnet.Self.Name)}[/]"
            : "";
        string spin = refreshingStatus ? "  [grey]syncing tailnet…[/]" : "";
        string note = Banner;
        string bannerPart = note != "" ? $"   [yellow]{Markup.Escape(Fit(note, 44))}[/]" : "";

        return $"[bold cyan1]fleet-connect[/] [grey]{Markup.Escape(version)}[/]   {counts}{selfTag}{spin}{bannerPart}";
    }

    static string RenderBar(ProgressReport report, int room)
    {
        int pct = Math.Clamp(report.Percent, 0, 100);
        const int barLen = 18;
        int filled = pct * barLen / 100;
        string bar = "[cyan1]" + new string('#', filled) + "[/][grey30]" + new string('-', barLen - filled) + "[/]";
        string label = Fit(report.Label ?? "", Math.Max(12, room - barLen - 8));
        return $"{bar} [white]{pct,3}%[/]  {Markup.Escape(label)}";
    }

    Panel MachinesPanel(int capacity, int innerWidth)
    {
        var visible = FilteredPcs();
        ClampSelection();

        string headerText;
        if (filtering)
            headerText = $"[cyan] machines [/][black on cyan1] /{Markup.Escape(filter)}| [/]";
        else if (!string.IsNullOrEmpty(filter))
            headerText = $"[cyan] machines ({visible.Count}/{pcs.Count}) [/][yellow]/{Markup.Escape(filter)}[/] ";
        else
            headerText = $"[cyan] machines ({pcs.Count}) [/]";

        IRenderable content;
        if (pcs.Count == 0)
        {
            content = new Rows(
                new Markup("[grey]No machines in pcs.csv yet.[/]"),
                new Text(""),
                new Markup("  [cyan]I[/]  Import machines from Tailscale"),
                new Markup("  [cyan]A[/]  Add a machine manually"),
                new Markup("  [cyan]O[/]  Open pcs.csv in external editor"),
                new Text(""),
                new Markup("[grey]" + Markup.Escape(Fit(CsvStore.GetListPath(), innerWidth)) + "[/]"));
        }
        else if (visible.Count == 0)
        {
            content = new Rows(
                new Markup($"[yellow]No machines match \"{Markup.Escape(filter)}\".[/]"),
                new Markup("[grey]Press[/] [cyan]Esc[/] [grey]to clear the filter.[/]"));
        }
        else
        {
            content = MachinesTable(visible, capacity, innerWidth);
        }

        return new Panel(content)
            .Header(headerText)
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(form == null ? Color.Cyan1 : Color.Grey42))
            .Expand();
    }

    Table MachinesTable(IReadOnlyList<PcModel> visible, int capacity, int innerWidth)
    {
        var table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty).RightAligned());

        bool anyStatus = visible.Any(p => p.Online != "");
        int nameCol = Math.Clamp(visible.Max(p => p.Name.Length), 6, 18);
        int addrRoom = Math.Max(8, innerWidth - nameCol - 14);

        var (first, last) = ListWindow.Around(selected, visible.Count, capacity);
        if (first > 0)
            table.AddRow(new Markup($"  [grey]↑ {first} more[/]"), new Markup(string.Empty));

        var self = tailnet?.Self;
        for (int i = first; i <= last; i++)
        {
            var pc = visible[i];
            bool focused = i == selected;
            string cursor = focused ? "[cyan1 bold]>[/] " : "  ";
            string dot = !anyStatus
                ? ""
                : pc.Online == "up"
                    ? "[green]●[/] "
                    : pc.Online == "down"
                        ? "[grey42]·[/] "
                        : "[grey35]?[/] ";

            string rawName = Fit(pc.Name, nameCol).PadRight(nameCol);
            string styledName = focused
                ? $"[cyan1 bold]{Markup.Escape(rawName)}[/]"
                : pc.Online == "up"
                    ? $"[white]{Markup.Escape(rawName)}[/]"
                    : $"[grey70]{Markup.Escape(rawName)}[/]";

            string targetText = pc.Address != "" ? pc.Address : ("@" + pc.SshAlias);
            if (Tailnet.IsSelf(pc, self))
                targetText = "(self) " + targetText;
            string addrPart = addrRoom >= 8
                ? "  [grey58]" + Markup.Escape(Fit(targetText, addrRoom)) + "[/]"
                : "";

            string protoBadge = pc.Protocol == PcProtocol.Ssh
                ? (focused ? "[bold springgreen2]ssh[/]" : "[springgreen2]ssh[/]")
                : (focused ? "[bold deepskyblue1]rdp[/]" : "[deepskyblue1]rdp[/]");
            string aliasMark = pc.SshAlias != "" ? "[grey50]*[/]" : " ";

            table.AddRow(
                new Markup(cursor + dot + styledName + addrPart),
                new Markup(aliasMark + protoBadge));
        }

        int below = visible.Count - 1 - last;
        if (below > 0)
            table.AddRow(new Markup($"  [grey]↓ {below} more[/]"), new Markup(string.Empty));

        return table;
    }

    internal static string Fit(string line, int width)
    {
        if (string.IsNullOrEmpty(line) || width <= 0)
            return "";
        var chars = line.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        }
        string flat = new string(chars);
        if (flat.Length <= width)
            return flat;
        return width == 1 ? "…" : flat.Substring(0, width - 1) + "…";
    }
}
