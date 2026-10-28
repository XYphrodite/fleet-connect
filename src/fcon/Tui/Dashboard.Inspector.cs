using Spectre.Console;
using Spectre.Console.Rendering;

namespace FleetConnect.Tui;

internal sealed partial class Dashboard
{
    Panel InspectorPanel(int capacity, int innerWidth)
    {
        if (form != null)
        {
            return new Panel(FormTable(form, innerWidth))
                .Header($"[cyan] {Markup.Escape(Fit(form.Title, innerWidth - 4))} [/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Cyan1))
                .Expand();
        }

        if (showHelp)
        {
            return new Panel(HelpTable(capacity, innerWidth))
                .Header("[cyan] keyboard shortcuts [/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Cyan1))
                .Expand();
        }

        var pc = CurrentSelected();
        if (pc == null)
        {
            return new Panel(new Markup("[grey]Select or add a machine to inspect its connection details.[/]"))
                .Header("[cyan] details [/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Grey42))
                .Expand();
        }

        return new Panel(DetailsTable(pc, innerWidth))
            .Header($"[cyan] {Markup.Escape(Fit(pc.Name, innerWidth - 4))} [/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey50))
            .Expand();
    }

    Table DetailsTable(PcModel pc, int innerWidth)
    {
        int valWidth = Math.Max(12, innerWidth - 13);
        var table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty).RightAligned());

        void Row(string key, string markupVal) =>
            table.AddRow(new Markup("[grey]" + Markup.Escape(key) + "[/]"), new Markup(markupVal));

        Row("host", pc.Address != "" ? Markup.Escape(Fit(pc.Address, valWidth)) : "[grey]—[/]");
        Row("default", pc.Protocol == PcProtocol.Ssh
            ? "[springgreen2]ssh[/] [grey](Enter)[/]"
            : "[deepskyblue1]rdp[/] [grey](Enter)[/]");
        Row("user", pc.User != "" ? Markup.Escape(Fit(pc.User, valWidth)) : "[grey]—[/]");

        if (pc.SshAlias == "")
        {
            Row("ssh alias", "[grey]—[/]");
        }
        else if (sshConfigMap.TryGetValue(pc.SshAlias, out SshAliasInfo aliasInfo))
        {
            string target = aliasInfo.HostName != "" ? aliasInfo.HostName : "in config";
            if (aliasInfo.Port != "")
                target += ":" + aliasInfo.Port;
            Row("ssh alias", $"[green]{Markup.Escape(Fit(pc.SshAlias, 14))}[/] [grey]({Markup.Escape(Fit(target, Math.Max(6, valWidth - 17)))})[/]");
        }
        else
        {
            Row("ssh alias", $"[yellow]{Markup.Escape(Fit(pc.SshAlias, Math.Max(8, valWidth - 10)))}[/] [red]missing[/]");
        }

        string portDisplay = pc.Port != ""
            ? Markup.Escape(pc.Port)
            : (pc.Protocol == PcProtocol.Ssh ? "[grey]22 (default)[/]" : "[grey]3389 (default)[/]");
        Row("port", portDisplay);

        var tsMatch = Tailnet.FindMatch(pc, tailnet?.Machines);
        if (Tailnet.NoStatus)
        {
            Row("tailscale", "[grey]disabled (-NoStatus)[/]");
        }
        else if (tsMatch != null)
        {
            string state = tsMatch.Online ? "[green]online[/]" : "[grey50]offline[/]";
            string os = tsMatch.Os != "" ? $" [grey]{Markup.Escape(tsMatch.Os)}[/]" : "";
            string ip = tsMatch.Address != "" && tsMatch.Address != pc.Address
                ? $" [grey]({Markup.Escape(tsMatch.Address)})[/]"
                : "";
            Row("tailscale", state + os + ip);
        }
        else
        {
            Row("tailscale", tailnet?.Machines == null ? "[grey]unavailable[/]" : "[grey]not in tailnet[/]");
        }

        if (!Tailnet.NoStatus)
        {
            if (PortProbe.ResolveRdpEndpoint(pc, out string rdpHost, out int rdpPort))
                Row("tcp " + rdpPort, FormatProbe(portProbe.Query(rdpHost, rdpPort, () => dirty = true)));
            if (PortProbe.ResolveSshEndpoint(pc, sshConfigMap, out string sshHost, out int sshPort))
                Row("tcp " + sshPort, FormatProbe(portProbe.Query(sshHost, sshPort, () => dirty = true)));
        }

        string rdpCmd = pc.Address == ""
            ? "[grey]—[/]"
            : Markup.Escape(Fit("mstsc /v:" + pc.Address + (pc.Port != "" && pc.Protocol == PcProtocol.Rdp ? ":" + pc.Port : ""), valWidth));
        string sshCmd = pc.SshAlias != ""
            ? Markup.Escape(Fit("ssh " + pc.SshAlias, valWidth))
            : pc.Address != ""
                ? Markup.Escape(Fit("ssh " + (pc.User != "" ? pc.User + "@" : "") + pc.Address +
                                    (pc.Port != "" && pc.Protocol == PcProtocol.Ssh ? " -p " + pc.Port : ""), valWidth))
                : "[grey]—[/]";

        Row("rdp [R]", "[grey70]" + rdpCmd + "[/]");
        Row("ssh [S]", "[grey70]" + sshCmd + "[/]");

        if (pc.Note != "")
            Row("note", "[italic silver]" + Markup.Escape(Fit(pc.Note, valWidth)) + "[/]");

        return table;
    }

    static string FormatProbe(PortStatus s) => s.State switch
    {
        ProbeState.Checking => "[grey]checking…[/]",
        ProbeState.Open => $"[green]open[/] [grey]({s.LatencyMs} ms)[/]",
        ProbeState.Closed => "[grey50]closed / filtered[/]",
        _ => "[grey]—[/]",
    };

    static Table FormTable(PcForm activeForm, int innerWidth)
    {
        int valWidth = Math.Max(10, innerWidth - 15);
        var table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty).RightAligned());

        for (int i = 0; i < PcForm.FieldCount; i++)
        {
            bool focused = i == activeForm.SelectedField;
            if (i == PcForm.FieldSave)
            {
                table.AddRow(new Markup(""), new Markup(""));
                string btn = focused
                    ? "[black on cyan1] > Save machine (Enter) [/]"
                    : "[cyan]  Save machine (Ctrl+S)[/]";
                table.AddRow(new Markup(btn), new Markup(""));
                continue;
            }

            string label = focused
                ? $"[cyan1 bold]> {Markup.Escape(PcForm.FieldLabel(i))}[/]"
                : $"  [grey]{Markup.Escape(PcForm.FieldLabel(i))}[/]";

            string valMarkup;
            if (focused && activeForm.EditingBuffer != null)
            {
                string shown = Fit(activeForm.EditingBuffer, Math.Max(4, valWidth - 2));
                valMarkup = $"[black on cyan1]{Markup.Escape(shown)}|[/]";
            }
            else if (i == PcForm.FieldProtocol)
            {
                string p = Protocols.ToName(activeForm.Protocol);
                valMarkup = focused ? $"[cyan1]◀[/] [bold white]{p}[/] [cyan1]▶[/]" : $"[white]{p}[/]";
            }
            else
            {
                string raw = activeForm.GetFieldValue(i);
                if (raw == "")
                    valMarkup = (i == PcForm.FieldSshAlias && focused) ? "[cyan1]◀[/] [grey](none)[/] [cyan1]▶[/]" : "[grey]—[/]";
                else
                    valMarkup = Markup.Escape(Fit(raw, valWidth));
            }

            table.AddRow(new Markup(label), new Markup(valMarkup));
        }

        if (!string.IsNullOrEmpty(activeForm.Error))
        {
            table.AddRow(new Markup(""), new Markup(""));
            table.AddRow(new Markup("[red]" + Markup.Escape(Fit(activeForm.Error, innerWidth - 2)) + "[/]"), new Markup(""));
        }

        return table;
    }

    static Table HelpTable(int capacity, int innerWidth)
    {
        var rows = new (string Key, string Desc)[]
        {
            ("↑ / ↓, J / K", "Select machine"),
            ("1..9", "Jump to row 1..9"),
            ("Enter", "Connect (default proto)"),
            ("R", "Launch RDP (stays in TUI)"),
            ("S", "Open SSH (returns on exit)"),
            ("/ or Ctrl+F", "Live search / filter"),
            ("A / E / C", "Add / Edit / Clone PC"),
            ("D or Del", "Delete selected PC"),
            ("[ / ]", "Move PC up / down"),
            ("I", "Import from Tailscale"),
            ("Y", "Sync pcs.csv to remotes"),
            ("O", "Open pcs.csv in editor"),
            ("F5", "Refresh status & probes"),
            ("U", "Self-update from GitHub"),
            ("F1 or ?", "Toggle this help"),
            ("Q or Esc", "Quit fleet-connect"),
        };

        var table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty));
        table.AddColumn(new TableColumn(string.Empty).RightAligned());

        int take = Math.Min(rows.Length, Math.Max(6, capacity));
        for (int i = 0; i < take; i++)
        {
            table.AddRow(
                new Markup("[cyan]" + Markup.Escape(rows[i].Key) + "[/]"),
                new Markup("[grey82]" + Markup.Escape(Fit(rows[i].Desc, Math.Max(10, innerWidth - 15))) + "[/]"));
        }
        return table;
    }

    string FooterMarkup()
    {
        if (confirm == ConfirmKind.DeleteSelected)
        {
            string name = CurrentSelected()?.Name ?? "machine";
            return $"[yellow]Delete '{Markup.Escape(name)}' from pcs.csv?[/]   [cyan]Y / Enter[/] confirm   [cyan]N / Esc[/] cancel";
        }
        if (confirm == ConfirmKind.SyncAll)
            return $"[yellow]Push pcs.csv to remote hosts?[/]   [cyan]Y / Enter[/] sync   [cyan]D[/] dry-run   [cyan]N / Esc[/] cancel";
        if (form != null && form.IsEditingText)
            return "[cyan]Enter[/] apply   [cyan]Tab[/] next field   [cyan]Del[/] clear   [cyan]Ctrl+S[/] save   [cyan]Esc[/] cancel";
        if (form != null)
            return "[cyan]↑↓/Tab[/] field   [cyan]Enter[/] edit   [cyan]←→[/] toggle   [cyan]Ctrl+S[/] save   [cyan]Esc[/] cancel";
        if (filtering)
            return "[cyan]type[/] filter   [cyan]↑↓[/] select   [cyan]Enter[/] connect   [cyan]Tab[/] keep filter   [cyan]Esc[/] clear";

        return "[cyan]Enter[/] connect  [cyan]R[/] rdp  [cyan]S[/] ssh  [cyan]/[/] filter  [cyan]A[/] add  [cyan]E[/] edit  [cyan]D[/] del  [cyan]I[/] import  [cyan]Y[/] sync  [cyan]F1[/] help  [cyan]Q[/] quit";
    }
}
