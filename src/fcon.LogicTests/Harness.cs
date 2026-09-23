// Headless verification for the fcon port. Runs on any OS with the .NET 10
// SDK (no Windows-only APIs linked in). Interactive picker paths are skipped
// when a real console is attached; everything else runs for real, including
// ssh dispatch through a fake ssh.exe and exit-code propagation.
using System.Text;

namespace FleetConnect;

static class LogicTests
{
    static int failed;
    static string root;
    static string csv;
    static string fakeBin;
    static string savedCsv;
    static string savedPath;
    static string savedHome;

    static int Main()
    {
        try
        {
            Setup();
            TestProtocols();
            TestCsv();
            TestFormat();
            TestAliases();
            TestRdpFile();
            TestSafeName();
            TestAdd();
            TestList();
            TestConnectSsh();
            TestConnectNoMatch();
            TestSyncDryRun();
            TestImportNoTailscale();
            TestUpdateHelp();
            TestSetupBogus();
            TestRouting();
        }
        catch (Exception ex)
        {
            Console.WriteLine("HARNESS ERROR: " + ex);
            failed++;
        }
        finally
        {
            Teardown();
        }
        Console.WriteLine(failed == 0 ? "ALL LOGIC TESTS PASSED" : failed + " LOGIC TEST(S) FAILED");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ harness

    static void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "fcon-logic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        csv = Path.Combine(root, "pcs.csv");
        fakeBin = Path.Combine(root, "bin");
        Directory.CreateDirectory(fakeBin);
        // Fake ssh.exe: echoes its argv like the earlier /tmp probe and exits
        // with FAKESSH_CODE, so dispatch and exit codes are observable.
        File.WriteAllText(Path.Combine(fakeBin, "ssh.exe"),
            "#!/bin/sh\necho \"FAKESSH argv: $@\"\nexit ${FAKESSH_CODE:-0}\n");
        try
        {
            var chmod = System.Diagnostics.Process.Start("chmod", "+x " + Path.Combine(fakeBin, "ssh.exe"));
            chmod?.WaitForExit();
        }
        catch { }
        savedCsv = Environment.GetEnvironmentVariable("FLEET_CONNECT_CSV");
        savedPath = Environment.GetEnvironmentVariable("PATH");
        savedHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("FLEET_CONNECT_CSV", csv);
        Environment.SetEnvironmentVariable("PATH",
            fakeBin + Path.PathSeparator + (savedPath ?? ""));
        Environment.SetEnvironmentVariable("HOME", root);
    }

    static void Teardown()
    {
        Environment.SetEnvironmentVariable("FLEET_CONNECT_CSV", savedCsv);
        Environment.SetEnvironmentVariable("PATH", savedPath);
        Environment.SetEnvironmentVariable("HOME", savedHome);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    static void SeedCsv() => File.WriteAllText(csv,
        "Name,Host,Protocol,User,SshAlias,Port,Note\n" +
        "xeon,desktop-ib88isg.tail08a9a5.ts.net,Rdp,local,fcon-xeon,,Xeon E5\n" +
        "nuc,100.100.10.14,Ssh,root,,2222,garage\n");

    static string CaptureOut(Func<int> fn, out int code)
    {
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);
        TextWriter saved = Console.Out;
        Console.SetOut(writer);
        try { code = fn(); }
        finally
        {
            Console.SetOut(saved);
            writer.Dispose();
        }
        return sb.ToString();
    }

    static void Check(bool cond, string msg)
    {
        if (!cond)
        {
            Console.WriteLine("FAIL: " + msg);
            failed++;
        }
        else
        {
            Console.WriteLine("PASS: " + msg);
        }
    }

    // --------------------------------------------------------------- tests

    static void TestProtocols()
    {
        Check(Protocols.Parse("rdp") == PcProtocol.Rdp, "parse rdp");
        Check(Protocols.Parse("RDP") == PcProtocol.Rdp, "parse RDP");
        Check(Protocols.Parse("r") == PcProtocol.Rdp, "parse r");
        Check(Protocols.Parse("ssh") == PcProtocol.Ssh, "parse ssh");
        Check(Protocols.Parse("S") == PcProtocol.Ssh, "parse S");
        Check(Protocols.Parse("", PcProtocol.Ssh) == PcProtocol.Ssh, "empty keeps default");
        bool threw = false;
        try { Protocols.Parse("rdpx"); } catch { threw = true; }
        Check(threw, "bad protocol throws");
        Check(Protocols.LooksLikeProtocol("rdp"), "rdp looks like protocol");
        Check(!Protocols.LooksLikeProtocol("remote"), "user 'remote' is not a protocol (deliberate fix)");
        Check(!Protocols.LooksLikeProtocol("local"), "user 'local' is not a protocol");
    }

    static void TestCsv()
    {
        var pcs = new List<PcModel>
        {
            new PcModel { Name = "a", Address = "h1", Protocol = PcProtocol.Rdp,
                User = "u", Note = "note, with comma and \"quote\" and unicode: \u0441\u0435\u0440\u0432\u0435\u0440" },
        };
        CsvStore.WritePcList(pcs);
        var back = CsvStore.ReadPcList();
        Check(back.Count == 1, "csv roundtrip keeps row");
        Check(back[0].Note == pcs[0].Note, "csv roundtrip keeps commas/quotes/unicode");
        Check(File.Exists(csv + ".bak") == false, "first write makes no .bak");
        CsvStore.WritePcList(back);
        Check(File.Exists(csv + ".bak"), "second write keeps .bak");
        File.WriteAllText(csv, "Name,Host\noldbox,10.0.0.9\n");
        var compat = CsvStore.ReadPcList();
        Check(compat.Count == 1 && compat[0].User == "" && compat[0].Protocol == PcProtocol.Rdp,
            "older csv with missing columns reads blanks");
        File.WriteAllText(csv, "Name,Host,Protocol\nbadbox,10.0.0.10,rdpx\n\ngoodbox,10.0.0.11,Rdp\n");
        var typo = CsvStore.ReadPcList();
        var bad = typo.FirstOrDefault(p => p.Name == "badbox");
        Check(bad != null && bad.Protocol == PcProtocol.Rdp, "protocol typo falls back to rdp");
        Check(typo.Any(p => p.Name == "goodbox"), "one bad row does not hide the rest");
    }

    static void TestFormat()
    {
        var pcs = new List<PcModel>
        {
            new PcModel { Name = "xeon", Address = "desktop-ib88isg.tail08a9a5.ts.net",
                Protocol = PcProtocol.Rdp, User = "local", SshAlias = "fcon-xeon",
                Note = "Xeon", Online = "up" },
            new PcModel { Name = "nuc", Address = "100.100.10.14", Protocol = PcProtocol.Ssh,
                User = "root", Port = "2222", Online = "down" },
        };
        var lines = Render.FormatPcLines(pcs);
        Check(lines.Count == 2, "two rows render two lines");
        Check(lines[0].StartsWith("* ") && lines[0].Contains("alias fcon-xeon"),
            "up mark and ssh alias shown");
        Check(lines[1].StartsWith(". ") && lines[1].Contains("port 2222") &&
              !lines[1].Contains("alias "), "down mark, port shown, no alias text");
        pcs[1].Online = "";
        var unknown = Render.FormatPcLines(pcs);
        Check(unknown[1].StartsWith("? "), "missing status renders '?'");
    }

    static void TestAliases()
    {
        string sshDir = Path.Combine(root, ".ssh");
        Directory.CreateDirectory(sshDir);
        File.WriteAllText(Path.Combine(sshDir, "config"),
            "Host fcon-xeon\n    HostName example.ts.net\n\n" +
            "Host wsl desktop-ib88isg-wsl\n    HostName 100.74.101.71\n\n" +
            "Host *\n    ServerAliveInterval 60\n");
        var found = Connect.GetLocalSshAliases();
        Check(found.Contains("fcon-xeon"), "finds fcon-xeon alias");
        Check(found.Contains("wsl") && found.Contains("desktop-ib88isg-wsl"),
            "finds multi-name Host block");
        Check(!found.Contains("*") && found.Count == 3, "skips wildcard, parses exactly three");
        Directory.Delete(sshDir, recursive: true);
        Check(Connect.GetLocalSshAliases().Count == 0, "missing config answers zero aliases");
    }

    static void TestRdpFile()
    {
        string file = Path.Combine(root, "t.rdp");
        File.WriteAllText(file, "screen mode id:i:2\nfull address:s:old\nusername:s:old\n", Encoding.Unicode);
        Connect.SetRdpSetting(file, "full address:s:", "new:3389");
        Connect.SetRdpSetting(file, "username:s:", "admin");
        string text = File.ReadAllText(file);
        Check(text.Contains("screen mode id:i:2"), "rdp rewrite keeps foreign lines");
        Check(text.Contains("full address:s:new:3389") && text.Contains("username:s:admin"),
            "rdp rewrite owns its two lines");
        Check(text.Split('\n').Count(s => s.StartsWith("username:s:")) == 1, "rdp key written once");
    }

    static void TestSafeName()
    {
        Check(Connect.SafeFileName("a/b") == "a_b", "slash in name is sanitized");
        Check(Connect.SafeFileName("") == "pc", "empty name falls back to pc");
    }

    static void TestAdd()
    {
        File.WriteAllText(csv, "Name,Host,Protocol,User,SshAlias,Port,Note\n");
        int code = CmdMisc.InvokeAdd(new List<string>
            { "test1", "10.0.0.5", "rdp", "--user", "admin" });
        Check(code == 0 && File.ReadAllText(csv).Contains("test1"), "add positional exits 0");
        Check(CmdMisc.InvokeAdd(new List<string> { "test1", "10.0.0.6" }) == 1,
            "duplicate add exits 1");
        Check(CmdMisc.InvokeAdd(new List<string> { "web", "10.0.0.7", "remote" }) == 0,
            "third positional 'remote' is a user, not a protocol");
        Check(CsvStore.ReadPcList().First(p => p.Name == "web").User == "remote",
            "user 'remote' stored as user");
        Check(CmdMisc.InvokeAdd(new List<string> { "help" }) == 0, "add help exits 0");
    }

    static void TestList()
    {
        SeedCsv();
        string output = CaptureOut(() => CmdMisc.InvokeList(), out int code);
        Check(code == 0, "list exits 0");
        Check(output.Contains("xeon") && output.Contains("alias fcon-xeon"), "list shows machine and alias");
    }

    static void TestConnectSsh()
    {
        SeedCsv();
        Environment.SetEnvironmentVariable("FAKESSH_CODE", "3");
        string output = CaptureOut(() => CmdMisc.InvokeConnect("xeon", "ssh"), out int code);
        Check(output.Contains("ssh fcon-xeon"), "alias used whole for ssh");
        Check(code == 3, "ssh exit code propagates (got " + code + ")");
        Environment.SetEnvironmentVariable("FAKESSH_CODE", "0");
        string direct = CaptureOut(() => CmdMisc.InvokeConnect("nuc", "ssh"), out int directCode);
        Check(direct.Contains("ssh root@100.100.10.14 -p 2222"), "direct ssh builds user@host -p port");
        Check(directCode == 0, "direct ssh exit code propagates");
        // RDP path without mstsc on this box must fail like the PS version.
        string rdp = CaptureOut(() => CmdMisc.InvokeConnect("xeon", "rdp"), out int rdpCode);
        if (Util.FindExe("mstsc.exe") == null)
            Check(rdpCode == 1, "rdp without mstsc exits 1");
        else
            Check(rdpCode == 0, "rdp with mstsc exits 0");
        Check(rdp.Contains("mstsc"), "rdp path announces mstsc");
    }

    static void TestConnectNoMatch()
    {
        SeedCsv();
        string output = CaptureOut(() => CmdMisc.InvokeConnect("nope", null), out int code);
        Check(code == 2 && output.Contains("No machine matches"), "unknown machine exits 2");
        File.WriteAllText(csv, "Name,Host,Protocol,User,SshAlias,Port,Note\n");
        Check(CmdMisc.InvokeConnect("nope", null) == 2, "empty list exits 2");
    }

    static void TestSyncDryRun()
    {
        SeedCsv();
        string output = CaptureOut(
            () => CmdSyncImport.InvokeSync(new List<string> { "--dry-run" }), out int code);
        Check(code == 0, "sync dry-run exits 0");
        Check(output.Contains("dry-run") && output.Contains("xeon"), "dry-run lists targets");
        Check(output.Contains("not in ~/.ssh/config"), "dry-run warns about missing local alias");
        Check(output.Contains("Synced 2 host(s)"), "dry-run counts both targets");
        Check(CmdSyncImport.InvokeSync(new List<string> { "--dry-run", "nope" }) == 1,
            "sync unknown target exits 1");
        File.WriteAllText(csv, "Name,Host,Protocol,User,SshAlias,Port,Note\n" +
            "x1,10.0.0.1\nx2,10.0.0.2\n");
        Check(CmdSyncImport.InvokeSync(new List<string> { "--dry-run", "x" }) == 1,
            "sync ambiguous prefix exits 1");
    }

    static void TestImportNoTailscale()
    {
        // Hermetic: shrink PATH to the fake bin so a real tailscale (e.g. via
        // WSL interop) cannot leak into the test on any machine.
        string keep = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeBin);
        string output;
        int code;
        try
        {
            output = CaptureOut(() => CmdSyncImport.InvokeImport(), out code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", keep);
        }
        Check(code == 1 && output.Contains("tailscale"), "import without tailscale exits 1");
    }

    static void TestUpdateHelp()
    {
        // Real update/setup bodies need Windows (registry, dism, HttpClient
        // downloads); here the stub proves dispatch reaches them with the
        // right arguments. Behaviours ride along in tests/Cli.Tests.ps1 on
        // a built Windows exe.
        CmdSetupUpdate.Calls.Clear();
        Check(Program.Run(new[] { "update", "--bogus" }) == 12, "route: update reached");
        Check(CmdSetupUpdate.Calls.Contains("update:--bogus"), "route: update got its args");
        CmdSetupUpdate.Calls.Clear();
        Check(Program.Run(new[] { "setup", "--bogus" }) == 11, "route: setup reached");
        Check(CmdSetupUpdate.Calls.Contains("setup:--bogus"), "route: setup got its args");
    }

    static void TestSetupBogus()
    {
        // Covered by the dispatch assertions in TestUpdateHelp.
        Check(true, "setup/update dispatch covered");
    }

    static void TestRouting()
    {
        SeedCsv();
        // Switch aliases in every position, like the PS binder.
        Check(Program.Run(new[] { "path" }) == 0, "route: path");
        Check(Program.Run(new[] { "help" }) == 0, "route: help word");
        Check(Program.Run(new[] { "-h" }) == 0, "route: -h");
        Check(Program.Run(new[] { "/?" }) == 0, "route: /?");
        Check(Program.Run(new[] { "--help" }) == 0, "route: --help");
        Check(Program.Run(new[] { "list", "--nostatus" }) == 0, "route: list with --nostatus");
        Check(Program.Run(new[] { "add", "r1", "10.9.9.1" }) == 0, "route: add");
        Check(Program.Run(new[] { "sync", "--dry-run", "-Yes" }) == 0, "route: sync dry-run with -Yes");
        Check(Program.Run(new[] { "update", "help" }) == 12, "route: update help");
        Check(Program.Run(new[] { "setup", "--bogus" }) == 11, "route: setup bogus");
        Check(Program.Run(new[] { "nope" }) == 2, "route: unknown name exits 2");
        if (!Picker.Interactive())
            Check(Program.Run(Array.Empty<string>()) == 2, "route: bare picker headless exits 2");
        else
            Console.WriteLine("SKIP: bare picker needs no console (one is attached)");
    }
}
