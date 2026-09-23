// Setup (enable RDP/SSH on this machine, admin required) and update
// (self-update from a GitHub release asset).
using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FleetConnect;

static class CmdSetupUpdate
{
    // ---------------------------------------------------------------- setup

    public static int InvokeSetup(List<string> setupArgs)
    {
        if (setupArgs.Count == 1 && Util.IsHelpCI(setupArgs[0]))
        {
            Console.WriteLine();
            Render.Paint("  fcon setup - enable RDP and/or SSH on this machine", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine("    fcon setup                 interactive: ask for RDP and SSH separately");
            Console.WriteLine("    fcon setup --rdp           enable RDP only");
            Console.WriteLine("    fcon setup --ssh           enable SSH only");
            Console.WriteLine("    fcon setup --rdp --ssh     enable both");
            Console.WriteLine("    fcon setup --yes           assume yes to asked components (use with --rdp/--ssh)");
            Console.WriteLine();
            Console.WriteLine("  Requires Administrator. Strict mode:");
            Console.WriteLine("    - without admin -> error, nothing changed");
            Console.WriteLine("    - RDP on Windows Home -> error (use SSH only)");
            Console.WriteLine("    - firewall rules are configured automatically");
            Console.WriteLine("    - reboot is not required (RDP/SSH start immediately)");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        if (!IsAdmin())
        {
            Render.Fail("Administrator rights required. Run PowerShell as Administrator and try again.");
            return ExitCodes.Error;
        }

        bool? wantRdp = null, wantSsh = null;
        bool assumeYes = false;
        var unknown = new List<string>();
        foreach (string a in setupArgs)
        {
            string low = a.ToLowerInvariant();
            if (low == "--rdp" || low == "-rdp" || low == "rdp")
                wantRdp = true;
            else if (low == "--ssh" || low == "-ssh" || low == "ssh")
                wantSsh = true;
            else if (low == "--yes" || low == "-yes" || low == "yes")
                assumeYes = true;
            else if (low == "--all" || low == "-all" || low == "all")
            {
                wantRdp = true;
                wantSsh = true;
            }
            else
                unknown.Add(a);
        }
        if (unknown.Count > 0)
        {
            Render.Fail("Unknown option(s): " + string.Join(" ", unknown) + ". Use fcon setup --help");
            return ExitCodes.Error;
        }

        bool interactive = Picker.Interactive();
        if (wantRdp == null && wantSsh == null)
        {
            if (!interactive && !assumeYes)
            {
                Render.Fail("No component selected. Use --rdp and/or --ssh, or run interactively.");
                return ExitCodes.Error;
            }
            if (assumeYes)
            {
                wantRdp = true;
                wantSsh = true;
            }
            else
            {
                wantRdp = AskYes("  Enable RDP? [y/N]: ");
                wantSsh = AskYes("  Enable SSH? [y/N]: ");
            }
        }
        else if (!assumeYes)
        {
            if (wantRdp == null && interactive)
                wantRdp = AskYes("  Enable RDP? [y/N]: ");
            else if (wantRdp == null)
                wantRdp = false;
            if (wantSsh == null && interactive)
                wantSsh = AskYes("  Enable SSH? [y/N]: ");
            else if (wantSsh == null)
                wantSsh = false;
        }

        if (wantRdp != true && wantSsh != true)
        {
            Render.Note("Nothing selected. No changes made.");
            return ExitCodes.NoChoice;
        }

        int code = ExitCodes.Ok;
        if (wantRdp == true)
        {
            Console.WriteLine();
            Render.Paint("  Enabling RDP...", ConsoleColor.Cyan);
            if (EnableLocalRdp() != 0)
                code = ExitCodes.Error;
        }
        if (wantSsh == true)
        {
            Console.WriteLine();
            Render.Paint("  Enabling SSH...", ConsoleColor.Cyan);
            if (EnableLocalSsh() != 0)
                code = ExitCodes.Error;
        }

        if (code == ExitCodes.Ok)
        {
            Console.WriteLine();
            Render.Paint("  Setup complete.", ConsoleColor.Green);
            Render.Note("Verify: Get-Service TermService,sshd | Select Name,Status");
        }
        else
        {
            Console.WriteLine();
            Render.Fail("Setup finished with errors (see above). Strict mode: fix the error and run again.");
        }
        return code;
    }

    static bool AskYes(string prompt)
    {
        Console.Write(prompt);
        string ans = Console.ReadLine() ?? "";
        return Regex.IsMatch(ans, "^(?i)y");
    }

    static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    static bool IsHomeEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string;
            return product != null &&
                product.IndexOf("home", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    static int EnableLocalRdp()
    {
        if (IsHomeEdition())
        {
            Render.Fail("RDP host is not available on Windows Home. Upgrade to Pro/Enterprise or use SSH only.");
            return ExitCodes.Error;
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Control\Terminal Server", writable: true);
            if (key == null)
            {
                Render.Fail("Failed to enable RDP (registry): Terminal Server key not found.");
                return ExitCodes.Error;
            }
            key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Render.Fail("Failed to enable RDP (registry): " + ex.Message);
            return ExitCodes.Error;
        }
        var fw = Util.RunCapture("netsh.exe",
            "advfirewall firewall set rule group=\"Remote Desktop\" new enable=yes");
        if (fw.ExitCode != 0)
        {
            Render.Fail("Failed to enable firewall rule for Remote Desktop: " + FirstLine(fw.StdErr + fw.StdOut));
            return ExitCodes.Error;
        }
        try { Util.RunCapture("sc.exe", "start TermService"); } catch { }
        Render.Paint("  RDP enabled (port 3389, firewall Remote Desktop).", ConsoleColor.Green);
        return ExitCodes.Ok;
    }

    static int EnableLocalSsh()
    {
        string capability = FindSshCapability();
        if (capability == null)
        {
            Render.Fail("OpenSSH Server capability not found (dism).");
            return ExitCodes.Error;
        }
        if (!IsCapabilityInstalled(capability))
        {
            Render.Note("Installing OpenSSH Server (dism)...");
            var add = Util.RunCapture("dism.exe",
                "/Online /Add-Capability /CapabilityName:" + capability + " /NoRestart");
            if (add.ExitCode != 0)
            {
                Render.Fail("Failed to install OpenSSH Server: " + FirstLine(add.StdErr + add.StdOut));
                return ExitCodes.Error;
            }
        }
        try
        {
            Util.RunCapture("sc.exe", "config sshd start=auto");
            Util.RunCapture("sc.exe", "start sshd");
        }
        catch (Exception ex)
        {
            Render.Fail("Failed to start sshd: " + ex.Message);
            return ExitCodes.Error;
        }
        // Idempotent: adding a duplicate rule fails, which is fine when one exists.
        Util.RunCapture("netsh.exe",
            "advfirewall firewall add rule name=\"sshd\" dir=in action=allow protocol=TCP localport=22");
        Render.Paint("  SSH enabled (port 22, sshd Automatic, firewall sshd).", ConsoleColor.Green);
        return ExitCodes.Ok;
    }

    static string FindSshCapability()
    {
        try
        {
            var list = Util.RunCapture("dism.exe", "/Online /Get-Capabilities");
            if (list.ExitCode != 0)
                return null;
            foreach (string line in (list.StdOut ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Capability Identity :", StringComparison.OrdinalIgnoreCase) &&
                    t.IndexOf("OpenSSH.Server", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.Substring("Capability Identity :".Length).Trim();
            }
        }
        catch { }
        return null;
    }

    static bool IsCapabilityInstalled(string capability)
    {
        try
        {
            var info = Util.RunCapture("dism.exe", "/Online /Get-CapabilityInfo /CapabilityName:" + capability);
            return info.ExitCode == 0 &&
                (info.StdOut ?? "").IndexOf("State : Installed", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        int nl = text.IndexOf('\n');
        return (nl >= 0 ? text.Substring(0, nl) : text).Trim();
    }

    // --------------------------------------------------------------- update

    // The exe ships from a GitHub release asset, so FLEET_CONNECT_REF names a
    // release tag here ('latest' by default), not a git branch.
    public static int InvokeUpdate(List<string> updateArgs)
    {
        if (updateArgs.Count == 1 && Util.IsHelpCI(updateArgs[0]))
        {
            Console.WriteLine();
            Render.Paint("  fcon update - update fcon from GitHub", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine("    fcon update                update to the latest release");
            Console.WriteLine("    fcon update --check        only check if update is available");
            Console.WriteLine("    fcon update --help         this text");
            Console.WriteLine();
            Console.WriteLine("  Mirrors install.ps1: uses $env:FLEET_CONNECT_REPO / _REF / _DIR if set.");
            Console.WriteLine("  Shows progress bar and status text like the panel.");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        bool checkOnly = false;
        var unknown = new List<string>();
        foreach (string a in updateArgs)
        {
            if (a.Equals("--check", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-check", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("check", StringComparison.OrdinalIgnoreCase))
                checkOnly = true;
            else
                unknown.Add(a);
        }
        if (unknown.Count > 0)
        {
            Render.Fail("Unknown option(s): " + string.Join(" ", unknown) + ". Use fcon update --help");
            return ExitCodes.Error;
        }

        string repo = Environment.GetEnvironmentVariable("FLEET_CONNECT_REPO");
        if (string.IsNullOrWhiteSpace(repo))
            repo = "XYphrodite/fleet-connect";
        string tag = Environment.GetEnvironmentVariable("FLEET_CONNECT_REF");
        if (string.IsNullOrWhiteSpace(tag))
            tag = "latest";
        string target = Environment.GetEnvironmentVariable("FLEET_CONNECT_DIR");
        if (string.IsNullOrWhiteSpace(target))
            target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "fleet-connect");
        string source = tag.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com/" + repo + "/releases/latest/download/fcon.exe"
            : "https://github.com/" + repo + "/releases/download/" + tag + "/fcon.exe";

        try
        {
            Progress("Checking for updates...", 5);
            Console.WriteLine();
            Render.Paint("==> Checking for updates...", ConsoleColor.Cyan);
            Render.Note("    " + source);

            if (checkOnly)
            {
                Progress("Checking availability...", 40);
                if (UrlExists(source))
                {
                    ProgressDone();
                    Render.Paint("  Available: ", ConsoleColor.Green);
                    Render.Note("  " + source);
                    Render.Note("  Repository: " + repo + "  ref: " + tag);
                    return ExitCodes.Ok;
                }
                ProgressDone();
                Render.Fail("Failed to check update: " + source + " did not answer.");
                return ExitCodes.Error;
            }

            Progress("Downloading fcon.exe...", 25);
            Render.Paint("==> Downloading fcon.exe...", ConsoleColor.Cyan);
            Render.Note("    " + source);
            string tmp = Path.Combine(Path.GetTempPath(),
                "fcon-update-" + Guid.NewGuid().ToString("N") + ".exe");
            DownloadWithProgress(source, tmp);

            Progress("Verifying file...", 55);
            Render.Paint("==> Verifying file...", ConsoleColor.Cyan);
            if (!LooksLikeExe(tmp))
                throw new Exception("What came back from " + source +
                    " is not an executable. Is the release published?");
            Render.Note("    file is valid");

            Progress("Installing to " + target + "...", 80);
            Render.Paint("==> Installing to " + target + "...", ConsoleColor.Cyan);
            Directory.CreateDirectory(target);
            string exePath = Path.Combine(target, "fcon.exe");
            if (File.Exists(exePath))
                File.Copy(exePath, exePath + ".bak", overwrite: true);
            // The running exe is locked, so stage aside and let a detached
            // cmd move it into place after this process exits.
            string staged = Path.Combine(target, "fcon.new.exe");
            File.Copy(tmp, staged, overwrite: true);
            try { File.Delete(tmp); } catch { }
            foreach (string stale in new[] { "fcon.cmd", "fleet-connect.ps1", "fcon.ps1" })
            {
                try
                {
                    string p = Path.Combine(target, stale);
                    if (File.Exists(p))
                        File.Delete(p);
                }
                catch { }
            }
            Render.Note("    staged, restart swaps it in");
            SpawnSwap(staged, exePath);

            EnsureTargetOnPath(target);

            Progress("Done!", 100);
            ProgressDone();
            Console.WriteLine();
            Render.Paint("  fleet-connect staged: " + target, ConsoleColor.Green);
            Render.Note("  version: " + repo + "@" + tag);
            Render.Note("  machine list: " + CsvStore.GetListPath());
            Console.WriteLine();
            Render.Note("  Done. Restart the terminal; the new fcon.exe lands on next start.");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            try { ProgressDone(); } catch { }
            Console.WriteLine();
            Render.Fail(ex.Message);
            return ExitCodes.Error;
        }
    }

    static bool UrlExists(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    static void DownloadWithProgress(string url, string dest)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        using var net = resp.Content.ReadAsStream();
        using var file = File.OpenWrite(dest);
        var buf = new byte[81920];
        long done = 0;
        int lastPct = -1;
        while (true)
        {
            int n = net.Read(buf, 0, buf.Length);
            if (n <= 0)
                break;
            file.Write(buf, 0, n);
            done += n;
            if (total.HasValue && total.Value > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    Progress("Downloading fcon.exe... " + pct + "%", 25 + pct * 30 / 100);
                }
            }
        }
    }

    static bool LooksLikeExe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 256 * 1024)
                return false;
            var magic = new byte[2];
            using (var fs = File.OpenRead(path))
                if (fs.Read(magic, 0, 2) < 2)
                    return false;
            return magic[0] == 0x4D && magic[1] == 0x5A; // 'MZ'
        }
        catch
        {
            return false;
        }
    }

    static void SpawnSwap(string staged, string exePath)
    {
        try
        {
            string cmd = "/c timeout /t 2 /nobreak >nul & move /y \"" +
                staged + "\" \"" + exePath + "\" >nul";
            var psi = new ProcessStartInfo("cmd.exe", cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new Exception("Could not stage the update (" + ex.Message +
                "). Copy " + staged + " over " + exePath + " manually after closing fcon.");
        }
    }

    static void EnsureTargetOnPath(string target)
    {
        try
        {
            string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            bool has = userPath.Split(';').Any(p => p.Trim().TrimEnd('\\', '/')
                .Equals(target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (!has)
            {
                Render.Paint("==> Adding to PATH...", ConsoleColor.Cyan);
                string updated = string.IsNullOrEmpty(userPath) ? target : userPath + ";" + target;
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
            }
        }
        catch { }
        try
        {
            string proc = Environment.GetEnvironmentVariable("Path") ?? "";
            bool has = proc.Split(';').Any(p => p.Trim().TrimEnd('\\', '/')
                .Equals(target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (!has)
                Environment.SetEnvironmentVariable("Path", proc + ";" + target);
        }
        catch { }
    }

    static void Progress(string status, int pct)
    {
        try
        {
            Console.Write("\r  [fcon update] " + status.PadRight(46).Substring(0, 46) + " " +
                pct.ToString().PadLeft(3) + "%");
        }
        catch { }
    }

    static void ProgressDone()
    {
        try { Console.WriteLine(); } catch { }
    }
}
