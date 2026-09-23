// Small process / environment helpers shared by the commands.
using System.Diagnostics;

namespace FleetConnect;

sealed class Captured
{
    public int ExitCode;
    public string StdOut = "";
    public string StdErr = "";
}

static class Util
{
    // PATH lookup for an .exe, the C# equivalent of Get-Command -ErrorAction
    // SilentlyContinue. Returns the full path or null.
    public static string FindExe(string fileName)
    {
        if (fileName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        // PathSeparator (';' on Windows, ':' on Linux) so the lookup - and the
        // cross-platform logic tests - work on either OS.
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    // Runs a program and captures both streams. For short-lived helpers only;
    // interactive programs (ssh sessions, mstsc) are started inheriting the
    // console instead - redirecting their stdio would detach the session.
    public static Captured RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new Captured { ExitCode = proc.ExitCode, StdOut = stdout, StdErr = stderr };
    }

    // Starts an interactive program inheriting this console (ssh, mstsc,
    // editors). Returns its exit code; -1 when it could not start.
    public static int RunInherited(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
            using var proc = Process.Start(psi);
            if (proc == null)
                return -1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public static bool IsHelp(string text) =>
        text == "help" || text == "-h" || text == "/?" || text == "--help";

    public static bool IsHelpCI(string text) =>
        string.Equals(text, "help", StringComparison.OrdinalIgnoreCase) ||
        text == "-h" || text == "/?" ||
        string.Equals(text, "--help", StringComparison.OrdinalIgnoreCase);
}
