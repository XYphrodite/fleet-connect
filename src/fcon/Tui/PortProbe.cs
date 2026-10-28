using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace FleetConnect.Tui;

internal enum ProbeState
{
    Unknown,
    Checking,
    Open,
    Closed,
}

internal sealed class PortStatus
{
    public ProbeState State = ProbeState.Unknown;
    public int LatencyMs;
    public DateTimeOffset CheckedAt = DateTimeOffset.MinValue;
}

// Asynchronous TCP reachability cache for the inspector panel so port checks
// never block keyboard navigation or redraws.
internal sealed class PortProbe
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);
    const int TimeoutMs = 1200;

    readonly ConcurrentDictionary<string, PortStatus> cache =
        new ConcurrentDictionary<string, PortStatus>(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim gate = new SemaphoreSlim(6, 6);

    public bool Disabled { get; set; }

    public void Clear() => cache.Clear();

    public PortStatus Query(string host, int port, Action onUpdated)
    {
        if (Disabled || string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return new PortStatus();

        string key = host.Trim() + ":" + port;
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(key, out PortStatus current))
        {
            if (current.State == ProbeState.Checking || (now - current.CheckedAt) < CacheTtl)
                return current;
        }

        var checking = new PortStatus
        {
            State = ProbeState.Checking,
            CheckedAt = now,
        };
        cache[key] = checking;
        _ = ProbeEndpointAsync(key, host.Trim(), port, onUpdated);
        return checking;
    }

    async Task ProbeEndpointAsync(string key, string host, int port, Action onUpdated)
    {
        bool acquired = false;
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            cache[key] = new PortStatus
            {
                State = ProbeState.Open,
                LatencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds),
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
        catch
        {
            cache[key] = new PortStatus
            {
                State = ProbeState.Closed,
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            if (acquired)
                gate.Release();
            try { onUpdated?.Invoke(); } catch { }
        }
    }

    public static bool ResolveRdpEndpoint(PcModel pc, out string host, out int port)
    {
        host = pc?.Address ?? "";
        port = 3389;
        if (pc == null || string.IsNullOrWhiteSpace(host))
            return false;
        if (pc.Protocol == PcProtocol.Rdp && int.TryParse(pc.Port, out int custom) &&
            custom >= 1 && custom <= 65535)
            port = custom;
        return true;
    }

    public static bool ResolveSshEndpoint(PcModel pc, IReadOnlyDictionary<string, SshAliasInfo> sshConfig,
                                          out string host, out int port)
    {
        host = "";
        port = 22;
        if (pc == null)
            return false;

        if (pc.SshAlias != "" && sshConfig != null &&
            sshConfig.TryGetValue(pc.SshAlias, out SshAliasInfo info))
        {
            host = info.HostName != "" ? info.HostName : pc.Address;
            if (int.TryParse(info.Port, out int aliasPort) && aliasPort >= 1 && aliasPort <= 65535)
                port = aliasPort;
            return !string.IsNullOrWhiteSpace(host);
        }

        host = pc.Address;
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (pc.SshAlias == "" && pc.Port != "" &&
            (pc.Protocol == PcProtocol.Ssh || pc.Port != "3389") &&
            int.TryParse(pc.Port, out int custom) && custom >= 1 && custom <= 65535)
            port = custom;
        return true;
    }
}
