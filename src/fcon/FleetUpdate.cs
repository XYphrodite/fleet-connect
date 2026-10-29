using System.Reflection;
using SelfUpdateKit;

namespace FleetConnect;

// Shared SelfUpdateKit wiring for fleet-connect (mirrors DstFarmUpdate):
// fixed GitHub repository, framework/self-contained asset selection, mandatory
// .sha256 sidecar verification, atomic rename swap and --help health probe.
internal static class FleetUpdate
{
    public const string Repository = "XYphrodite/fleet-connect";
    public const string FullAssetName = "fcon.exe";
    public const string FrameworkAssetName = "fcon-framework.exe";
    public const string InstalledFileName = "fcon.exe";

    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            if (v == null)
                return new ReleaseVersion(0, 0, 0);
            return new ReleaseVersion(
                Math.Max(0, v.Major),
                Math.Max(0, v.Minor),
                Math.Max(0, v.Build));
        }
    }

    public static string RequireInstalledPath(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
            throw new InvalidOperationException("Self-update is only available for an installed fcon.exe.");

        string fileName = Path.GetFileName(processPath);
        if (!string.Equals(fileName, InstalledFileName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileName, FrameworkAssetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Self-update is only available for an installed fcon.exe (running under '" + fileName + "').");
        }
        return Path.GetFullPath(processPath);
    }

    public static void CleanupRetiredOnStartup(string processPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processPath) ||
                !Path.IsPathFullyQualified(processPath) ||
                !string.Equals(Path.GetFileName(processPath), InstalledFileName, StringComparison.OrdinalIgnoreCase))
                return;

            string full = Path.GetFullPath(processPath);
            new ExecutableReplacer().RemoveRetiredCopies(full);

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (string legacy in new[] { "fcon.new.exe", "fcon.cmd", "fleet-connect.ps1", "fcon.ps1" })
                {
                    try
                    {
                        string p = Path.Combine(dir, legacy);
                        if (File.Exists(p))
                            File.Delete(p);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static void ResolveFlavour(out bool useFramework, out bool allowFallback)
    {
        string fw = Environment.GetEnvironmentVariable("FLEET_CONNECT_FRAMEWORK");
        if (string.IsNullOrWhiteSpace(fw) ||
            fw.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            fw.Equals("detect", StringComparison.OrdinalIgnoreCase))
        {
            useFramework = HasDotNet10Runtime();
            allowFallback = true;
            return;
        }
        if (fw == "0" ||
            fw.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            fw.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            fw.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            useFramework = false;
            allowFallback = false;
            return;
        }
        useFramework = true;
        allowFallback = false;
    }

    public static ReleaseSourceOptions BuildOptions(bool useFramework, bool allowFallback)
    {
        var options = new ReleaseSourceOptions
        {
            Repository = Repository,
            ChecksumMode = ChecksumMode.SidecarAsset,
            UserAgent = "fleet-connect/" + CurrentVersion,
        };
        if (useFramework)
        {
            options.ExecutableAssetNames.Add(FrameworkAssetName);
            if (allowFallback)
                options.ExecutableAssetNames.Add(FullAssetName);
        }
        else
        {
            options.ExecutableAssetNames.Add(FullAssetName);
        }
        return options;
    }

    public static string ResolveRequestedTag(string explicitTag)
    {
        string candidate = !string.IsNullOrWhiteSpace(explicitTag)
            ? explicitTag.Trim()
            : Environment.GetEnvironmentVariable("FLEET_CONNECT_REF")?.Trim();

        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return null;

        return candidate;
    }

    public static async Task<SelfUpdateReport> RunAsync(
        string exePath,
        SelfUpdateRequest request,
        Action<SelfUpdateProgress> onProgress,
        CancellationToken cancellationToken)
    {
        ResolveFlavour(out bool useFramework, out bool allowFallback);
        var options = BuildOptions(useFramework, allowFallback);
        try
        {
            using var source = new GitHubReleaseSource(options);
            var service = new SelfUpdateService(exePath, CurrentVersion, source, options);
            return await service.UpdateAsync(request, cancellationToken, onProgress).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public static void MapProgress(SelfUpdateProgress report, out string label, out int percent)
    {
        switch (report.Phase)
        {
            case SelfUpdatePhase.Checking:
                label = "Checking GitHub releases...";
                percent = 10;
                return;
            case SelfUpdatePhase.ReleaseAvailable:
                label = report.Release != null
                    ? "Found release " + report.Release.Tag
                    : "Release available";
                percent = 20;
                return;
            case SelfUpdatePhase.Downloading:
                if (report.TotalBytes.HasValue && report.TotalBytes.Value > 0 && report.ReceivedBytes.HasValue)
                {
                    int dlPct = (int)Math.Clamp(report.ReceivedBytes.Value * 100L / report.TotalBytes.Value, 0L, 100L);
                    label = "Downloading " + (report.Release?.Tag ?? "update") + "... " + dlPct + "%";
                    percent = 20 + dlPct * 55 / 100;
                }
                else
                {
                    label = "Downloading " + (report.Release?.Tag ?? "update") + "...";
                    percent = 45;
                }
                return;
            case SelfUpdatePhase.Verifying:
                label = "Verifying SHA-256 & health probe...";
                percent = 82;
                return;
            case SelfUpdatePhase.Installing:
                label = "Swapping executable into place...";
                percent = 94;
                return;
            default:
                label = "Updating...";
                percent = 50;
                return;
        }
    }

    public static bool HasDotNet10Runtime()
    {
        try
        {
            var r = Util.RunCapture("dotnet", "--list-runtimes");
            if (r.ExitCode == 0 && (r.StdOut ?? "").Contains("Microsoft.NETCore.App 10."))
                return true;
        }
        catch { }

        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } pf
                ? Path.Combine(pf, "dotnet")
                : null,
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "dotnet"),
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            try
            {
                string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared))
                    continue;
                foreach (string dir in Directory.GetDirectories(shared))
                {
                    if (Path.GetFileName(dir).StartsWith("10.", StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
        }
        return false;
    }
}
