using System.Collections.Concurrent;
using System.Text;

namespace Gt5pOnlineServer.Services;

// Live traffic ring buffer backing the /admin Web UI.
// The per-request middleware in Program.cs pushes one entry per
// request/response pair (headers + truncated body previews).
// chain stays PS3 -> DNS redirect -> Grim, UI reads from here.
public sealed record TrafficEntry(
    long Id,
    DateTime Time,
    string Method,
    string Path,
    string ClientIp,
    string ReqHeaders,
    string ReqBodyPreview,
    int ReqBytes,
    int Status,
    string ResContentType,
    long ResBytes,
    double DurationMs,
    string? Note);

public sealed class TrafficStore
{
    private long _seq;
    private readonly ConcurrentQueue<TrafficEntry> _q = new();
    private const int Cap = 500;

    public TrafficEntry Add(
        string method, string path, string clientIp,
        string reqHeaders, byte[] reqBody,
        int status, string resContentType, long resBytes,
        double durationMs, string? note = null)
    {
        var id = Interlocked.Increment(ref _seq);
        var e = new TrafficEntry(id, DateTime.Now, method, path, clientIp,
            reqHeaders, Preview(reqBody), reqBody.Length,
            status, resContentType, resBytes, durationMs, note);
        _q.Enqueue(e);
        while (_q.Count > Cap) _q.TryDequeue(out _);
        return e;
    }

    public List<TrafficEntry> Latest(int n = 100)
    {
        n = Math.Clamp(n, 1, Cap);
        return _q.TakeLast(n).Reverse().ToList();
    }

    public void Clear()
    {
        _q.Clear();
    }

    internal static string Preview(byte[] body, int max = 2048)
    {
        if (body.Length == 0) return "";
        int n = Math.Min(body.Length, 512);
        int printable = 0;
        foreach (var b in body.AsSpan(0, n))
            if ((b >= 32 && b < 127) || b is 9 or 10 or 13) printable++;
        string s = printable / (double)Math.Max(1, n) > 0.7
            ? Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, max))
            : $"<{body.Length} bytes binary: {Convert.ToHexString(body, 0, Math.Min(body.Length, 64))}...>";
        return body.Length > max ? s + $"... [truncated {body.Length}B]" : s;
    }
}
