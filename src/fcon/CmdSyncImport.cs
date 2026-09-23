// Sync (push pcs.csv to remotes) and import (merge the tailnet into the list).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FleetConnect;

static class CmdSyncImport
{
    // ------------------------------------------------------------------ sync

    public static int InvokeSync(List<string> syncArgs)
    {
        if (syncArgs.Count == 1 && Util.IsHelpCI(syncArgs[0]))
        {
            Console.WriteLine();
            Render.Paint("  fcon sync - push pcs.csv to remote machines", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine("    fcon sync                          push to all known machines (except self)");
            Console.WriteLine("    fcon sync mks68 xeon home-pc       push to listed names");
            Console.WriteLine("    fcon sync --dry-run                show what would be pushed");
            Console.WriteLine();
            Console.WriteLine("  Uses ssh/scp (OpenSSH). For each target:");
            Console.WriteLine("    - creates %LOCALAPPDATA%\\fleet-connect if missing");
            Console.WriteLine("    - tries scp, falls back to \"more\" pipe for hosts without sftp");
            Console.WriteLine("  SshAlias from the list is used when present, otherwise User@Host.");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        string localPath = CsvStore.GetListPath();
        if (!File.Exists(localPath))
        {
            Render.Fail("No local list at " + localPath);
            return ExitCodes.Error;
        }
        var allPcs = CsvStore.ReadPcList();
        if (allPcs.Count == 0)
        {
            Render.Fail("List is empty");
            return ExitCodes.Error;
        }

        bool dryRun = false;
        var wants = new List<string>();
        foreach (string a in syncArgs)
        {
            if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
                dryRun = true;
            else
                wants.Add(a);
        }

        var targets = new List<PcModel>();
        if (wants.Count == 0)
        {
            targets.AddRange(allPcs);
        }
        else
        {
            foreach (string want in wants)
            {
                var hits = allPcs.Where(p => p.Name == want).ToList();
                if (hits.Count == 0)
                    hits = allPcs.Where(p => p.Name.StartsWith(want, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0)
                    hits = allPcs.Where(p => p.Address == want).ToList();
                if (hits.Count == 0)
                {
                    Render.Fail("No machine matches '" + want + "'");
                    return ExitCodes.Error;
                }
                if (hits.Count > 1)
                {
                    Render.Fail("'" + want + "' matches multiple: " + string.Join(", ", hits.Select(h => h.Name)));
                    return ExitCodes.Error;
                }
                targets.Add(hits[0]);
            }
        }

        // Skip self: detect via Tailscale Self DNSName/Address/HostName.
        try
        {
            var self = Tailnet.GetSelf();
            if (self != null && (self.DnsName != "" || self.Address != ""))
            {
                var filtered = new List<PcModel>();
                foreach (var t in targets)
                {
                    bool isSelf = false;
                    if (self.DnsName != "" &&
                        (t.Address == self.DnsName || t.Address == self.DnsName + "."))
                        isSelf = true;
                    if (self.Address != "" && t.Address == self.Address)
                        isSelf = true;
                    // Same box under the operator's own list name.
                    if (self.HostName != "" &&
                        (t.Name.Equals(self.HostName, StringComparison.OrdinalIgnoreCase) ||
                         t.Address.StartsWith(self.HostName + ".", StringComparison.OrdinalIgnoreCase)))
                        isSelf = true;
                    if (isSelf)
                    {
                        Render.Note("Skipping self " + t.Name);
                        continue;
                    }
                    filtered.Add(t);
                }
                targets = filtered;
            }
        }
        catch { }

        if (targets.Count == 0)
        {
            Render.Note("Nothing to sync.");
            return ExitCodes.Ok;
        }

        string ssh = Util.FindExe("ssh.exe");
        string scp = Util.FindExe("scp.exe");
        if (ssh == null)
        {
            Render.Fail("No ssh.exe on PATH.");
            return ExitCodes.Error;
        }

        Console.WriteLine();
        int lineCount = File.ReadAllLines(localPath).Length;
        Render.Note("Local: " + localPath + " (" + lineCount + " lines)");
        if (dryRun)
            Render.Paint("  Dry run - no files will be written", ConsoleColor.Yellow);
        Console.WriteLine();

        int failed = 0, okCount = 0;
        var localAliases = Connect.GetLocalSshAliases();
        foreach (var pc in targets)
        {
            if (pc.SshAlias != "" && !localAliases.Contains(pc.SshAlias))
            {
                Render.Paint("  !! " + pc.Name + ": ssh alias '" + pc.SshAlias + "' is not in ~/.ssh/config here;",
                    ConsoleColor.Yellow);
                Render.Paint("     sync copies only pcs.csv, run setup-client for this alias first.",
                    ConsoleColor.Yellow);
            }
            string sshTarget = pc.SshAlias != "" ? pc.SshAlias
                : pc.User != "" ? pc.User + "@" + pc.Address : pc.Address;
            string portSsh = (pc.Port != "" && pc.SshAlias == "") ? "-p " + pc.Port + " " : "";
            string portScp = (pc.Port != "" && pc.SshAlias == "") ? "-P " + pc.Port + " " : "";

            Console.Write("  -> " + pc.Name + " (" + sshTarget + ") ...");
            if (dryRun)
            {
                Render.Paint(" dry-run", ConsoleColor.DarkGray);
                okCount++;
                continue;
            }

            // 1) ensure remote dir exists.
            try
            {
                Util.RunCapture(ssh, portSsh + Quote(sshTarget) + " " +
                    Quote("mkdir \"%LOCALAPPDATA%\\fleet-connect\" 2>nul & echo ok"));
            }
            catch { }

            bool pushed = false;

            // 2) push via 'more' pipe - works on all Windows OpenSSH hosts,
            //    even when sftp is disabled.
            try
            {
                if (PipeFileToSsh(ssh, portSsh + Quote(sshTarget) + " " +
                        Quote("more > \"%LOCALAPPDATA%\\fleet-connect\\pcs.csv\""), localPath))
                    pushed = true;
            }
            catch { }

            // 3) fallback to scp if the pipe failed and scp is available.
            // NOTE: the remote home is derived from the list's User; the PS
            // version hardcoded the author's own profile dir here.
            if (!pushed && scp != null)
            {
                string remoteUser = pc.User != "" ? pc.User : "local";
                string dest = sshTarget + ":C:/Users/" + remoteUser + "/AppData/Local/fleet-connect/pcs.csv";
                try
                {
                    var scpRun = Util.RunCapture(scp, "-o StrictHostKeyChecking=accept-new " +
                        portScp + Quote(localPath) + " " + dest);
                    if (scpRun.ExitCode == 0)
                        pushed = true;
                }
                catch { }
            }

            if (pushed)
            {
                bool verifyOk = false;
                try
                {
                    var head = Util.RunCapture(ssh, portSsh + Quote(sshTarget) + " " +
                        Quote("type \"%LOCALAPPDATA%\\fleet-connect\\pcs.csv\""));
                    string first = (head.StdOut ?? "").Split('\n')[0];
                    if (Regex.IsMatch(first, "Name.*Host"))
                        verifyOk = true;
                }
                catch { }
                if (verifyOk)
                    Render.Paint(" ok", ConsoleColor.Green);
                else
                    Render.Paint(" ok (unverified)", ConsoleColor.Yellow);
                okCount++;
            }
            else
            {
                Render.Paint(" failed", ConsoleColor.Red);
                failed++;
            }
        }

        Console.WriteLine();
        if (failed > 0)
        {
            Render.Fail(failed + " of " + targets.Count + " failed.");
            return ExitCodes.Error;
        }
        Render.Paint("  Synced " + okCount + " host(s).", ConsoleColor.Green);
        Console.WriteLine();
        return ExitCodes.Ok;
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    static bool PipeFileToSsh(string ssh, string args, string filePath)
    {
        var psi = new ProcessStartInfo(ssh, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var proc = Process.Start(psi);
        if (proc == null)
            return false;
        try
        {
            using (var writer = proc.StandardInput)
                writer.Write(File.ReadAllText(filePath));
        }
        catch
        {
            return false;
        }
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }

    // ---------------------------------------------------------------- import

    public static int InvokeImport()
    {
        var machines = Tailnet.GetTailnetMachines();
        if (machines == null)
        {
            Render.Fail("Could not read \"tailscale status --json\". Is Tailscale installed and up?");
            return ExitCodes.Error;
        }

        // Phones and tablets are in the tailnet too, and neither answers RDP or SSH.
        var mobile = new Regex("^(?i)(ios|android|tvos)$");
        var skipped = machines.Where(m => mobile.IsMatch(m.Os)).ToList();
        machines = machines.Where(m => !mobile.IsMatch(m.Os)).ToList();
        if (machines.Count == 0)
        {
            Render.Fail("The tailnet has no machine this tool could connect to.");
            return ExitCodes.Error;
        }

        bool useNames = Tailnet.TestMagicDnsWorks(machines[0].DnsName);

        var existing = CsvStore.ReadPcList();
        var added = new List<string>();
        var changed = new List<string>();

        foreach (var m in machines)
        {
            string address = (useNames && m.DnsName != "") ? m.DnsName : m.Address;

            // Matched on the name and on either form of the address, so a
            // machine already in the list under an operator's own name is
            // updated rather than added twice.
            var match = existing.FirstOrDefault(p =>
                p.Name == m.Name || p.Address == m.Address || p.Address == m.DnsName);

            if (match != null)
            {
                if (match.Address != address)
                {
                    changed.Add("~ " + match.Name + ": " + match.Address + " -> " + address);
                    match.Address = address;
                }
                continue;
            }

            var pc = new PcModel
            {
                Name = m.Name,
                Address = address,
                // A Windows box is reached over RDP and a Linux one over SSH
                // far more often than the other way round.
                Protocol = Regex.IsMatch(m.Os, "^(?i)linux") ? PcProtocol.Ssh : PcProtocol.Rdp,
            };
            existing.Add(pc);
            added.Add("+ " + pc.Name + "  " + pc.Address + "  " + Protocols.ToName(pc.Protocol));
        }

        Console.WriteLine();
        if (!useNames)
            Render.Note("MagicDNS does not resolve here, so 100.x addresses are stored instead of names.");
        foreach (var s in skipped)
            Render.Note("- " + s.Name + " (" + s.Os + "), skipped");
        foreach (string line in added)
            Render.Paint("  " + line, ConsoleColor.Green);
        foreach (string line in changed)
            Render.Paint("  " + line, ConsoleColor.Yellow);

        if (added.Count == 0 && changed.Count == 0)
        {
            Render.Note("Nothing to change.");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        Console.WriteLine();
        if (!Program.Yes)
        {
            if (!Picker.Interactive())
            {
                Render.Note("Nothing written. Run with -Yes to apply this without being asked.");
                return ExitCodes.NoChoice;
            }
            Console.Write("  Write this to " + CsvStore.GetListPath() + "? [y/N]: ");
            string answer = Console.ReadLine() ?? "";
            if (!Regex.IsMatch(answer, "^(?i)y"))
            {
                Render.Note("Left alone.");
                return ExitCodes.NoChoice;
            }
        }
        CsvStore.WritePcList(existing);
        Render.Note("Written. The previous list is kept as " + CsvStore.GetListPath() + ".bak");
        Console.WriteLine();
        return ExitCodes.Ok;
    }
}
