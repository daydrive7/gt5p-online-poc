using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Gt5pOnlineServer.Grim;

namespace Gt5pOnlineServer.Services;

// Port of grim_dns.py — UDP redirector: game hostnames -> host_ip, rest forwarded upstream.
public sealed class DnsBackgroundService(
    GrimRootOptions opts,
    ILogger<DnsBackgroundService> log,
    GrimFileLogger flog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = opts;
        if (!cfg.Dns.Enabled) { log.LogInformation("[dns] disabled"); return; }

        var redirect = new HashSet<string>(
            cfg.Dns.RedirectDomains.Select(d => d.Trim().Trim('.').ToLowerInvariant()));
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.Dns.Port));
        log.LogInformation("[dns] listening on UDP/{Port}; {N} game domains -> {Ip}",
            cfg.Dns.Port, redirect.Count, cfg.Server.HostIp);
        flog.Log($"[dns] listening on UDP/{cfg.Dns.Port}; {redirect.Count} game domains -> {cfg.Server.HostIp}");

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try { req = await udp.ReceiveAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "[dns] recv failed"); continue; }
            _ = Task.Run(() => Handle(req, udp, redirect, cfg), stoppingToken);
        }
    }

    private void Handle(UdpReceiveResult req, UdpClient udp,
        HashSet<string> redirect, GrimRootOptions cfg)
    {
        try
        {
            var data = req.Buffer;
            if (data.Length < 12) return;
            ushort qdcount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
            if (qdcount != 1) { Forward(data, req.RemoteEndPoint, udp, cfg); return; }

            var (qname, qend) = ReadName(data, 12);
            string ql = qname.Trim('.').ToLowerInvariant();
            if (qend + 4 > data.Length) return;
            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(qend, 2));

            // Log every lookup: a NAT/DME hostname the game needs would show
            // up here as forwarded-upstream instead of redirected.
            flog.Log($"[dns] query {qname} type={qtype} (client {req.RemoteEndPoint.Address})");

            if (redirect.Contains(ql) && qtype == 1)
            {
                flog.Log($"[dns] REDIRECT {qname} -> {cfg.Server.HostIp}  (client {req.RemoteEndPoint.Address})");
                var resp = BuildAResponse(data, qname, cfg.Server.HostIp);
                udp.Send(resp, resp.Length, req.RemoteEndPoint);
            }
            else if (redirect.Contains(ql) && qtype == 28)
            {
                var header = data[..2].Concat(new byte[] { 0x81, 0x80, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }).ToArray();
                var resp = header.Concat(data[12..(qend + 4)]).ToArray();
                udp.Send(resp, resp.Length, req.RemoteEndPoint);
            }
            else
            {
                Forward(data, req.RemoteEndPoint, udp, cfg);
            }
        }
        catch (Exception ex)
        {
            flog.Log($"[dns] error handling packet from {req.RemoteEndPoint}: {ex.Message}");
        }
    }

    private static void Forward(byte[] data, IPEndPoint client, UdpClient udp, GrimRootOptions cfg)
    {
        using var s = new UdpClient();
        s.Client.ReceiveTimeout = 4000;
        foreach (var up in cfg.Dns.UpstreamServers)
        {
            try
            {
                s.Send(data, data.Length, up, 53);
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var reply = s.Receive(ref ep);
                udp.Send(reply, reply.Length, client);
                return;
            }
            catch { /* try next upstream */ }
        }
        var fail = data[..2].Concat(new byte[] { 0x81, 0x82, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }).ToArray();
        udp.Send(fail, fail.Length, client);
    }

    internal static (string Name, int End) ReadName(byte[] data, int off)
    {
        var labels = new List<string>();
        int jumps = 0, end = off;
        while (true)
        {
            if (off >= data.Length) return (".", off);
            byte l = data[off];
            if (l == 0) { off++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (jumps == 0) end = off + 2;
                int ptr = ((l & 0x3F) << 8) | data[off + 1];
                off = ptr;
                if (++jumps > 32) return (".", end);
                continue;
            }
            labels.Add(Encoding.Latin1.GetString(data, off + 1, l));
            off += 1 + l;
        }
        return (string.Join(".", labels), jumps > 0 ? end : off);
    }

    private static byte[] EncodeName(string name)
    {
        var out_ = new List<byte>();
        foreach (var label in name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            out_.Add((byte)label.Length);
            out_.AddRange(Encoding.Latin1.GetBytes(label));
        }
        out_.Add(0);
        return out_.ToArray();
    }

    private static byte[] BuildAResponse(byte[] query, string qname, string ip)
    {
        var (_, qend) = ReadName(query, 12);
        var question = query[12..(qend + 4)];
        var header = query[..2].Concat(new byte[] { 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 }).ToArray();
        var answer = EncodeName(qname)
            .Concat(new byte[] { 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04 })
            .Concat(IPAddress.Parse(ip).GetAddressBytes()).ToArray();
        return header.Concat(question).Concat(answer).ToArray();
    }
}
