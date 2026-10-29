// Setup (enable RDP/SSH on this machine, admin required) and update
// (self-update from a GitHub release asset).
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SelfUpdateKit;

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

    // Powered by SelfUpdateKit: version comparison, .sha256 sidecar verification,
    // atomic rename swap (.old-<timestamp>) and post-install --help health probe.
    public static int InvokeUpdate(List<string> updateArgs)
    {
        if (updateArgs.Count == 1 && Util.IsHelpCI(updateArgs[0]))
        {
            Console.WriteLine();
            Render.Paint("  fcon update - self-update fcon from GitHub Releases (SelfUpdateKit)", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine("    fcon update                update to the latest release if newer");
            Console.WriteLine("    fcon update --check        only check if a newer release is published");
            Console.WriteLine("    fcon update --tag v1.1.1   install a specific release tag");
            Console.WriteLine("    fcon update --help         this text");
            Console.WriteLine();
            Console.WriteLine("  Verifies SHA-256 sidecar checksum, runs a --help probe, and atomically");
            Console.WriteLine("  replaces the running executable (with automatic rollback on failure).");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        bool checkOnly = false;
        string explicitTag = null;
        var unknown = new List<string>();
        for (int i = 0; i < updateArgs.Count; i++)
        {
            string a = updateArgs[i];
            if (a.Equals("--check", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-check", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("check", StringComparison.OrdinalIgnoreCase))
            {
                checkOnly = true;
            }
            else if (a.Equals("--tag", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("-tag", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= updateArgs.Count)
                {
                    Render.Fail("Missing value for " + a + ". Example: fcon update --tag v1.1.1");
                    return ExitCodes.Error;
                }
                explicitTag = updateArgs[++i];
            }
            else
            {
                unknown.Add(a);
            }
        }
        if (unknown.Count > 0)
        {
            Render.Fail("Unknown option(s): " + string.Join(" ", unknown) + ". Use fcon update --help");
            return ExitCodes.Error;
        }

        try
        {
            string exePath = FleetUpdate.RequireInstalledPath(Environment.ProcessPath);
            string requestedTag = FleetUpdate.ResolveRequestedTag(explicitTag);
            var request = new SelfUpdateRequest(CheckOnly: checkOnly, Tag: requestedTag);

            Progress("Checking for updates...", 5);
            var report = FleetUpdate.RunAsync(
                exePath,
                request,
                p =>
                {
                    FleetUpdate.MapProgress(p, out string label, out int pct);
                    Progress(label, pct);
                },
                CancellationToken.None).GetAwaiter().GetResult();

            ProgressDone();
            Console.WriteLine();

            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                Render.Paint("  Up to date: v" + report.Installed + " (" + report.Tag + ")", ConsoleColor.Green);
                Console.WriteLine();
                return ExitCodes.Ok;
            }

            if (report.Status == SelfUpdateStatus.UpdateAvailable)
            {
                Render.Paint("  Update available: v" + report.Installed + " -> " + report.Tag, ConsoleColor.Green);
                if (report.ReleaseDescriptor != null)
                    Render.Note("  " + report.ReleaseDescriptor.ExecutableUrl);
                Console.WriteLine();
                return ExitCodes.Ok;
            }

            string installDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(installDir))
                EnsureTargetOnPath(installDir);

            Render.Paint("  Updated fcon: v" + report.Installed + " -> " + report.Tag, ConsoleColor.Green);
            Render.Note("  verified SHA-256 checksum & --help health probe");
            Render.Note("  binary: " + exePath);
            Render.Note("  machine list: " + CsvStore.GetListPath());
            Console.WriteLine();
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

    public static int InvokeUpdateFromTui(Action<string, int> onProgress, Action<string> onLog,
                                          out string summary)
    {
        summary = "";
        try
        {
            string exePath = FleetUpdate.RequireInstalledPath(Environment.ProcessPath);
            string requestedTag = FleetUpdate.ResolveRequestedTag(null);
            var request = new SelfUpdateRequest(CheckOnly: false, Tag: requestedTag);

            onProgress?.Invoke("Checking GitHub releases...", 8);
            var report = FleetUpdate.RunAsync(
                exePath,
                request,
                p =>
                {
                    FleetUpdate.MapProgress(p, out string label, out int pct);
                    onProgress?.Invoke(label, pct);
                },
                CancellationToken.None).GetAwaiter().GetResult();

            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                onProgress?.Invoke("Up to date", 100);
                summary = "Already up to date (v" + report.Installed + ")";
                onLog?.Invoke(summary);
                return ExitCodes.Ok;
            }

            string installDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(installDir))
                EnsureTargetOnPath(installDir, quiet: true);

            onProgress?.Invoke("Updated to " + report.Tag, 100);
            summary = "Updated v" + report.Installed + " -> " + report.Tag + " — restart fcon to use it";
            onLog?.Invoke(summary + " (" + exePath + ")");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            summary = "Update failed: " + ex.Message;
            onLog?.Invoke(summary);
            return ExitCodes.Error;
        }
    }

    static void EnsureTargetOnPath(string target, bool quiet = false)
    {
        try
        {
            string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            bool has = userPath.Split(';').Any(p => p.Trim().TrimEnd('\\', '/')
                .Equals(target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (!has)
            {
                if (!quiet)
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
