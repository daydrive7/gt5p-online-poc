namespace Gt5pOnlineServer.Grim;

// Mirrors config.json sections from the Python server so the same file can be shared.
public sealed class GrimRootOptions
{
    public ServerOptions Server { get; set; } = new();
    public DnsOptions Dns { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public GrimSection Grim { get; set; } = new();
    public MatchingOptions Matching { get; set; } = new();
    public RtimeOptions Rtime { get; set; } = new();
}

public sealed class ServerOptions
{
    public string Name { get; set; } = "GT5P Custom Server";
    public string HostIp { get; set; } = "127.0.0.1";
    public string ListenIp { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public int ExtraHttpPort { get; set; } = 8080;
    public string LogDir { get; set; } = "logs";
    public string ResourcesDir { get; set; } = "resources";
    public string? ArchiveContentsDir { get; set; }
    public bool WeatherEnabled { get; set; }
    public int WeatherRefreshMinutes { get; set; } = 60;
    public string WeatherUnit { get; set; } = "celsius";
}

public sealed class DnsOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 53;
    public string[] UpstreamServers { get; set; } = ["8.8.8.8", "1.1.1.1"];
    public string[] RedirectDomains { get; set; } =
    [
        "gt5prologue.ps3.online.jp.gran-turismo.com",
        "gt5prologue.ps3.online.us.gran-turismo.com",
        "gt5prologue.ps3.online.eu.gran-turismo.com",
        "gt5prologue.ps3.online.uk.gran-turismo.com",
        "gt5ptrial.ps3.online.jp.gran-turismo.com",
        "gt5p.ps3.online.jp.gran-turismo.com",
        "gt5p.ps3.online.us.gran-turismo.com",
        "gt5p.ps3.online.eu.gran-turismo.com",
        "www.gran-turismo.com",
    ];
}

public sealed class TlsOptions
{
    public bool Enabled { get; set; }
    public string CertFile { get; set; } = "certs/server.crt";
    public string KeyFile { get; set; } = "certs/server.key";
    public string CaFile { get; set; } = "certs/ca.crt";
}

public sealed class GrimSection
{
    public string PfsVersionsAccepted { get; set; } = "any";
    public Dictionary<string, object> KnownPfsVersions { get; set; } = new();
    public bool Maintenance { get; set; }
    public string MaintenanceMessage { get; set; } = "The server is currently undergoing maintenance.";
    public bool StrictMode { get; set; } = true;
    public int SessionLifetimeSec { get; set; } = 3600;
    public string ServerId { get; set; } = "gt5p";
    public string Region { get; set; } = "jp";
}

public sealed class MatchingOptions
{
    public int PollingTimeSec { get; set; } = 5;
    public int RemainingTimeSec { get; set; } = 120;
    public int WaitBeforeCreateSec { get; set; } = 2;
}

public sealed class RtimeOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 3659;
    public string ReplyMode { get; set; } = "echo";
}
