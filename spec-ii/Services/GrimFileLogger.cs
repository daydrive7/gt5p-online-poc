using System.Text;

namespace Gt5pOnlineServer.Services;

// Thread-safe mirror of grim_http.py logging: console + logs/grim_yyyyMMdd.log,
// with request (headers+body) and response lines for the iteration loop.
public sealed class GrimFileLogger
{
    private readonly string _dir;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _day = "";

    public GrimFileLogger(string logDir)
    {
        _dir = logDir;
        Directory.CreateDirectory(_dir);
    }

    public void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        lock (_gate)
        {
            Console.WriteLine(line);
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    public void LogRequest(string method, string path, IHeaderDictionary headers, byte[]? body)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in headers) sb.AppendLine($"    {k}: {v}");
        string bodyS = "";
        if (body is { Length: > 0 })
        {
            int n = Math.Min(body.Length, 512);
            int printable = body.Take(n).Count(b => (b >= 32 && b < 127) || b is 9 or 10 or 13);
            bodyS = printable / (double)Math.Max(1, n) > 0.7
                ? Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 4096))
                : $"<{body.Length} bytes binary: {Convert.ToHexString(body, 0, Math.Min(body.Length, 64))}...>";
        }
        Log($">>> {method} {path}\n{sb}    [body] {bodyS}");
    }

    public void LogResponse(int status, string ctype, long size, string extra = "")
        => Log($"<<< {status} {ctype} {size}B {extra}");

    private void EnsureWriter()
    {
        var day = DateTime.Now.ToString("yyyyMMdd");
        if (_writer is null || day != _day)
        {
            _writer?.Dispose();
            _day = day;
            _writer = new StreamWriter(Path.Combine(_dir, $"grim_{day}.log"), append: true, Encoding.UTF8);
        }
    }
}
