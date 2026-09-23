// Test double for CmdSetupUpdate (registry, Windows identities and
// dism/sc/netsh have no meaning off Windows, so the real file is not linked
// into the logic tests). Records dispatch so routing is still verified.
namespace FleetConnect;

static class CmdSetupUpdate
{
    public static readonly List<string> Calls = new List<string>();

    public static int InvokeSetup(List<string> args)
    {
        Calls.Add("setup:" + string.Join(",", args));
        return 11;
    }

    public static int InvokeUpdate(List<string> args)
    {
        Calls.Add("update:" + string.Join(",", args));
        return 12;
    }
}
