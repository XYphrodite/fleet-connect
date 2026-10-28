namespace FleetConnect.Tui;

internal sealed partial class Dashboard
{
    void OpenForm(PcFormMode mode, PcModel seed)
    {
        if (busy)
        {
            Banner = "Wait for the current background task to finish";
            return;
        }
        ReloadSshMetadata();
        showHelp = false;
        form = new PcForm(mode, seed, localAliases);
    }

    void ApplySavedForm(PcForm completed, PcModel saved)
    {
        if (completed.Mode == PcFormMode.Edit && !string.IsNullOrEmpty(completed.OriginalName))
        {
            int idx = pcs.FindIndex(p => string.Equals(p.Name, completed.OriginalName, StringComparison.Ordinal));
            if (idx >= 0)
                pcs[idx] = saved;
            else
                pcs.Add(saved);
            SavePcList("Updated " + saved.Name + " (" + (saved.Address != "" ? saved.Address : saved.SshAlias) + ")",
                saved.Name);
            Banner = "Saved " + saved.Name;
            return;
        }

        pcs.Add(saved);
        SavePcList("Added " + saved.Name + " (" + (saved.Address != "" ? saved.Address : saved.SshAlias) + " " +
                   Protocols.ToName(saved.Protocol) + ")", saved.Name);
        Banner = "Added " + saved.Name;
    }

    void PromptDeleteSelected()
    {
        if (busy)
        {
            Banner = "Wait for the current background task to finish";
            return;
        }
        if (CurrentSelected() == null)
        {
            Banner = "No machine selected";
            return;
        }
        confirm = ConfirmKind.DeleteSelected;
    }

    void DeleteSelectedConfirmed()
    {
        var target = CurrentSelected();
        if (target == null)
            return;
        int visibleIdx = selected;
        pcs.RemoveAll(p => string.Equals(p.Name, target.Name, StringComparison.Ordinal));
        var nextVisible = FilteredPcs();
        selected = nextVisible.Count == 0 ? 0 : Math.Clamp(visibleIdx, 0, nextVisible.Count - 1);
        SavePcList("Deleted " + target.Name + " (backup kept in pcs.csv.bak)");
        Banner = "Deleted " + target.Name;
    }

    void MoveSelectedRow(int delta)
    {
        if (busy)
            return;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            Banner = "Clear filter (Esc) before reordering rows";
            return;
        }
        if (pcs.Count < 2)
            return;
        int idx = Math.Clamp(selected, 0, pcs.Count - 1);
        int target = idx + delta;
        if (target < 0 || target >= pcs.Count)
            return;
        var item = pcs[idx];
        pcs[idx] = pcs[target];
        pcs[target] = item;
        selected = target;
        SavePcList("Moved " + item.Name + (delta < 0 ? " up" : " down"), item.Name);
    }

    void OpenExternalEditor()
    {
        string path = CsvStore.GetListPath();
        if (!File.Exists(path))
        {
            path = CsvStore.NewEmptyList();
            lastCsvWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        string editor = Environment.GetEnvironmentVariable("FLEET_CONNECT_EDITOR");
        if (string.IsNullOrWhiteSpace(editor))
            editor = "notepad.exe";
        if (Util.RunDetached(editor, "\"" + path + "\"") < 0)
        {
            string err = "Failed to start editor: " + editor;
            log.Add(err);
            Banner = err;
            return;
        }
        log.Add("Opened " + path + " in " + editor + " (auto-reloads on save).");
        Banner = "Opened CSV in " + editor;
    }

    void StartImportTask()
    {
        if (busy)
        {
            Banner = "Another task is already running";
            return;
        }
        busy = true;
        progress = new ProgressReport { Label = "Importing from Tailscale...", Percent = 20 };
        dirty = true;
        _ = Task.Run(() =>
        {
            try
            {
                var snap = Tailnet.GetSnapshot();
                List<PcModel> snapshotList;
                lock (stateLock)
                {
                    if (snap != null)
                        tailnet = snap;
                    snapshotList = pcs.ToList();
                }

                if (!CmdSyncImport.BuildImportPlan(snap?.Machines, snapshotList, null,
                        out ImportPlan plan, out string error))
                {
                    log.Add("Import failed: " + error);
                    Banner = error;
                    return;
                }

                progress = new ProgressReport { Label = "Merging tailnet hosts...", Percent = 75 };
                dirty = true;

                if (!plan.UseNames)
                    log.Add("MagicDNS does not resolve here; storing 100.x addresses.");
                foreach (var s in plan.Skipped)
                    log.Add("Skipped mobile node " + s.Name + " (" + s.Os + ")");
                foreach (string a in plan.Added)
                    log.Add("Import " + a);
                foreach (string c in plan.Changed)
                    log.Add("Import " + c);

                if (plan.Added.Count == 0 && plan.Changed.Count == 0)
                {
                    lock (stateLock)
                        ApplyTailnetToList(pcs, snap?.Machines);
                    log.Add("Tailscale import: list is already up to date.");
                    Banner = "Nothing new to import";
                    return;
                }

                string keepName = CurrentSelected()?.Name;
                lock (stateLock)
                {
                    ApplyTailnetToList(plan.Merged, snap?.Machines);
                    pcs = plan.Merged;
                }
                SavePcList("Imported from Tailscale: +" + plan.Added.Count + " added, ~" +
                           plan.Changed.Count + " updated.", keepName);
                Banner = "Imported +" + plan.Added.Count + ", updated " + plan.Changed.Count;
            }
            catch (Exception ex)
            {
                log.Add("Import error: " + ex.Message);
                Banner = "Import failed";
            }
            finally
            {
                progress = null;
                busy = false;
                dirty = true;
            }
        });
    }

    void PromptSync()
    {
        if (busy)
        {
            Banner = "Another task is already running";
            return;
        }
        if (pcs.Count == 0)
        {
            Banner = "List is empty; nothing to sync";
            return;
        }
        confirm = ConfirmKind.SyncAll;
    }

    void StartSyncTask(bool dryRun)
    {
        if (busy)
            return;
        string localPath = CsvStore.GetListPath();
        if (!File.Exists(localPath))
        {
            Banner = "No local pcs.csv on disk yet";
            log.Add("Sync aborted: " + localPath + " does not exist.");
            return;
        }
        string ssh = Util.FindExe("ssh.exe");
        string scp = Util.FindExe("scp.exe");
        if (ssh == null)
        {
            Banner = "No ssh.exe on PATH";
            log.Add("Sync aborted: no ssh.exe on PATH.");
            return;
        }

        busy = true;
        progress = new ProgressReport { Label = "Preparing sync...", Percent = 5 };
        dirty = true;

        var snapshotPcs = pcs.ToList();
        var aliasesSnapshot = localAliases.ToList();

        _ = Task.Run(() =>
        {
            try
            {
                var self = tailnet?.Self ?? Tailnet.GetSelf();
                var targets = CmdSyncImport.FilterSelfFromTargets(snapshotPcs, self,
                    skipped => log.Add("Sync: skipping self (" + skipped + ")"));
                if (targets.Count == 0)
                {
                    log.Add("Sync: nothing to push (only self in list).");
                    Banner = "Nothing to sync";
                    return;
                }

                int ok = 0, fail = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    int pct = (i * 100) / targets.Count;
                    progress = new ProgressReport
                    {
                        Label = (dryRun ? "Dry-run " : "Syncing ") + target.Name +
                                " (" + (i + 1) + "/" + targets.Count + ")",
                        Percent = pct,
                    };
                    dirty = true;

                    if (dryRun)
                    {
                        string sshTarget = target.SshAlias != "" ? target.SshAlias
                            : target.User != "" ? target.User + "@" + target.Address : target.Address;
                        if (target.SshAlias != "" && !aliasesSnapshot.Contains(target.SshAlias))
                            log.Add("Warning: " + target.Name + " alias '" + target.SshAlias + "' is missing from ~/.ssh/config");
                        log.Add("Dry-run -> " + target.Name + " (" + sshTarget + ")");
                        ok++;
                        continue;
                    }

                    bool pushed = CmdSyncImport.PushCsvToTarget(target, localPath, ssh, scp,
                        aliasesSnapshot, out string dest, out string warn, out bool verified);
                    if (warn != null)
                        log.Add("Warning: " + warn);
                    if (pushed)
                    {
                        ok++;
                        log.Add("Synced -> " + target.Name + " (" + dest + ")" +
                                (verified ? " [verified]" : " [unverified]"));
                    }
                    else
                    {
                        fail++;
                        log.Add("Sync FAILED -> " + target.Name + " (" + dest + ")");
                    }
                }

                Banner = dryRun
                    ? "Dry-run complete: " + ok + " target(s)"
                    : fail == 0
                        ? "Synced " + ok + " host(s)"
                        : "Sync finished: " + ok + " ok, " + fail + " failed";
            }
            catch (Exception ex)
            {
                log.Add("Sync error: " + ex.Message);
                Banner = "Sync failed";
            }
            finally
            {
                progress = null;
                busy = false;
                dirty = true;
            }
        });
    }

    void StartUpdateTask()
    {
        if (busy)
        {
            Banner = "Another task is already running";
            return;
        }
        busy = true;
        progress = new ProgressReport { Label = "Checking for updates...", Percent = 5 };
        dirty = true;
        _ = Task.Run(() =>
        {
            try
            {
                int code = CmdSetupUpdate.InvokeUpdateFromTui(
                    (status, pct) =>
                    {
                        progress = new ProgressReport { Label = status, Percent = Math.Clamp(pct, 0, 100) };
                        dirty = true;
                    },
                    line => log.Add(line),
                    out string summary);
                Banner = code == ExitCodes.Ok ? "Update staged! Restart fcon to apply." : summary;
            }
            finally
            {
                progress = null;
                busy = false;
                dirty = true;
            }
        });
    }
}
