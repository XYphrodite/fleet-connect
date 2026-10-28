namespace FleetConnect.Tui;

internal sealed partial class Dashboard
{
    // Returns true when the Live loop should exit (either to quit when sshSession
    // is null, or to temporarily leave the alternate screen for an SSH session).
    internal bool HandleKey(ConsoleKeyInfo key, out PendingSshSession sshSession)
    {
        sshSession = null;

        if (form != null)
        {
            var outcome = form.HandleKey(key, pcs, out PcModel saved);
            if (outcome == PcFormAction.Cancelled)
            {
                form = null;
                Banner = "Edit cancelled";
            }
            else if (outcome == PcFormAction.Saved && saved != null)
            {
                ApplySavedForm(form, saved);
                form = null;
            }
            return false;
        }

        if (confirm != ConfirmKind.None)
        {
            HandleConfirmKey(key);
            return false;
        }

        if (filtering)
            return HandleFilterKey(key, out sshSession);

        if (key.Key == ConsoleKey.F1 || key.KeyChar == '?')
        {
            showHelp = !showHelp;
            return false;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            if (showHelp)
            {
                showHelp = false;
                return false;
            }
            if (!string.IsNullOrEmpty(filter))
            {
                string keepName = CurrentSelected()?.Name;
                filter = "";
                RestoreSelection(keepName);
                return false;
            }
            return true;
        }

        if (key.Key == ConsoleKey.F && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            filtering = true;
            return false;
        }

        var visible = FilteredPcs();

        // Alt+Up / Alt+Down or [ / ] reorder rows in pcs.csv when unfiltered.
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) &&
            (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.DownArrow))
        {
            MoveSelectedRow(key.Key == ConsoleKey.UpArrow ? -1 : 1);
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                StepSelection(-1, visible.Count);
                return false;
            case ConsoleKey.DownArrow:
                StepSelection(1, visible.Count);
                return false;
            case ConsoleKey.PageUp:
                JumpSelection(-8, visible.Count);
                return false;
            case ConsoleKey.PageDown:
                JumpSelection(8, visible.Count);
                return false;
            case ConsoleKey.Home:
                selected = 0;
                return false;
            case ConsoleKey.End:
                selected = Math.Max(0, visible.Count - 1);
                return false;
            case ConsoleKey.F5:
                StartStatusRefresh(manual: true);
                return false;
            case ConsoleKey.Enter:
                return ConnectSelected(overrideProtocol: null, out sshSession);
            case ConsoleKey.Delete:
                PromptDeleteSelected();
                return false;
        }

        char ch = key.KeyChar;
        if (ch == '/')
        {
            filtering = true;
            return false;
        }
        if (ch == '[')
        {
            MoveSelectedRow(-1);
            return false;
        }
        if (ch == ']')
        {
            MoveSelectedRow(1);
            return false;
        }
        if (ch >= '1' && ch <= '9')
        {
            int idx = ch - '1';
            if (idx < visible.Count)
                selected = idx;
            return false;
        }

        switch (char.ToLowerInvariant(ch))
        {
            case 'q':
                return true;
            case 'j':
                StepSelection(1, visible.Count);
                return false;
            case 'k':
                StepSelection(-1, visible.Count);
                return false;
            case 'r':
                return ConnectSelected(PcProtocol.Rdp, out sshSession);
            case 's':
                return ConnectSelected(PcProtocol.Ssh, out sshSession);
            case 'a':
                OpenForm(PcFormMode.Add, null);
                return false;
            case 'e':
                if (CurrentSelected() != null)
                    OpenForm(PcFormMode.Edit, CurrentSelected());
                return false;
            case 'c':
                if (CurrentSelected() != null)
                    OpenForm(PcFormMode.Clone, CurrentSelected());
                return false;
            case 'd':
                PromptDeleteSelected();
                return false;
            case 'i':
                StartImportTask();
                return false;
            case 'y':
                PromptSync();
                return false;
            case 'u':
                StartUpdateTask();
                return false;
            case 'o':
                OpenExternalEditor();
                return false;
        }

        return false;
    }

    bool HandleFilterKey(ConsoleKeyInfo key, out PendingSshSession sshSession)
    {
        sshSession = null;
        var visible = FilteredPcs();
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                string keep = CurrentSelected()?.Name;
                filter = "";
                filtering = false;
                RestoreSelection(keep);
                return false;
            case ConsoleKey.Tab:
                filtering = false;
                return false;
            case ConsoleKey.Enter:
                filtering = false;
                return ConnectSelected(overrideProtocol: null, out sshSession);
            case ConsoleKey.UpArrow:
                StepSelection(-1, visible.Count);
                return false;
            case ConsoleKey.DownArrow:
                StepSelection(1, visible.Count);
                return false;
            case ConsoleKey.Delete:
            case ConsoleKey.Backspace when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                filter = "";
                ClampSelection();
                return false;
            case ConsoleKey.Backspace:
                if (filter.Length > 0)
                {
                    filter = filter.Substring(0, filter.Length - 1);
                    selected = 0;
                }
                else
                {
                    filtering = false;
                }
                return false;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    filter += key.KeyChar;
                    selected = 0;
                }
                return false;
        }
    }

    void HandleConfirmKey(ConsoleKeyInfo key)
    {
        char ch = char.ToLowerInvariant(key.KeyChar);
        if (key.Key == ConsoleKey.Escape || ch == 'n')
        {
            confirm = ConfirmKind.None;
            Banner = "Cancelled";
            return;
        }

        if (confirm == ConfirmKind.DeleteSelected)
        {
            if (ch == 'y' || key.Key == ConsoleKey.Enter)
            {
                confirm = ConfirmKind.None;
                DeleteSelectedConfirmed();
            }
            return;
        }

        if (confirm == ConfirmKind.SyncAll)
        {
            if (ch == 'y' || key.Key == ConsoleKey.Enter)
            {
                confirm = ConfirmKind.None;
                StartSyncTask(dryRun: false);
            }
            else if (ch == 'd')
            {
                confirm = ConfirmKind.None;
                StartSyncTask(dryRun: true);
            }
        }
    }

    void StepSelection(int delta, int count)
    {
        if (count <= 0) { selected = 0; return; }
        selected = (selected + delta + count) % count;
    }

    void JumpSelection(int delta, int count)
    {
        if (count <= 0) { selected = 0; return; }
        selected = Math.Clamp(selected + delta, 0, count - 1);
    }

    bool ConnectSelected(PcProtocol? overrideProtocol, out PendingSshSession sshSession)
    {
        sshSession = null;
        var pc = CurrentSelected();
        if (pc == null)
        {
            Banner = "No machine selected";
            return false;
        }

        PcProtocol proto = overrideProtocol ?? pc.Protocol;
        if (pc.Address == "" && pc.SshAlias == "")
        {
            string msg = pc.Name + " has neither a host nor an ssh alias.";
            log.Add(msg);
            Banner = msg;
            return false;
        }

        if (proto == PcProtocol.Rdp)
        {
            if (pc.Address == "")
            {
                string msg = pc.Name + " has no Host address for RDP (only SSH alias '" + pc.SshAlias + "').";
                log.Add(msg);
                Banner = msg;
                return false;
            }
            Connect.PrepareRdp(pc, out string rdpExe, out string rdpArgs, out string summary, out _);
            if (Util.RunDetached(rdpExe, rdpArgs) < 0)
            {
                string err = "Failed to start mstsc.exe for " + pc.Name;
                log.Add(err);
                Banner = err;
            }
            else
            {
                log.Add("RDP -> " + pc.Name + "  (" + summary + ")");
                Banner = "Launched RDP: " + pc.Name;
            }
            return false;
        }

        if (!Connect.PrepareSsh(pc, out string sshExe, out string sshArgs, out string sshSummary, out string error))
        {
            log.Add(error);
            Banner = error;
            return false;
        }

        log.Add("SSH -> " + pc.Name + "  (" + sshSummary + ")");
        sshSession = new PendingSshSession
        {
            PcName = pc.Name,
            Exe = sshExe,
            Args = sshArgs,
            Summary = sshSummary,
        };
        return true;
    }
}
