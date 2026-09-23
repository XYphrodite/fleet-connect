// Everyday commands: list, edit, path, help, add, and the connect flow.
namespace FleetConnect;

static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int NoChoice = 2;
}

static class CmdMisc
{
    public static int InvokeList()
    {
        var pcs = CsvStore.ReadPcList();
        Tailnet.AddOnlineStatus(pcs);
        if (pcs.Count == 0)
        {
            Render.Note("No machines yet. Run \"fcon import\" or \"fcon edit\".");
            Render.Note(CsvStore.GetListPath());
            return ExitCodes.NoChoice;
        }
        Console.WriteLine();
        foreach (string line in Render.FormatPcLines(pcs))
            Console.WriteLine("  " + line);
        Console.WriteLine();
        return ExitCodes.Ok;
    }

    public static int InvokeEdit()
    {
        string path = CsvStore.GetListPath();
        if (!File.Exists(path))
            path = CsvStore.NewEmptyList();
        string editor = Environment.GetEnvironmentVariable("FLEET_CONNECT_EDITOR");
        if (string.IsNullOrWhiteSpace(editor))
            editor = "notepad.exe";
        return Util.RunDetached(editor, "\"" + path + "\"") < 0
            ? ExitCodes.Error
            : ExitCodes.Ok;
    }

    public static int InvokePath()
    {
        Console.WriteLine(CsvStore.GetListPath());
        return ExitCodes.Ok;
    }

    const string HelpText = @"fleet-connect - pick a machine from a list and open RDP or SSH to it.

The machine list is a CSV; one row is one PC. Nothing here stores a password:
RDP is handed a user name and Windows asks for the rest, SSH goes out through the
local OpenSSH client and whatever ~/.ssh/config already says about that host.

    fcon                  pick from the list
    fcon gpu              connect to the machine named gpu, its own protocol
    fcon gpu ssh          the same machine over SSH instead
    fcon list             print the list and exit
    fcon add              add a new machine to the list
    fcon add gpu host     add with positional args (see help)
    fcon sync             push pcs.csv to remote machines
    fcon sync mks68 xeon  push to selected machines
    fcon import           merge the tailnet into the list
    fcon setup            enable RDP/SSH on this machine (requires admin)
    fcon update           update fcon from GitHub (progress bar + status)
    fcon edit             open the CSV in an editor
    fcon path             print where the CSV lives
    fcon help             this text

The list lives in %LOCALAPPDATA%\fleet-connect\pcs.csv unless $env:FLEET_CONNECT_CSV
names another file. Its columns are

    Name      short name, what you type after `fcon`
    Host      address or DNS name, put into the command as it stands
    Protocol  Rdp or Ssh - the default for this machine, still overridable
    User      login for RDP, and for SSH when no alias is set
    SshAlias  a Host entry from ~/.ssh/config; when set, SSH uses it and nothing else
    Port      only when it is not the protocol's usual one
    Note      free text, shown in the list

Exit codes: 0 done, 1 error, 2 nothing chosen or no such machine.";

    public static int InvokeHelp()
    {
        Console.WriteLine(HelpText);
        return ExitCodes.Ok;
    }

    public static int InvokeConnect(string wanted, string wantedVia)
    {
        var pcs = CsvStore.ReadPcList();
        if (pcs.Count == 0)
        {
            Render.Fail("There are no machines in the list yet.");
            Render.Note("Run \"fcon import\" to read them out of the tailnet, or \"fcon edit\" to type them in.");
            Render.Note(CsvStore.GetListPath());
            return ExitCodes.NoChoice;
        }

        PcModel pc = null;
        if (!string.IsNullOrEmpty(wanted))
        {
            // Exact name first, then prefix, then anything containing it.
            var hits = pcs.Where(p => p.Name == wanted).ToList();
            if (hits.Count == 0)
                hits = pcs.Where(p => p.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                hits = pcs.Where(p => p.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      p.Address.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0)
            {
                Render.Fail("No machine matches '" + wanted + "'.");
                InvokeList();
                return ExitCodes.NoChoice;
            }
            if (hits.Count == 1)
            {
                pc = hits[0];
            }
            else
            {
                int i = Picker.Show(hits.Count + " machines match '" + wanted + "'",
                    Render.FormatPcLines(hits), hits.Select(h => h.Name).ToList());
                if (i < 0)
                    return ExitCodes.NoChoice;
                pc = hits[i];
            }
        }
        else
        {
            Tailnet.AddOnlineStatus(pcs);
            int i = Picker.Show("Machines   (arrows move, letters jump, Enter connects, Esc quits)",
                Render.FormatPcLines(pcs), pcs.Select(p => p.Name).ToList());
            if (i < 0)
                return ExitCodes.NoChoice;
            pc = pcs[i];
        }

        PcProtocol protocol = pc.Protocol;
        if (!string.IsNullOrEmpty(wantedVia))
        {
            protocol = Protocols.Parse(wantedVia, pc.Protocol);
        }
        else if (string.IsNullOrEmpty(wanted))
        {
            // Only the menu asks. "fcon gpu" is the shortcut, and stopping it
            // to ask would undo the whole point of having one.
            int start = pc.Protocol == PcProtocol.Ssh ? 1 : 0;
            int i = Picker.Show(pc.Name + " - how?",
                new List<string> { "rdp   Remote Desktop", "ssh   Secure Shell" },
                new List<string> { "rdp", "ssh" }, start, selectOnKey: true);
            if (i < 0)
                return ExitCodes.NoChoice;
            protocol = i == 1 ? PcProtocol.Ssh : PcProtocol.Rdp;
        }

        return Connect.ConnectPc(pc, protocol);
    }

    public static int InvokeAdd(List<string> addArgs)
    {
        if (addArgs.Count == 1 && Util.IsHelpCI(addArgs[0]))
        {
            Console.WriteLine();
            Render.Paint("  fcon add - add a new machine to the list", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine("    fcon add                                   interactive prompts");
            Console.WriteLine("    fcon add <name> <host> [rdp|ssh]            positional");
            Console.WriteLine("    fcon add <name> <host> --user <user> --port <port> --note <text> --alias <sshAlias>");
            Console.WriteLine();
            Console.WriteLine("  Flags (order does not matter):");
            Console.WriteLine("    --user, -u       login for RDP and SSH (when no alias)");
            Console.WriteLine("    --alias          Host entry from ~/.ssh/config (when set, SSH uses only it)");
            Console.WriteLine("    --port           non-default port");
            Console.WriteLine("    --note           free text shown in the list");
            Console.WriteLine("    --protocol, -p   rdp or ssh (also as 3rd positional arg)");
            Console.WriteLine();
            Console.WriteLine("  Examples:");
            Console.WriteLine("    fcon add srv1 10.0.0.5 rdp --user admin --note \"office\"");
            Console.WriteLine("    fcon add dev dev-box.ts.net ssh --alias dev --port 2222");
            Console.WriteLine();
            return ExitCodes.Ok;
        }

        string addName = null, addHost = null, addProtoText = null;
        string addUser = null, addAlias = null, addPort = null, addNote = null;
        var positional = new List<string>();

        for (int i = 0; i < addArgs.Count; i++)
        {
            string a = addArgs[i];
            string low = a.ToLowerInvariant();
            if (low == "--user" || low == "-u" || low == "user" || low == "u")
            {
                if (i + 1 >= addArgs.Count) { Render.Fail("Missing value for " + a); return ExitCodes.Error; }
                addUser = addArgs[++i];
                continue;
            }
            if (low == "--alias" || low == "-alias" || low == "alias" ||
                low == "--ssh-alias" || low == "ssh-alias" || low == "--sshalias")
            {
                if (i + 1 >= addArgs.Count) { Render.Fail("Missing value for " + a); return ExitCodes.Error; }
                addAlias = addArgs[++i];
                continue;
            }
            if (low == "--port" || low == "-port" || low == "port")
            {
                if (i + 1 >= addArgs.Count) { Render.Fail("Missing value for " + a); return ExitCodes.Error; }
                addPort = addArgs[++i];
                continue;
            }
            if (low == "--note" || low == "-note" || low == "note")
            {
                if (i + 1 >= addArgs.Count) { Render.Fail("Missing value for " + a); return ExitCodes.Error; }
                addNote = addArgs[++i];
                continue;
            }
            if (low == "--protocol" || low == "-protocol" || low == "protocol" ||
                low == "--proto" || low == "proto" || low == "--p" || low == "-p" || low == "p")
            {
                if (i + 1 >= addArgs.Count) { Render.Fail("Missing value for " + a); return ExitCodes.Error; }
                addProtoText = addArgs[++i];
                continue;
            }
            positional.Add(a);
        }

        // Positional mapping: name, host, [protocol|user] - protocol detected by value.
        if (positional.Count >= 1 && addName == null)
            addName = positional[0];
        if (positional.Count >= 2 && addHost == null)
            addHost = positional[1];
        if (positional.Count >= 3)
        {
            string third = positional[2];
            if (addProtoText == null && Protocols.LooksLikeProtocol(third))
                addProtoText = third;
            else if (addUser == null)
                addUser = third;
        }
        if (positional.Count >= 4 && addUser == null)
            addUser = positional[3];
        if (positional.Count >= 5 && addNote == null)
            addNote = string.Join(" ", positional.GetRange(4, positional.Count - 4));

        bool interactive = Picker.Interactive();

        if (addName == null)
        {
            if (!interactive) { Render.Fail("Name is required. Usage: fcon add <name> <host> [rdp|ssh]"); return ExitCodes.Error; }
            Console.Write("  Name (short, e.g. gpu): ");
            addName = (Console.ReadLine() ?? "").Trim();
            if (addName == "") { Render.Note("Cancelled."); return ExitCodes.NoChoice; }
        }
        if (addHost == null)
        {
            if (!interactive) { Render.Fail("Host is required. Usage: fcon add <name> <host>"); return ExitCodes.Error; }
            Console.Write("  Host (address or DNS): ");
            addHost = (Console.ReadLine() ?? "").Trim();
            if (addHost == "") { Render.Note("Cancelled."); return ExitCodes.NoChoice; }
        }
        if (addProtoText == null && interactive && positional.Count == 0 && addArgs.Count == 0)
        {
            Console.Write("  Protocol [rdp/ssh, default rdp]: ");
            string ans = (Console.ReadLine() ?? "").Trim();
            if (ans != "")
                addProtoText = ans;
        }

        PcProtocol proto;
        try
        {
            proto = Protocols.Parse(addProtoText);
        }
        catch (Exception ex)
        {
            Render.Fail(ex.Message);
            return ExitCodes.Error;
        }

        if (addUser == null && interactive && addArgs.Count == 0)
        {
            Console.Write("  User (empty = none): ");
            addUser = (Console.ReadLine() ?? "").Trim();
        }
        if (addAlias == null && interactive && addArgs.Count == 0)
        {
            Console.Write("  SshAlias (empty = none, uses Host from ~/.ssh/config): ");
            addAlias = (Console.ReadLine() ?? "").Trim();
        }
        if (addPort == null && interactive && addArgs.Count == 0)
        {
            Console.Write("  Port (empty = default): ");
            addPort = (Console.ReadLine() ?? "").Trim();
        }
        if (addNote == null && interactive && addArgs.Count == 0)
        {
            Console.Write("  Note (empty = none): ");
            addNote = (Console.ReadLine() ?? "").Trim();
        }

        var existing = CsvStore.ReadPcList();
        if (existing.Any(p => p.Name == addName))
        {
            Render.Fail("A machine named '" + addName + "' already exists.");
            return ExitCodes.Error;
        }
        var dupHost = existing.FirstOrDefault(p => p.Address == addHost);
        if (dupHost != null)
            Render.Note("Note: another machine '" + dupHost.Name + "' already uses host '" + addHost + "'.");

        existing.Add(new PcModel
        {
            Name = addName,
            Address = addHost,
            Protocol = proto,
            User = addUser ?? "",
            SshAlias = addAlias ?? "",
            Port = addPort ?? "",
            Note = addNote ?? "",
        });
        CsvStore.WritePcList(existing);

        Console.WriteLine();
        Render.Paint("  Added " + addName + "  " + addHost + "  " + Protocols.ToName(proto), ConsoleColor.Green);
        if (addUser != null && addUser != "")
            Render.Note("  user: " + addUser);
        if (addAlias != null && addAlias != "")
            Render.Note("  alias: " + addAlias);
        if (addPort != null && addPort != "")
            Render.Note("  port: " + addPort);
        Render.Note("  list: " + CsvStore.GetListPath());
        Console.WriteLine();
        return ExitCodes.Ok;
    }
}
