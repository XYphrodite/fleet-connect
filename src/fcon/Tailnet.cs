// Tailscale status: one record per tailnet machine, $null-able when tailsacle
// is absent or says nothing usable ("no status to show", never a failure).
using System.Text.Json;

namespace FleetConnect;

sealed class TailnetMachine
{
    public string Name = "";
    public string DnsName = "";
    public string Address = "";
    public bool Online;
    public string Os = "";
    public string HostName = "";
}

sealed class TailnetSnapshot
{
    public TailnetMachine Self;
    public List<TailnetMachine> Machines;
}

static class Tailnet
{
    // Set by Program from -NoStatus so AddOnlineStatus can honour it.
    public static bool NoStatus;

    // Test hook for the hermetic logic tests.
    internal static bool TestNoTailscale;

    public static string ResolveTailscale()
    {
        string fromPath = Util.FindExe("tailscale.exe");
        if (fromPath != null)
            return fromPath;
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string candidate = Path.Combine(root, "Tailscale", "tailscale.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static TailnetSnapshot GetSnapshot()
    {
        if (TestNoTailscale)
            return null;
        string exe = ResolveTailscale();
        if (exe == null)
            return null;
        Captured run;
        try
        {
            run = Util.RunCapture(exe, "status --json");
        }
        catch
        {
            return null;
        }
        if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.StdOut))
            return null;
        return ParseStatusJson(run.StdOut);
    }

    internal static TailnetSnapshot ParseStatusJson(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(jsonText);
        }
        catch
        {
            return null;
        }
        using (json)
        {
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            TailnetMachine selfRecord = null;
            var nodes = new List<JsonElement>();
            if (root.TryGetProperty("Self", out JsonElement self) &&
                self.ValueKind == JsonValueKind.Object)
            {
                nodes.Add(self);
                string selfV4 = ReadFirstIpv4(self);
                selfRecord = new TailnetMachine
                {
                    DnsName = (Prop(self, "DNSName") ?? "").TrimEnd('.'),
                    Address = selfV4 ?? "",
                    HostName = Prop(self, "HostName") ?? "",
                    Online = PropBool(self, "Online"),
                    Os = Prop(self, "OS") ?? "",
                };
                if (selfRecord.DnsName != "")
                {
                    int dot = selfRecord.DnsName.IndexOf('.');
                    selfRecord.Name = dot >= 0 ? selfRecord.DnsName.Substring(0, dot) : selfRecord.DnsName;
                }
                else
                {
                    selfRecord.Name = selfRecord.HostName;
                }
            }

            if (root.TryGetProperty("Peer", out JsonElement peers) &&
                peers.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in peers.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object)
                        nodes.Add(p.Value);
                }
            }

            List<TailnetMachine> machines = null;
            if (nodes.Count > 0)
            {
                var result = new List<TailnetMachine>();
                foreach (JsonElement node in nodes)
                {
                    string v4 = ReadFirstIpv4(node);
                    if (v4 == null)
                        continue;
                    // The name is read from DNSName and never built from HostName.
                    string dns = (Prop(node, "DNSName") ?? "").TrimEnd('.');
                    string label;
                    if (dns != "")
                    {
                        int dot = dns.IndexOf('.');
                        label = dot >= 0 ? dns.Substring(0, dot) : dns;
                    }
                    else
                    {
                        label = Prop(node, "HostName") ?? "";
                    }
                    if (label == "")
                        continue;
                    result.Add(new TailnetMachine
                    {
                        Name = label,
                        DnsName = dns,
                        Address = v4,
                        Online = PropBool(node, "Online"),
                        Os = Prop(node, "OS") ?? "",
                        HostName = Prop(node, "HostName") ?? "",
                    });
                }
                if (result.Count > 0)
                    machines = result;
            }

            if (selfRecord == null && machines == null)
                return null;
            return new TailnetSnapshot { Self = selfRecord, Machines = machines };
        }
    }

    static string Prop(JsonElement node, string name)
    {
        if (node.TryGetProperty(name, out JsonElement v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return null;
    }

    static bool PropBool(JsonElement node, string name)
    {
        if (node.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.True)
                return true;
            if (v.ValueKind == JsonValueKind.False)
                return false;
        }
        return false;
    }

    static string ReadFirstIpv4(JsonElement node)
    {
        if (node.TryGetProperty("TailscaleIPs", out JsonElement ips) &&
            ips.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement ip in ips.EnumerateArray())
            {
                string s = ip.ValueKind == JsonValueKind.String ? ip.GetString() : null;
                if (!string.IsNullOrEmpty(s) && s.IndexOf(':') < 0)
                    return s;
            }
        }
        return null;
    }

    public static List<TailnetMachine> GetTailnetMachines() => GetSnapshot()?.Machines;

    // This machine's own tailnet record, for the sync self-skip.
    public static TailnetMachine GetSelf() => GetSnapshot()?.Self;

    public static TailnetMachine FindMatch(PcModel pc, IReadOnlyList<TailnetMachine> machines)
    {
        if (pc == null || machines == null)
            return null;
        foreach (var m in machines)
        {
            if (m.Name == pc.Name || m.Address == pc.Address || m.DnsName == pc.Address)
                return m;
        }
        return null;
    }

    public static bool IsSelf(PcModel t, TailnetMachine self)
    {
        if (t == null || self == null || (self.DnsName == "" && self.Address == ""))
            return false;
        if (self.DnsName != "" &&
            (t.Address == self.DnsName || t.Address == self.DnsName + "."))
            return true;
        if (self.Address != "" && t.Address == self.Address)
            return true;
        if (self.HostName != "" &&
            (t.Name.Equals(self.HostName, StringComparison.OrdinalIgnoreCase) ||
             t.Address.StartsWith(self.HostName + ".", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    // MagicDNS either resolves on this machine or it does not: asked once for
    // the whole tailnet rather than paying one DNS timeout per machine.
    public static bool TestMagicDnsWorks(string sampleName)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
            return false;
        try
        {
            System.Net.Dns.GetHostAddresses(sampleName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void AddOnlineStatus(List<PcModel> pcs)
    {
        if (NoStatus || pcs.Count == 0)
            return;
        var machines = GetTailnetMachines();
        if (machines == null)
            return;
        foreach (var pc in pcs)
        {
            foreach (var m in machines)
            {
                if (m.Name == pc.Name || m.Address == pc.Address || m.DnsName == pc.Address)
                {
                    pc.Online = m.Online ? "up" : "down";
                    break;
                }
            }
        }
    }
}
