using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Gt5pOnlineServer.Grim;

namespace Gt5pOnlineServer.Services;

// Minimal rtime/NAT introducer on UDP 3659 (the port both consoles punch
// 4-byte keepalives at ~1/sec: `d013e4f0`). The full Sony handshake is still
// being recovered from native-nat-transport.asm, so this service starts as
// an always-on registry + responder with selectable reply modes:
//
//   off   - log only, never reply (baseline capture)
//   echo  - send the payload back (liveness probe; harmless if ignored)
//
// Peers are keyed by observed endpoint; /admin/api/rtime exposes them so the
// operator can correlate UDP endpoints with HTTP sessions (same LAN IPs).
// Set Rtime:ReplyMode / Rtime:Port / Rtime:Enabled via config or env
// (Rtime__ReplyMode, Rtime__Port, Rtime__Enabled).
public sealed record RtimePeer(string Endpoint, string Ip, int Port, long Packets, DateTime LastSeen, string LastHex);

public sealed class RtimeService(IServiceProvider services) : BackgroundService
{
    private readonly ConcurrentDictionary<string, (long Count, DateTime Seen, string Hex)> peers = new();

    public List<RtimePeer> Snapshot() => peers
        .Select(kv =>
        {
            var parts = kv.Key.Split(':');
            return new RtimePeer(kv.Key, string.Join(":", parts[..^1]), int.TryParse(parts[^1], out var p) ? p : 0,
                kv.Value.Count, kv.Value.Seen, kv.Value.Hex);
        })
        .OrderByDescending(p => p.LastSeen)
        .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        GrimRootOptions opts;
        GrimFileLogger flog;
        using (var scope = services.CreateScope())
        {
            opts = scope.ServiceProvider.GetRequiredService<GrimRootOptions>();
            flog = scope.ServiceProvider.GetRequiredService<GrimFileLogger>();
        }
        if (!opts.Rtime.Enabled)
        {
            flog.Log("[rtime] disabled");
            return;
        }
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, opts.Rtime.Port));
        }
        catch (Exception ex)
        {
            flog.Log($"[rtime] could not bind UDP/{opts.Rtime.Port}: {ex.Message}");
            return;
        }
        flog.Log($"[rtime] listening on UDP/{opts.Rtime.Port}; reply={opts.Rtime.ReplyMode}");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult req;
                try { req = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { flog.Log($"[rtime] recv failed: {ex.Message}"); continue; }
                _ = Task.Run(() => Handle(udp, req, opts, flog), stoppingToken);
            }
        }
        finally
        {
            udp.Close();
        }
    }

    private void Handle(UdpClient udp, UdpReceiveResult req, GrimRootOptions opts, GrimFileLogger flog)
    {
        try
        {
            var ep = req.RemoteEndPoint.ToString();
            var hex = Convert.ToHexString(req.Buffer, 0, Math.Min(req.Buffer.Length, 64)).ToLowerInvariant();
            peers.AddOrUpdate(ep,
                _ => (1, DateTime.UtcNow, hex),
                (_, old) => (old.Count + 1, DateTime.UtcNow, hex));
            // Log only new peers and every 60th packet to avoid flooding.
            var count = peers[ep].Count;
            if (count == 1 || count % 60 == 0)
                flog.Log($"[rtime] {req.Buffer.Length}B from {ep} hex={hex} (#{count})");
            if (opts.Rtime.ReplyMode.Equals("echo", StringComparison.OrdinalIgnoreCase) && req.Buffer.Length > 0)
                udp.Send(req.Buffer, req.Buffer.Length, req.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            flog.Log($"[rtime] error handling packet from {req.RemoteEndPoint}: {ex.Message}");
        }
    }
}
