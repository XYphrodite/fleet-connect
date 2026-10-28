using System.Diagnostics;
using Spectre.Console;

namespace FleetConnect.Tui;

internal enum ConfirmKind
{
    None,
    DeleteSelected,
    SyncAll,
}

internal sealed class PendingSshSession
{
    public string PcName = "";
    public string Exe = "";
    public string Args = "";
    public string Summary = "";
}

internal sealed class ProgressReport
{
    public string Label = "";
    public int Percent;
}

// Full-screen split-view launcher & manager modeled after dstfarm's Dashboard.
internal sealed partial class Dashboard
{
    const string EnterAlternateScreen = "\x1b[?1049h\x1b[H";
    const string LeaveAlternateScreen = "\x1b[?1049l";

    readonly IAnsiConsole console;
    readonly LogBuffer log = new LogBuffer();
    readonly PortProbe portProbe = new PortProbe();
    readonly object stateLock = new object();

    List<PcModel> pcs = new List<PcModel>();
    List<string> localAliases = new List<string>();
    Dictionary<string, SshAliasInfo> sshConfigMap =
        new Dictionary<string, SshAliasInfo>(StringComparer.OrdinalIgnoreCase);
    TailnetSnapshot tailnet;
    DateTimeOffset tailnetCheckedAt = DateTimeOffset.MinValue;
    DateTime lastCsvWriteUtc = DateTime.MinValue;
    DateTimeOffset lastCsvPollAt = DateTimeOffset.MinValue;

    int selected;
    string filter = "";
    bool filtering;
    bool showHelp;
    PcForm form;
    ConfirmKind confirm = ConfirmKind.None;

    bool busy;
    bool refreshingStatus;
    ProgressReport progress;
    string banner = "";
    DateTimeOffset bannerAt = DateTimeOffset.MinValue;
    volatile bool dirty = true;

    public Dashboard(IAnsiConsole console = null)
    {
        this.console = console ?? AnsiConsole.Console;
        portProbe.Disabled = Tailnet.NoStatus;
    }

    string Banner
    {
        get => (DateTimeOffset.UtcNow - bannerAt) < TimeSpan.FromSeconds(12) ? banner : "";
        set
        {
            banner = value ?? "";
            bannerAt = DateTimeOffset.UtcNow;
            dirty = true;
        }
    }

    public int Run()
    {
        ReloadLocalState(initial: true);
        if (!Tailnet.NoStatus)
            StartStatusRefresh(manual: false);

        while (true)
        {
            PendingSshSession pendingSsh = null;
            dirty = true;
            console.Write(new ControlCode(EnterAlternateScreen));
            try
            {
                console.Live(new Text(string.Empty))
                    .AutoClear(true)
                    .Overflow(VerticalOverflow.Crop)
                    .Start(ctx => pendingSsh = RunLiveLoop(ctx));
            }
            finally
            {
                console.Write(new ControlCode(LeaveAlternateScreen));
            }

            if (pendingSsh == null)
                return ExitCodes.Ok;

            Render.Note(pendingSsh.Summary);
            var sw = Stopwatch.StartNew();
            int code = Util.RunInherited(pendingSsh.Exe, pendingSsh.Args);
            sw.Stop();
            if (code < 0)
            {
                log.Add("SSH failed to start: " + pendingSsh.Summary);
                Banner = "Failed to launch ssh.exe";
            }
            else
            {
                log.Add("SSH " + pendingSsh.PcName + " finished (exit " + code + ", " +
                        FormatDuration(sw.Elapsed) + ")");
            }
            ReloadSshMetadata();
            PollExternalCsvChange(force: true);
            if (!Tailnet.NoStatus)
                StartStatusRefresh(manual: false);
        }
    }

    PendingSshSession RunLiveLoop(LiveDisplayContext ctx)
    {
        int lastW = -1, lastH = -1;
        long lastLogVer = -1;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - lastCsvPollAt) > TimeSpan.FromSeconds(1.5))
            {
                lastCsvPollAt = now;
                PollExternalCsvChange(force: false);
            }
            if (!Tailnet.NoStatus && !refreshingStatus &&
                (now - tailnetCheckedAt) > TimeSpan.FromSeconds(30))
            {
                StartStatusRefresh(manual: false);
            }

            int w = console.Profile.Width;
            int h = console.Profile.Height;
            long logVer = log.Version;
            if (dirty || w != lastW || h != lastH || logVer != lastLogVer)
            {
                dirty = false;
                lastW = w;
                lastH = h;
                lastLogVer = logVer;
                ctx.UpdateTarget(BuildLayout());
                ctx.Refresh();
            }

            if (!KeyAvailable())
            {
                Thread.Sleep(50);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            dirty = true;
            if (HandleKey(key, out PendingSshSession sshSession))
                return sshSession;
        }
    }

    static bool KeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch { return false; }
    }

    internal void ReloadLocalState(bool initial)
    {
        string path = CsvStore.GetListPath();
        lastCsvWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        string selectedName = CurrentSelected()?.Name;
        var loaded = CsvStore.ReadPcList(w => log.Add("CSV warning: " + w));
        lock (stateLock)
        {
            ApplyTailnetToList(loaded, tailnet?.Machines);
            pcs = loaded;
        }
        ReloadSshMetadata();
        RestoreSelection(selectedName);

        if (initial)
        {
            if (pcs.Count == 0)
                log.Add("List is empty (" + path + "). Press I to import from Tailscale or A to add a PC.");
            else
                log.Add("Loaded " + pcs.Count + " machine(s). Enter connects, R=RDP, S=SSH, F1=help.");
        }
        dirty = true;
    }

    void ReloadSshMetadata()
    {
        localAliases = Connect.GetLocalSshAliases();
        sshConfigMap = Connect.GetLocalSshConfigMap();
    }

    void PollExternalCsvChange(bool force)
    {
        if (form != null || busy)
            return;
        string path = CsvStore.GetListPath();
        DateTime diskUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (!force && diskUtc == lastCsvWriteUtc)
            return;
        if (diskUtc == lastCsvWriteUtc && !force)
            return;
        if (diskUtc != lastCsvWriteUtc)
        {
            ReloadLocalState(initial: false);
            log.Add("Reloaded pcs.csv from disk (" + pcs.Count + " machine(s)).");
        }
    }

    void SavePcList(string logLine, string selectName = null)
    {
        CsvStore.WritePcList(pcs);
        string path = CsvStore.GetListPath();
        lastCsvWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        lock (stateLock)
            ApplyTailnetToList(pcs, tailnet?.Machines);
        if (selectName != null)
            RestoreSelection(selectName);
        else
            ClampSelection();
        if (!string.IsNullOrEmpty(logLine))
            log.Add(logLine);
        dirty = true;
    }

    internal IReadOnlyList<PcModel> FilteredPcs()
    {
        if (string.IsNullOrWhiteSpace(filter))
            return pcs;
        string q = filter.Trim();
        return pcs.Where(p =>
            p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Address.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.User.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.SshAlias.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Note.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            Protocols.ToName(p.Protocol).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    internal PcModel CurrentSelected()
    {
        var visible = FilteredPcs();
        if (visible.Count == 0)
            return null;
        int idx = Math.Clamp(selected, 0, visible.Count - 1);
        return visible[idx];
    }

    void ClampSelection()
    {
        var visible = FilteredPcs();
        selected = visible.Count == 0 ? 0 : Math.Clamp(selected, 0, visible.Count - 1);
    }

    void RestoreSelection(string name)
    {
        var visible = FilteredPcs();
        if (visible.Count == 0)
        {
            selected = 0;
            return;
        }
        if (!string.IsNullOrEmpty(name))
        {
            for (int i = 0; i < visible.Count; i++)
            {
                if (string.Equals(visible[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    return;
                }
            }
        }
        selected = Math.Clamp(selected, 0, visible.Count - 1);
    }

    static void ApplyTailnetToList(List<PcModel> list, IReadOnlyList<TailnetMachine> machines)
    {
        if (list == null)
            return;
        foreach (var pc in list)
        {
            if (machines == null)
            {
                pc.Online = "";
                continue;
            }
            var m = Tailnet.FindMatch(pc, machines);
            pc.Online = m == null ? "" : (m.Online ? "up" : "down");
        }
    }

    void StartStatusRefresh(bool manual)
    {
        if (refreshingStatus)
            return;
        refreshingStatus = true;
        tailnetCheckedAt = DateTimeOffset.UtcNow;
        if (manual)
        {
            portProbe.Clear();
            ReloadSshMetadata();
            PollExternalCsvChange(force: false);
        }
        _ = Task.Run(() =>
        {
            try
            {
                var snap = Tailnet.NoStatus ? null : Tailnet.GetSnapshot();
                lock (stateLock)
                {
                    tailnet = snap;
                    ApplyTailnetToList(pcs, snap?.Machines);
                }
                if (manual)
                {
                    if (Tailnet.NoStatus)
                        log.Add("Refreshed local list (-NoStatus is active).");
                    else if (snap?.Machines == null)
                        log.Add("Tailscale status unavailable.");
                    else
                    {
                        int up = pcs.Count(p => p.Online == "up");
                        log.Add("Refreshed Tailscale status (" + up + "/" + pcs.Count + " online).");
                    }
                }
            }
            finally
            {
                tailnetCheckedAt = DateTimeOffset.UtcNow;
                refreshingStatus = false;
                dirty = true;
            }
        });
    }

    static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)
            return Math.Max(1, (int)Math.Round(elapsed.TotalSeconds)) + "s";
        if (elapsed.TotalHours < 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }
}
