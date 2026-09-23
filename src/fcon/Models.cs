// Data model. Mirrors the PcModel class and the pcs.csv columns 1:1.
using System.Text.RegularExpressions;

namespace FleetConnect;

enum PcProtocol
{
    Rdp,
    Ssh,
}

sealed class PcModel
{
    public string Name = "";
    public string Address = "";
    public PcProtocol Protocol = PcProtocol.Rdp;
    public string User = "";
    public string SshAlias = "";
    public string Port = "";
    public string Note = "";
    // 'up', 'down' or '' - read from tailscale status, never stored in the CSV.
    public string Online = "";
}

static class Protocols
{
    public static PcProtocol Parse(string text, PcProtocol @default = PcProtocol.Rdp)
    {
        if (string.IsNullOrWhiteSpace(text))
            return @default;
        string t = text.Trim();
        if (Regex.IsMatch(t, @"^(?i)r(dp)?$"))
            return PcProtocol.Rdp;
        if (Regex.IsMatch(t, @"^(?i)s(sh)?$"))
            return PcProtocol.Ssh;
        throw new Exception("Unknown protocol '" + text + "'. Use rdp or ssh.");
    }

    public static string ToName(PcProtocol p) => p == PcProtocol.Ssh ? "ssh" : "rdp";

    // Positional-arg detection for `fcon add <name> <host> [rdp|ssh|user]`.
    // NOTE: the PS version tested '^(?i)r(dp)?|s(sh)?$', where the missing
    // end-anchor on the first branch made any user name starting with 'r'
    // (e.g. 'remote') look like a protocol and then fail the add. The anchored
    // form below is a deliberate fix: only a real protocol word counts.
    public static bool LooksLikeProtocol(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (Regex.IsMatch(text.Trim(), @"^(?i)r(dp)?$") ||
         Regex.IsMatch(text.Trim(), @"^(?i)s(sh)?$"));
}
