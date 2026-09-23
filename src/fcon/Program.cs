// Entry point. Same routing as the PS version: `fcon`, `fcon <name>`,
// `fcon <name> <via>`, subcommands, -NoStatus/-Yes/-Help switches,
// exit codes 0 done / 1 error / 2 nothing chosen or no such machine.
using System.Text.RegularExpressions;

namespace FleetConnect;

static class Program
{
    public static bool Yes;
    public static bool NoStatusSwitch;

    static int Main(string[] args) => Run(args);

    // Split out of Main so the logic tests can drive routing headlessly.
    internal static int Run(string[] args)
    {
        try
        {
            bool noStatus = false, yes = false, help = false;
            var positional = new List<string>();
            foreach (string a in args)
            {
                string low = a.ToLowerInvariant();
                if (low == "-nostatus" || low == "--nostatus")
                    noStatus = true;
                else if (low == "-yes" || low == "--yes")
                    yes = true;
                else if (low == "-help" || low == "--help" || low == "-h" || low == "/?")
                    help = true;
                else
                    positional.Add(a);
            }
            // `fcon help` / `fcon -h` / `fcon /?` as the command word.
            if (positional.Count > 0 && Util.IsHelpCI(positional[0]) && positional.Count == 1)
                help = true;

            Yes = yes;
            NoStatusSwitch = noStatus;
            Tailnet.NoStatus = noStatus;

            if (help && positional.Count <= 1)
                return CmdMisc.InvokeHelp();

            string name = positional.Count >= 1 ? positional[0] : null;
            string via = positional.Count >= 2 ? positional[1] : null;
            var extra = positional.Count > 2
                ? positional.GetRange(2, positional.Count - 2)
                : new List<string>();

            if (name != null)
            {
                if (Regex.IsMatch(name, "^(?i)list$"))
                    return CmdMisc.InvokeList();
                if (Regex.IsMatch(name, "^(?i)add$"))
                {
                    var addArgs = new List<string>();
                    if (via != null)
                        addArgs.Add(via);
                    addArgs.AddRange(extra);
                    return CmdMisc.InvokeAdd(addArgs);
                }
                if (Regex.IsMatch(name, "^(?i)sync$"))
                {
                    var syncArgs = new List<string>();
                    if (via != null)
                        syncArgs.Add(via);
                    syncArgs.AddRange(extra);
                    return CmdSyncImport.InvokeSync(syncArgs);
                }
                if (Regex.IsMatch(name, "^(?i)import$"))
                    return CmdSyncImport.InvokeImport();
                if (Regex.IsMatch(name, "^(?i)setup$"))
                {
                    var setupArgs = new List<string>();
                    if (via != null)
                        setupArgs.Add(via);
                    setupArgs.AddRange(extra);
                    return CmdSetupUpdate.InvokeSetup(setupArgs);
                }
                if (Regex.IsMatch(name, "^(?i)update$"))
                {
                    var updArgs = new List<string>();
                    if (via != null)
                        updArgs.Add(via);
                    updArgs.AddRange(extra);
                    return CmdSetupUpdate.InvokeUpdate(updArgs);
                }
                if (Regex.IsMatch(name, "^(?i)edit$"))
                    return CmdMisc.InvokeEdit();
                if (Regex.IsMatch(name, "^(?i)path$"))
                    return CmdMisc.InvokePath();
            }
            return CmdMisc.InvokeConnect(name, via);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Render.Fail(ex.Message);
            return ExitCodes.Error;
        }
    }
}
