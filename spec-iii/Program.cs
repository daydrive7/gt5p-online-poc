using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gt5pOnlineServer.Data;
using Gt5pOnlineServer.Grim;
using Gt5pOnlineServer.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: false);

var grimSection = builder.Configuration.GetSection("Grim");
var serverSection = builder.Configuration.GetSection("Server");
var dnsSection = builder.Configuration.GetSection("Dns");
var tlsSection = builder.Configuration.GetSection("Tls");
var matchingSection = builder.Configuration.GetSection("Matching");

var opts = new GrimRootOptions();
builder.Configuration.GetSection("Server").Bind(opts.Server);
builder.Configuration.GetSection("Dns").Bind(opts.Dns);
builder.Configuration.GetSection("Tls").Bind(opts.Tls);
builder.Configuration.GetSection("Grim").Bind(opts.Grim);
builder.Configuration.GetSection("Matching").Bind(opts.Matching);
builder.Configuration.GetSection("Rtime").Bind(opts.Rtime);
ApplyLegacySnakeCase(builder.Configuration, opts);

string contentRoot = builder.Environment.ContentRootPath;
string logDir = Path.GetFullPath(opts.Server.LogDir, contentRoot);
Directory.CreateDirectory(logDir);

string resDir = Path.GetFullPath(opts.Server.ResourcesDir, contentRoot);
Directory.CreateDirectory(resDir);

var flog = new GrimFileLogger(logDir);
builder.Services.AddSingleton(flog);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<GrimStore>();
builder.Services.AddSingleton<TrafficStore>();
builder.Services.AddSingleton(new WeatherFeed(resDir, logDir, opts.Server.WeatherEnabled, opts.Server.WeatherRefreshMinutes, opts.Server.WeatherUnit, flog));
builder.Services.AddHostedService(s => s.GetRequiredService<WeatherFeed>());
builder.Services.AddSingleton(new ReplayStore(Path.Combine(resDir, "replays-private")));
builder.Services.AddSingleton(new NativeProfileStore(logDir));

// Postgres (Npgsql) with InMemory fallback for dev/tests without a server
string? connStr = builder.Configuration.GetConnectionString("GrimDb");
bool forceMemory = Environment.GetEnvironmentVariable("USE_INMEMORY_DB") == "1" || string.IsNullOrWhiteSpace(connStr);
if (!forceMemory)
{
    builder.Services.AddDbContext<GrimDbContext>(o => o.UseNpgsql(connStr));
    flog.Log("[db] using Postgres (Npgsql)");
}
else
{
    builder.Services.AddDbContext<GrimDbContext>(o => o.UseInMemoryDatabase("grim"));
    flog.Log("[db] using InMemory database (set ConnectionStrings:GrimDb for Postgres)");
}

builder.Services.AddHostedService<DnsBackgroundService>();
builder.Services.AddSingleton<RtimeService>();
builder.Services.AddHostedService(s => s.GetRequiredService<RtimeService>());

// Kestrel ports mirror run.py: HTTP 80 + 8080, HTTPS 443
string certPath = Path.IsPathRooted(opts.Tls.CertFile) ? opts.Tls.CertFile : Path.Combine(contentRoot, opts.Tls.CertFile);
string keyPath = Path.IsPathRooted(opts.Tls.KeyFile) ? opts.Tls.KeyFile : Path.Combine(contentRoot, opts.Tls.KeyFile);
bool haveCert = opts.Tls.Enabled && File.Exists(certPath) && File.Exists(keyPath);

builder.WebHost.ConfigureKestrel(k =>
{
    var listenAddress = IPAddress.Parse(opts.Server.ListenIp);
    k.Listen(listenAddress, opts.Server.HttpPort, listener => listener.UseGracefulReadEof(opts.Server.GracefulSocketCloseSeconds));
    if (opts.Server.ExtraHttpPort != 0 && opts.Server.ExtraHttpPort != opts.Server.HttpPort)
        try { k.Listen(listenAddress, opts.Server.ExtraHttpPort, listener => listener.UseGracefulReadEof(opts.Server.GracefulSocketCloseSeconds)); } catch { }
    if (haveCert)
    {
        try
        {
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
            k.Listen(listenAddress, opts.Server.HttpsPort, lo => lo.UseHttps(cert));
        }
        catch (Exception ex) { Console.WriteLine($"[https] could not start ({ex.Message}) - https disabled"); }
    }
    else if (opts.Tls.Enabled)
    {
        Console.WriteLine("[https] configured certificate or key is missing");
    }
});

var Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    [".xml"] = "application/xml; charset=utf-8",
    [".jpg"] = "image/jpeg",
    [".jpeg"] = "image/jpeg",
    [".png"] = "image/png",
    [".img"] = "application/octet-stream",
    [".dat"] = "application/octet-stream",
    [".dls"] = "application/octet-stream",
    [".pam"] = "application/octet-stream",
    [".swf"] = "application/x-shockwave-flash",
};

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if ((ctx.Request.Path.Value ?? "").StartsWith("/admin", StringComparison.Ordinal))
    {
        await next();
        return;
    }
    var store = ctx.RequestServices.GetRequiredService<TrafficStore>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    ctx.Request.EnableBuffering();
    byte[] reqBody;
    using (var ms = new MemoryStream())
    {
        await ctx.Request.Body.CopyToAsync(ms);
        reqBody = ms.ToArray();
        ctx.Request.Body.Position = 0;
    }
    var rawTarget = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget ?? ctx.Request.Path.Value ?? "/";
    var headers = string.Join("\n", ctx.Request.Headers.Select(h => $"    {h.Key}: {h.Value}"));
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    var origBody = ctx.Response.Body;
    using var responseCounter = new CountingWriteStream(origBody);
    ctx.Response.Body = responseCounter;
    string? note = null;
    try { await next(); }
    catch (Exception ex) { note = "!! " + ex.GetType().Name + ": " + ex.Message; throw; }
    finally
    {
        sw.Stop();
        ctx.Response.Body = origBody;
        flog.LogResponse(ctx.Response.StatusCode, ctx.Response.ContentType ?? "",
            responseCounter.BytesWritten, note ?? "");
        store.Add(ctx.Request.Method, rawTarget, clientIp, headers, reqBody,
            ctx.Response.StatusCode, ctx.Response.ContentType ?? "",
            responseCounter.BytesWritten, sw.Elapsed.TotalMilliseconds, note);
    }
});

// /ap/game/join/ and /ap/game/join hit the same handler; // collapses to /.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value;
    if (!string.IsNullOrEmpty(p))
    {
        var n = Regex.Replace(p, @"/{2,}", "/");
        if (n.Length > 1 && n.EndsWith('/')) n = n.TrimEnd('/');
        if (n != p) ctx.Request.Path = new PathString(n);
    }
    await next();
});

// Serve exact captured resource paths before legacy aliases. This also permits
// per-region News/TV/event/weather replacements without changing route code.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && !path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".." || x.Contains('\\') || x.Contains(':')))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var candidate = Path.Combine(resDir, Path.Combine(segments.Length == 0 ? new[] { "__no_resource__" } : segments));
        if (File.Exists(candidate))
        {
            await LogReq(ctx.Request);
            await SendResFile(candidate).ExecuteAsync(ctx);
            return;
        }
        // TV.DAT uses these cache filenames. Mount only known binary assets;
        // XML stays in resources, where encoding declarations are normalized.
        var archive = opts.Server.ArchiveContentsDir;
        var asset = Regex.Match(path, @"^/data2?/(tv|museum)/(?:common/|contents/|thumbnail/)?(tv_[0-9]+(?:_[0-9]+)?\.(?:pam|strb|dls)|museum_[0-9]+\.img)$");
        if (asset.Success && asset.Groups[1].Value == "tv")
        {
            var name = asset.Groups[2].Value;
            var directory = Path.Combine(resDir, "data2", "tv");
            var local = Path.Combine(directory, name);
            if (!File.Exists(local) && Directory.Exists(directory) && Regex.IsMatch(name, @"^tv_[0-9]+\.(?:dls|strb)$"))
            {
                var matches = Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(name) + "_*" + Path.GetExtension(name));
                if (matches.Length == 1) local = matches[0];
            }
            if (File.Exists(local))
            {
                await LogReq(ctx.Request);
                await SendResFile(local).ExecuteAsync(ctx);
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(archive) && asset.Success &&
            asset.Groups[2].Value.StartsWith(asset.Groups[1].Value + "_", StringComparison.Ordinal))
        {
            var media = Path.GetFullPath(Path.Combine(archive, asset.Groups[2].Value), contentRoot);
            if (!File.Exists(media) && Directory.Exists(archive) &&
                Regex.IsMatch(asset.Groups[2].Value, @"^tv_[0-9]+\.(?:pam|strb|dls)$"))
            {
                // ponytail: scan the small archive directory for the cache hash;
                // replace with a startup index if the archive grows substantially.
                var matches = Directory.GetFiles(archive,
                    Path.GetFileNameWithoutExtension(media) + "_*" + Path.GetExtension(media));
                if (matches.Length == 1) media = Path.GetFullPath(matches[0]);
            }
            if (File.Exists(media))
            {
                await LogReq(ctx.Request);
                await Results.File(media, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(ctx);
                return;
            }
        }
    }
    await next();
});
app.UseRouting();

// Ensure DB schema exists (EnsureCreated keeps dev simple; use migrations in prod).
try
{
    using var sc = app.Services.CreateScope();
    var db = sc.ServiceProvider.GetRequiredService<GrimDbContext>();
    await db.Database.EnsureCreatedAsync();
    var users = await db.Users.CountAsync();
    var recs = await db.Rankings.CountAsync();
    flog.Log($"[http] state loaded: {users} users, {recs} ranking records (Postgres={(haveCert ? "n/a" : "")}{(forceMemory ? "inmemory" : "postgres")})");
}
catch (Exception ex)
{
    flog.Log($"[db] WARNING: could not init database ({ex.Message}) - continuing, requests may fail until Postgres is up");
}

// Banner (mirrors run.py).
Console.WriteLine(new string('=', 66));
Console.WriteLine("   GT5P CUSTOM SERVER (C#) - Gran Turismo 5 Prologue protocol workbench");
Console.WriteLine(new string('=', 66));
Console.WriteLine($"   Host IP        : {opts.Server.HostIp}");
Console.WriteLine($"   Grim HTTPS     : tcp/{opts.Server.HttpsPort}" + (haveCert ? "" : " (disabled, no cert)"));
Console.WriteLine($"   Grim HTTP      : tcp/{opts.Server.HttpPort} + tcp/{opts.Server.ExtraHttpPort}");
if (opts.Dns.Enabled)
    Console.WriteLine($"   DNS redirect   : udp/{opts.Dns.Port}");
Console.WriteLine($"   rtime diagnostic: udp/{opts.Rtime.Port} (reply={opts.Rtime.ReplyMode}; native NAT not implemented)");
Console.WriteLine($"   Resources      : {resDir}");
Console.WriteLine($"   Logs           : {logDir}");
Console.WriteLine(new string('=', 66));

async Task<byte[]> ReadBodyAsync(HttpRequest req)
{
    req.EnableBuffering();
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    req.Body.Position = 0;
    return ms.ToArray();
}

string? TokenFrom(HttpRequest req)
{
    var cookie = req.Headers.Cookie.ToString();
    foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
        if (part.StartsWith("X-gt-token=", StringComparison.Ordinal))
            return part["X-gt-token=".Length..];
    return null;
}

IResult Xml(string xml, int status = 200)
{
    return Results.Content(xml, "application/xml; charset=utf-8", statusCode: status);
}

string? CanonicalLocale(string? value)
{
    var locale = value?.Trim().ToLowerInvariant();
    return locale switch
    {
        "uk" => "gb",
        "gb" or "de" or "es" or "fr" or "it" or "nl" or "pt" or "jp" or "us" or "kr" or "tw" => locale,
        _ => null,
    };
}

void DisableCache(HttpRequest req)
{
    req.HttpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    req.HttpContext.Response.Headers["Pragma"] = "no-cache";
    req.HttpContext.Response.Headers["Expires"] = "0";
}

IResult XmlWithCookie(HttpContext ctx, string xml, string cookie, int status = 200)
{
    ctx.Response.Headers["Set-Cookie"] = cookie;
    return Results.Content(xml, "application/xml; charset=utf-8", statusCode: status);
}

string SessionCookie(SessionEntity session) =>
    $"X-gt-token={session.Token}; Path=/; Expires={session.ExpiresAt.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture)}";

IResult SendResFile(string path, int status = 200)
{
    if (!File.Exists(path))
    {
        var x = GrimXml.Decline("resource not found");
        return Results.Content(x, "application/xml; charset=utf-8", statusCode: 404);
    }
    var relative = Path.GetRelativePath(Path.GetFullPath(resDir), Path.GetFullPath(path));
    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
        return Results.BadRequest();
    if (relative.StartsWith("replays-private" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Xml(GrimXml.Decline("resource not found"), 404);
    var ext = Path.GetExtension(path).ToLowerInvariant();
    var ctype = Mime.GetValueOrDefault(ext, "application/octet-stream");
    if (ext == ".xml" && opts.Server.FreshXmlResponses)
        return new FreshXmlResult(path, ctype);
    return Results.File(Path.GetFullPath(path), ctype, enableRangeProcessing: true);
}

async Task LogReq(HttpRequest req)
{
    var body = req.Method == "POST" ? await ReadBodyAsync(req) : null;
    req.HttpContext.Items["body"] = body;
    var headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()) as IDictionary<string, string>;
    var hdrs = new HeaderDictionary();
    foreach (var (k, v) in headers) hdrs[k] = v;
    flog.LogRequest(req.Method, req.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget ?? req.Path.Value ?? "/",
        hdrs, body);
}

byte[] BodyOf(HttpContext c) => (c.Items["body"] as byte[]) ?? Array.Empty<byte>();

bool MaintenanceBlock(HttpRequest req)
{
    var p = req.Path.Value ?? "/";
    if (opts.Grim.Maintenance && !p.StartsWith("/init", StringComparison.Ordinal))
    {
        flog.Log($"    [maintenance mode] refusing {p}");
        return true;
    }
    return false;
}

// bootstrap
app.MapGet("/init/regionlist.xml", async (HttpRequest req) =>
{
    await LogReq(req);
    if (MaintenanceBlock(req)) return Xml(GrimXml.Maintenance(opts.Grim.MaintenanceMessage));
    return SendResFile(Path.Combine(resDir, "regionlist.xml"));
});
app.MapGet("/init/{region}/serverlist.xml", async (HttpRequest req, string region) =>
{
    await LogReq(req);
    if (MaintenanceBlock(req)) return Xml(GrimXml.Maintenance(opts.Grim.MaintenanceMessage));
    flog.Log($"    serverlist requested for region '{region}'");
    var custom = Path.Combine(resDir, $"serverlist_{region}.xml");
    return SendResFile(File.Exists(custom) ? custom : Path.Combine(resDir, "serverlist.xml"));
});
app.MapGet("/data2/init/{region}/serverlist2_{locale}.xml", async (HttpRequest req, string region, string locale) =>
{
    await LogReq(req);
    if (!Regex.IsMatch(region, @"^(?:[a-z]{2}|_default)$")) return Xml(GrimXml.Decline("unknown region"), 404);
    var canonical = locale == "1" ? "gb" : CanonicalLocale(locale);
    if (canonical is null) return Xml(GrimXml.Decline("unknown locale"), 404);
    var directory = Path.Combine(resDir, "data2", "init", region);
    var localized = Path.Combine(directory, $"serverlist2_{canonical}.xml");
    return SendResFile(File.Exists(localized) ? localized : Path.Combine(directory, "serverlist2_gb.xml"));
});

// login
async Task<IResult> Login(HttpRequest req, string pfs, GrimStore store)
{
    await LogReq(req);
    var body = BodyOf(req.HttpContext);
    flog.Log($"    login: PFS {pfs} body={body.Length}B");
    NpTicket ticket;
    try
    {
        ticket = NpTicket.FromBuffer(body);
        flog.Log($"    ticket: {ticket}");
    }
    catch (Exception ex)
    {
        flog.Log($"    !! ticket parse failed ({ex.Message}); rejecting");
        return Xml(GrimXml.Decline("bad ticket"), 400);
    }
    var user = await store.GetOrCreateUserAsync(ticket.OnlineId, ticket.Region);
    var sess = await store.OpenSessionAsync(user, opts.Grim.SessionLifetimeSec);
    var resp = GrimXml.TicketResult(user.Nickname, user.OnlineId, user.UserNumber,
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    var cookie = SessionCookie(sess);
    flog.Log($"    login OK: {user.OnlineId} #{user.UserNumber}");
    DisableCache(req);
    return XmlWithCookie(req.HttpContext, resp, cookie);
}
app.MapPost("/ap/ticket/login/{pfs}", (HttpRequest req, string pfs, GrimStore s) => Login(req, pfs, s));
app.MapPost("/ap/ticket/login", (HttpRequest req, GrimStore s) => Login(req, "native", s));

// session keep-alive
async Task<IResult> Extend(HttpRequest req, GrimStore store)
{
    await LogReq(req);
    var tok = TokenFrom(req);
    var session = tok is null ? null : await store.ExtendSessionAsync(tok, opts.Grim.SessionLifetimeSec);
    if (session is not null)
    {
        DisableCache(req);
        return XmlWithCookie(req.HttpContext, GrimXml.Envelope("1"), SessionCookie(session));
    }
    return Xml(GrimXml.Decline("no session"), 401);
}
app.MapPost("/ap/misc/extend", (HttpRequest r, GrimStore s) => Extend(r, s));
app.MapPost("/ap/ticket/logout", async (HttpRequest req, GrimStore store) =>
{
    await LogReq(req);
    if (TokenFrom(req) is { } token) await store.CloseSessionAsync(token);
    req.HttpContext.Response.Headers["Set-Cookie"] = "X-gt-token=; Path=/; Max-Age=0";
    return Xml(GrimXml.Envelope("1"));
});
app.MapPost("/ap/misc/sendband", async (HttpRequest req, GrimStore store, NativeProfileStore profiles) =>
{
    await LogReq(req);
    var token = TokenFrom(req);
    var session = token is null ? null : await store.GetSessionAsync(token);
    if (session is null) return Xml(GrimXml.Decline("login required"), 401);
    var (command, parameters) = GrimXml.ParseEnvelope(BodyOf(req.HttpContext));
    if (command != "band.set" || !long.TryParse(parameters.GetValueOrDefault("send"), out var send) ||
        !long.TryParse(parameters.GetValueOrDefault("receive"), out var receive) || send < 0 || receive < 0)
        return Xml(GrimXml.Decline("invalid bandwidth report"), 400);
    profiles.Update(session.Value.User.OnlineId, send: send, receive: receive);
    return Xml(GrimXml.Envelope("1"));
});

// client logging
app.MapMethods("/log/{rest}", ["GET", "POST"], async (HttpRequest req, string rest) =>
{
    await LogReq(req);
    var body = BodyOf(req.HttpContext);
    var (cmd, ps) = body.Length > 0 ? GrimXml.ParseEnvelope(body) : ("raw", new());
    flog.Log($"    [putLog/{rest}] {cmd} {{{string.Join(",", ps.Select(kv => kv.Key + "=" + kv.Value))}}} raw={Encoding.UTF8.GetString(body.Take(512).ToArray())}");
    return Xml(GrimXml.Envelope("1"));
});

// patch
app.MapGet("/patchinfo.xml", async (HttpRequest req) => { await LogReq(req); return SendResFile(Path.Combine(resDir, "patchinfo.xml")); });
app.MapGet("/patch/patchinfo.xml", async (HttpRequest req) => { await LogReq(req); return SendResFile(Path.Combine(resDir, "patchinfo.xml")); });

// news (single dispatch like python's 3 routes)
app.MapGet("/news/{sid}/{region}/{file}.xml", async (HttpRequest req, string sid, string region, string file) =>
{
    await LogReq(req);
    if (file == "root") return SendResFile(Path.Combine(resDir, "news", "root.xml"));
    var m = Regex.Match(file, @"^l(\d+)_(\d+)$");
    if (m.Success)
    {
        flog.Log($"    news list for categories {m.Groups[1].Value}_{m.Groups[2].Value}");
        return SendResFile(Path.Combine(resDir, "news", $"l{m.Groups[1].Value}_{m.Groups[2].Value}.xml"));
    }
    return SendResFile(Path.Combine(resDir, "news", $"{file}.xml"));
});
app.MapGet("/news/common/{a}/{b}.jpg", async (HttpRequest req, string a, string b) =>
{
    await LogReq(req);
    return SendResFile(Path.Combine(resDir, "news", "common", $"{a}_{b}.jpg"));
});

// events
app.MapGet("/event/{server}/event_list.xml", async (HttpRequest req, string server) =>
{
    await LogReq(req);
    var regional = Path.Combine(resDir, "event", server, "event_list.xml");
    return SendResFile(File.Exists(regional) ? regional : Path.Combine(resDir, "event", "event_list.xml"));
});
app.MapGet("/event/{server}/event_{id}.xml", async (HttpRequest req, string server, string id) =>
{
    await LogReq(req);
    var regional = Path.Combine(resDir, "event", server, $"event_{id}.xml");
    var defaultPath = Path.Combine(resDir, "event", "1", $"event_{id}.xml");
    var legacy = Path.Combine(resDir, "event", $"event_{id}.xml");
    return SendResFile(File.Exists(regional) ? regional : File.Exists(defaultPath) ? defaultPath : legacy);
});
app.MapGet("/event/{server}/setting.xml", async (HttpRequest req) =>
{
    await LogReq(req); return SendResFile(Path.Combine(resDir, "event", "setting.xml"));
});

// Native getUrlBase2 treats /dynamic/pam as a URI string, not a Grim envelope.
app.MapGet("/dynamic/pam", async (HttpRequest req) =>
{
    await LogReq(req);
    DisableCache(req);
    if (MaintenanceBlock(req))
        return Results.Text("Service unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
    var downloadBase = new UriBuilder(Uri.UriSchemeHttp, opts.Server.HostIp, opts.Server.HttpPort)
        .Uri.GetLeftPart(UriPartial.Authority);
    return Results.Text(downloadBase, "text/plain");
});

// museum / tv / greeting / used cars / weather / user
app.MapGet("/data2/{kind}/{srv}/{region}/{fname}", async (HttpRequest req, string kind, string fname) =>
{
    await LogReq(req);
    var specTv = Regex.Match(fname, @"^tv2?_([a-zA-Z]{2}|1)_(root|l_\d+|\d+)\.xml$");
    if (kind.Equals("tv", StringComparison.OrdinalIgnoreCase) && specTv.Success)
    {
        var locale = specTv.Groups[1].Value == "1" ? "gb" : specTv.Groups[1].Value.ToLowerInvariant();
        var suffix = specTv.Groups[2].Value;
        if (suffix == "root") return SendResFile(Path.Combine(resDir, "tv", "tv_root.xml"));
        if (locale is not ("gb" or "de" or "es" or "fr" or "it" or "nl" or "pt")) locale = "gb";
        var catalog = Path.Combine(resDir, "data2", "tv", "eu", locale, $"tv2_{locale}_{suffix}.xml");
        if (File.Exists(catalog)) return SendResFile(catalog);
        var original = Path.Combine(resDir, "tv", "speciii", $"tv_{locale}_{suffix}.xml");
        return SendResFile(original);
    }
    var direct = Path.Combine(resDir, kind, fname);
    if (File.Exists(direct)) return SendResFile(direct);
    // English fallback from installed EU Spec III metadata. Locale-specific exact
    // paths above take precedence; unknown IDs still return 404.
    var tv = Regex.Match(fname, @"^tv2?_[a-zA-Z]{2}_(root|l_\d+|\d+)\.xml$");
    if (kind.Equals("tv", StringComparison.OrdinalIgnoreCase) && tv.Success)
        return SendResFile(Path.Combine(resDir, "tv", $"tv_{tv.Groups[1].Value}.xml"));
    return SendResFile(direct);
});
app.MapGet("/data/{kind}/{*rest}", async (HttpRequest req, string kind, string rest) =>
{
    await LogReq(req); return SendResFile(Path.Combine(resDir, kind, Path.GetFileName(rest)));
});
app.MapGet("/greeting/{srv}/{region}/greeting_list.xml", async (HttpRequest req) =>
{
    await LogReq(req); return SendResFile(Path.Combine(resDir, "greeting_list.xml"));
});
app.MapGet("/used_car/{srv}/used_car_list.xml", async (HttpRequest req) =>
{
    await LogReq(req); return SendResFile(Path.Combine(resDir, "used_car_list.xml"));
});
app.MapGet("/weather", async (HttpRequest req, WeatherFeed feed) =>
{
    await LogReq(req);
    DisableCache(req);
    return Xml(feed.RootXml());
});
app.MapGet("/weather.xml", async (HttpRequest req, WeatherFeed feed) =>
{
    await LogReq(req);
    DisableCache(req);
    return Xml(feed.RootXml());
});
app.MapGet("/weather/{*rest}", async (HttpRequest req, string rest, WeatherFeed feed) =>
{
    await LogReq(req);
    DisableCache(req);
    if (rest == "root.xml") return Xml(feed.RootXml());
    var match = Regex.Match(rest, @"^(\d+)\.xml$");
    if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id) || id is < 1 or > 21)
        return Xml(GrimXml.Decline("unknown weather region"), 404);
    var requestedUnit = req.Query["unit"].ToString();
    var unit = string.IsNullOrWhiteSpace(requestedUnit) ? feed.DefaultUnit : WeatherFeed.ParseUnit(requestedUnit);
    var item = feed.ItemXml(id, unit);
    return item is null ? Xml(GrimXml.Decline("weather not available yet"), 503) : Xml(item);
});
app.MapGet("/admin/api/weather", (HttpRequest req, WeatherFeed feed) =>
{
    DisableCache(req);
    return Results.Json(feed.Status());
});
app.MapGet("/user/{uid}.xml", async (HttpRequest req, string uid, GrimStore store, NativeProfileStore profiles) =>
{
    await LogReq(req);
    DisableCache(req);
    var user = await store.FindUserAsync(onlineId: uid);
    return user is null ? Xml(GrimXml.Decline("user not found"), 404) : Xml(profiles.Xml(user));
});
app.MapGet("/user/no/{number:int}.xml", async (HttpRequest req, int number, GrimStore store, NativeProfileStore profiles) =>
{
    await LogReq(req);
    DisableCache(req);
    var user = await store.FindUserAsync(userNumber: number);
    return user is null ? Xml(GrimXml.Decline("user not found"), 404) : Xml(profiles.Xml(user));
});

// matching
async Task<IResult> Match(HttpRequest req, string action, GrimStore store, Dictionary<string, string>? suppliedParams = null, bool logRequest = true, bool specIII = false)
{
    if (logRequest) await LogReq(req);
    var body = BodyOf(req.HttpContext);
    var ps = suppliedParams ?? (body.Length > 0 ? GrimXml.ParseEnvelope(body).Params : new Dictionary<string, string>());
    string onlineId;
    int userNumber;
    var nativeMatching = suppliedParams is not null;
    var authenticated = false;
    var tok = TokenFrom(req);
    if (tok is not null && await store.GetSessionAsync(tok) is { } sess)
    {
        onlineId = sess.User.OnlineId;
        userNumber = sess.User.UserNumber;
        authenticated = true;
    }
    else
    {
        if (nativeMatching) return Xml(GrimXml.Decline("session required"), 401);
        onlineId = ps.GetValueOrDefault("online_id", ps.GetValueOrDefault("sce_np_id", "anonymous"));
        var u = await store.GetOrCreateUserAsync(onlineId);
        userNumber = u.UserNumber;
    }

    var uid = authenticated
        ? userNumber.ToString(CultureInfo.InvariantCulture)
        : ps.GetValueOrDefault("user_id", ps.GetValueOrDefault("sce_np_id", ps.GetValueOrDefault("online_id", onlineId)));
    if (string.IsNullOrWhiteSpace(uid)) uid = onlineId;

    string? peerId = null;
    if (nativeMatching)
    {
        if (action == "enter")
        {
            peerId = ps.GetValueOrDefault("sce_np_id");
            try
            {
                if (peerId is null || Convert.FromBase64String(peerId).Length != 40)
                    return Xml(GrimXml.Decline("sce_np_id must contain the 40-byte console identity"), 400);
            }
            catch (FormatException) { return Xml(GrimXml.Decline("invalid sce_np_id encoding"), 400); }
        }
    }

    var eventParam = ps.TryGetValue("event_id", out var eventValue) && !string.IsNullOrWhiteSpace(eventValue)
        ? eventValue
        : ps.TryGetValue("board_id", out var boardValue) ? boardValue : "";
    var typeParam = ps.GetValueOrDefault("online_type");
    string eventId;
    string onlineType;
    if (action == "enter")
    {
        if (string.IsNullOrWhiteSpace(eventParam))
            return Xml(GrimXml.Decline("event_id is required"), 400);
        eventId = eventParam;
        onlineType = string.IsNullOrWhiteSpace(typeParam) ? "0" : typeParam;
    }
    else if (store.TryGetActiveRoom(uid, out var activeEvent, out var activeType))
    {
        if (nativeMatching && !string.IsNullOrWhiteSpace(typeParam) && typeParam != activeType)
            return Xml(GrimXml.Decline("online_type mismatch"), 409);
        eventId = activeEvent;
        onlineType = activeType;
    }
    else if (!string.IsNullOrWhiteSpace(eventParam))
    {
        eventId = eventParam;
        onlineType = string.IsNullOrWhiteSpace(typeParam) ? "0" : typeParam;
    }
    else
    {
        return Xml(GrimXml.Decline("player has not entered an event"), 409);
    }

    if (!int.TryParse(ps.GetValueOrDefault("max_entry", "16"), out var maxEntry) || maxEntry <= 0)
        maxEntry = 16;
    var nat = ps.GetValueOrDefault("nat_type", "1");
    var matchAlone = ps.GetValueOrDefault("match_alone", "") is "1" or "true" or "True";
    var packetInterval = 100;
    if (specIII)
    {
        maxEntry = maxEntry is >= 2 and <= 16 ? maxEntry : 16;
        if (ps.TryGetValue("vehicle_packet_interval", out var interval) &&
            (!int.TryParse(interval, out packetInterval) || packetInterval is < 1 or > 10000))
            return Xml(GrimXml.Decline("invalid vehicle_packet_interval"), 400);
    }

    flog.Log($"    [match.{action}] user={onlineId}({uid}) event={eventId} type={onlineType} params={{{string.Join(",", ps.Select(kv => kv.Key + "=" + kv.Value))}}}");

    if (action == "enter")
    {
        if (!store.TryEnterGame(eventId, onlineType, maxEntry, nat, uid, onlineId, out _, peerId, packetInterval))
            return Xml(GrimXml.Decline("event is full"), 409);
        return Xml(GrimXml.Envelope("1", new Dictionary<string, string>
        {
            ["polling_time"] = opts.Matching.PollingTimeSec.ToString(CultureInfo.InvariantCulture),
            ["remaining_time"] = opts.Matching.RemainingTimeSec.ToString(CultureInfo.InvariantCulture),
        }));
    }
    if (action == "join")
    {
        var (state, payload) = store.JoinGame(eventId, onlineType, uid, matchAlone, specIII);
        var f = new Dictionary<string, string>
        {
            ["state"] = state,
            ["polling_time"] = opts.Matching.PollingTimeSec.ToString(CultureInfo.InvariantCulture),
            ["remaining_time"] = opts.Matching.RemainingTimeSec.ToString(CultureInfo.InvariantCulture),
        };
        if (payload is not null) foreach (var (k, v) in payload) if (v is not null) f[k] = v;
        flog.Log($"    [match.join] -> state={state}");
        return Xml(GrimXml.Envelope("1", f));
    }
    if (action == "create")
    {
        var gameId = ps.TryGetValue("game_id", out var pg) && !string.IsNullOrEmpty(pg)
            ? pg : $"{onlineId}@{DateTime.UtcNow:yyyyMMddHHmmss}";
        var blob = ps.GetValueOrDefault("game_stats", ps.GetValueOrDefault("blob", ""));
        if (specIII)
        {
            try
            {
                if (Convert.FromBase64String(blob).Length is < 1 or > 16777216)
                    return Xml(GrimXml.Decline("invalid game_stats"), 400);
            }
            catch (FormatException) { return Xml(GrimXml.Decline("invalid game_stats encoding"), 400); }
        }
        var (state, payload) = store.CreateGame(eventId, onlineType, uid, gameId, blob, specIII);
        var f = new Dictionary<string, string>
        {
            ["state"] = state,
            ["game_id"] = gameId,
            ["remaining_time"] = opts.Matching.RemainingTimeSec.ToString(CultureInfo.InvariantCulture),
        };
        if (payload is not null) foreach (var (k, v) in payload) if (v is not null) f[k] = v;
        flog.Log($"    [match.create] -> state={state} game={gameId}");
        return Xml(GrimXml.Envelope("1", f));
    }
    if (action == "start")
    {
        var gameId = ps.GetValueOrDefault("game_id", "");
        if (!store.StartGame(eventId, onlineType, uid, gameId))
            return Xml(GrimXml.Decline("race start rejected"), 409);
        return Xml(GrimXml.Envelope("1"));
    }
    if (action == "leave")
    {
        store.LeaveGame(eventId, onlineType, uid);
        return Xml(GrimXml.Envelope("1"));
    }
    return Xml(GrimXml.Decline("bad action"), 400);
}
foreach (var prefix in new[] { "/ap/game", "/ap/matching" })
    foreach (var a in new[] { "enter", "join", "create", "leave", "start" })
    {
        var action = a;
        app.MapPost($"{prefix}/{action}", (HttpRequest r, GrimStore s) => Match(r, action, s));
    }

// generic /ap/* grim commands
async Task<IResult> ApCommand(HttpRequest req, string endpoint, GrimStore store)
{
    await LogReq(req);
    var body = BodyOf(req.HttpContext);
    var (cmd, ps) = body.Length > 0 ? GrimXml.ParseEnvelope(body) : ("", new Dictionary<string, string>());
    if (string.IsNullOrEmpty(cmd)) return Xml(GrimXml.Decline("invalid Grim envelope"), 400);
    flog.Log($"    [/ap/{endpoint}] command='{cmd}' params={{{string.Join(",", ps.Select(kv => kv.Key + "=" + kv.Value))}}}");

    if (endpoint.Equals("matching", StringComparison.OrdinalIgnoreCase) || endpoint.Equals("matching2", StringComparison.OrdinalIgnoreCase))
    {
        var normalized = cmd.ToLowerInvariant();
        if (normalized == "matching.getlobbystatus")
            return Xml(store.LobbyStatusXml(ps.GetValueOrDefault("online_type")));
        var action = normalized switch
        {
            "matching.enter" or "matching.entergame" => "enter",
            "matching.join" or "matching.joingame" => "join",
            "matching.create" or "matching.creategame" => "create",
            "matching.leave" or "matching.leavegame" => "leave",
            "matching.start" or "matching.startgame" => "start",
            _ => null,
        };
        if (action is not null)
            return await Match(req, action, store, ps, false, endpoint.Equals("matching2", StringComparison.OrdinalIgnoreCase));
        return Xml(GrimXml.Decline($"unimplemented matching command: {cmd}"), 501);
    }

    string? onlineId = null;
    var tok = TokenFrom(req);
    if (tok is not null && await store.GetSessionAsync(tok) is { } sess) onlineId = sess.User.OnlineId;
    if (endpoint == "profile" && cmd == "profile.update")
    {
        if (onlineId is null) return Xml(GrimXml.Decline("login required"), 401);
        if (ps.GetValueOrDefault("user_id", onlineId) != onlineId)
            return Xml(GrimXml.Decline("profile identity mismatch"), 403);
        var comment = ps.GetValueOrDefault("comment", "");
        if (comment.Length > 1024) return Xml(GrimXml.Decline("comment too long"), 400);
        req.HttpContext.RequestServices.GetRequiredService<NativeProfileStore>().Update(onlineId, comment: comment);
        return Xml(GrimXml.Envelope("1"));
    }
    if (endpoint is "ranking" or "rankings")
    {
        if (cmd is not ("ranking.update" or "ranking.getCount" or "getCount" or "ranking.get" or "ranking.getList" or "ranking.getListMultiBoard" or "ranking.calc" or "ranking.calc2" or "calc2" or "ranking.calcTemporary"))
            return Xml(GrimXml.Decline($"unimplemented ranking command: {cmd}"), 501);
        var response = await RankingProtocol.ExecuteAsync(cmd, ps, onlineId, store, req.HttpContext.RequestServices.GetRequiredService<ReplayStore>());
        return Xml(response.Xml, response.Status);
    }
    onlineId ??= ps.GetValueOrDefault("online_id", "?");

    if (endpoint == "locale" && cmd == "servertime.get")
    {
        DisableCache(req);
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Xml($"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<grim><result>{now}</result></grim>\n");
    }
    if (endpoint == "locale" && cmd == "language.set")
    {
        DisableCache(req);
        var locale = CanonicalLocale(ps.GetValueOrDefault("language"));
        if (locale is null) return Xml(GrimXml.Decline("unsupported language"), 400);
        // Native success path 0x901578 passes result text to locale setter
        // 0x8fecd8. Returning a boolean corrupts subsequent URL components.
        return Xml(GrimXml.Envelope(locale));
    }
    if (ps.ContainsKey("time") && (ps.ContainsKey("event") || ps.ContainsKey("board")))
    {
        var boardId = ps.TryGetValue("event", out var pe) ? pe : ps["board"];
        var rank = await store.SubmitRecordAsync(boardId, onlineId,
            long.TryParse(ps["time"], out var t) ? t : 0,
            ps.GetValueOrDefault("car", ""), ps.GetValueOrDefault("meta", ""));
        return Xml(GrimXml.Envelope("1", new Dictionary<string, string> { ["rank"] = (rank ?? 0).ToString() }));
    }
    if (opts.Grim.StrictMode)
        return Xml(GrimXml.Decline($"unimplemented command on /ap/{endpoint}: {cmd}"), 501);
    return Xml(GrimXml.Envelope("1"));
}
app.MapPost("/ap/{endpoint}", (HttpRequest r, string endpoint, GrimStore s) => ApCommand(r, endpoint, s));

// Native replay commands usually carry their XML envelope in the Xml HTTP
// header (rankingReplay.put, body = replay bytes). rankingReplay.duplicate
// instead carries the envelope in the POST body (no Xml header, no binary).
async Task<IResult> ReplayCommand(HttpRequest req, GrimStore store, ReplayStore replays, bool upload)
{
    await LogReq(req);
    var token = TokenFrom(req);
    var session = token is null ? null : await store.GetSessionAsync(token);
    if (session is null) return Xml(GrimXml.Decline("login required"), 401);
    var envelope = req.Headers["Xml"].ToString();
    var envelopeBytes = string.IsNullOrWhiteSpace(envelope) && !upload && BodyOf(req.HttpContext).Length < 8192
        ? BodyOf(req.HttpContext) : Encoding.UTF8.GetBytes(envelope);
    var (command, parameters) = GrimXml.ParseEnvelope(envelopeBytes);
    string? Board(string key) => int.TryParse(parameters.GetValueOrDefault(key), out int id) && id > 0 ? id.ToString() : null;
    async Task<RankingEntry?> OwnRecord(string board) => (await store.GetNativeBoardAsync(board)).FirstOrDefault(r => r.OnlineId == session.Value.User.OnlineId);
    if (upload && command == "rankingReplay.put" && Board("board_id") is { } board)
    {
        var row = await OwnRecord(board);
        var bytes = BodyOf(req.HttpContext);
        if (row is null || bytes.Length is < 1 or > 16777216) return Xml(GrimXml.Decline("invalid replay upload"), 400);
        await replays.SaveAsync(row, bytes);
        return Xml(GrimXml.Envelope("1"));
    }
    if (!upload && command == "rankingReplay.duplicate" && Board("source_board_id") is { } source && Board("destination_board_id") is { } destination)
    {
        var first = await OwnRecord(source);
        var second = await OwnRecord(destination);
        if (first is null || second is null || !await replays.DuplicateAsync(first, second)) return Xml(GrimXml.Decline("source replay or matching result not found"), 404);
        return Xml(GrimXml.Envelope("1"));
    }
    return Xml(GrimXml.Decline("unsupported replay command"), 400);
}
app.MapPost("/ap/replay/upload", (HttpRequest r, GrimStore s, ReplayStore p) => ReplayCommand(r, s, p, true));
app.MapPost("/ap/replay", (HttpRequest r, GrimStore s, ReplayStore p) => ReplayCommand(r, s, p, false));
app.MapGet("/replay/{id:int}.dat", async (int id, GrimStore store, ReplayStore replays) =>
{
    var row = await store.GetRankingByIdAsync(id);
    return row is not null && replays.Find(row) is { } path
        ? Results.File(path, "application/octet-stream", enableRangeProcessing: true)
        : Xml(GrimXml.Decline("replay not found"), 404);
});

// admin Web UI + JSON APIs (operator only, no auth - LAN tool)
app.MapGet("/admin", () => Results.Content(AdminUi.Html, "text/html; charset=utf-8"));
app.MapGet("/admin/api/traffic", (HttpRequest req, TrafficStore t) =>
{
    int.TryParse(req.Query["n"], out var n);
    return Results.Json(t.Latest(n == 0 ? 100 : n));
});
app.MapPost("/admin/api/traffic/clear", (TrafficStore t) =>
{
    t.Clear();
    return Results.Json(new { cleared = true });
});
app.MapGet("/admin/api/users", async (HttpRequest req, GrimStore s) =>
{
    int.TryParse(req.Query["n"], out var n);
    var users = await s.ListUsersAsync(n == 0 ? 100 : n);
    return Results.Json(users.Select(u => new
    {
        onlineId = u.OnlineId,
        userNumber = u.UserNumber,
        region = u.Region,
        nickname = u.Nickname,
        lastLogin = u.LastLogin.ToString("yyyy-MM-dd HH:mm:ss"),
    }));
});
app.MapGet("/admin/api/rankings", async (HttpRequest req, GrimStore s) =>
{
    int.TryParse(req.Query["n"], out var n);
    var board = req.Query["board"].ToString();
    var rows = await s.ListRankingsAsync(string.IsNullOrEmpty(board) ? null : board, n == 0 ? 100 : n);
    return Results.Json(rows.Select(r => new
    {
        boardId = r.BoardId,
        onlineId = r.OnlineId,
        timeMs = r.TimeMs,
        car = r.Car,
        date = r.Date.ToString("yyyy-MM-dd HH:mm:ss"),
    }));
});
app.MapGet("/admin/api/rooms", (GrimStore s) => Results.Json(s.RoomsSnapshot()));
app.MapGet("/admin/api/rtime", (RtimeService r) => Results.Json(r.Snapshot()));

// catch-alls: file fallback for GET, strict/benign for POST
app.MapGet("/{**path}", async (HttpRequest req, string path) =>
{
    await LogReq(req);
    var rel = (req.Path.Value ?? "/").TrimStart('/');
    if (rel.Contains("..")) return Results.Text("bad request", "text/plain", statusCode: 400);
    var candidate = Path.Combine(resDir, rel.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(candidate))
    {
        flog.Log($"    [fallback] serving resources/{rel}");
        return SendResFile(candidate);
    }
    flog.Log($"    [fallback] NO resource for GET {req.Path} (drop a file at resources/{req.Path} to stub it)");
    return Xml(GrimXml.Decline("not found"), 404);
});
app.MapPost("/{**path}", async (HttpRequest req, string path, GrimStore store) =>
{
    await LogReq(req);
    // Don't swallow real /ap/* typos that already matched above; this is truly unknown.
    var body = BodyOf(req.HttpContext);
    var (cmd, ps) = body.Length > 0 ? GrimXml.ParseEnvelope(body) : ("", new Dictionary<string, string>());
    if (string.IsNullOrEmpty(cmd)) return Xml(GrimXml.Decline("invalid Grim envelope"), 400);
    flog.Log($"    [unknown POST] {req.Path} command='{cmd}' params={{{string.Join(",", ps.Select(kv => kv.Key + "=" + kv.Value))}}} raw={Encoding.UTF8.GetString(body.Take(512).ToArray())}");
    if (opts.Grim.StrictMode)
    {
        flog.Log($"    [strict_mode] rejecting unknown POST {req.Path} with 501");
        return Xml(GrimXml.Decline($"unimplemented endpoint: {req.Path}"), 501);
    }
    return Xml(GrimXml.Envelope("1"));
});

app.Run();

static void ApplyLegacySnakeCase(IConfiguration cfg, GrimRootOptions o)
{
    // config.json from Python uses snake_case; Configuration binder needs exact keys.
    // Map the known ones manually when present.
    string? S(string section, string key) => cfg.GetSection(section)[key];
    var s = cfg.GetSection("server");
    if (s["host_ip"] is { } v) o.Server.HostIp = v;
    if (int.TryParse(s["http_port"], out var p1)) o.Server.HttpPort = p1;
    if (int.TryParse(s["https_port"], out var p2)) o.Server.HttpsPort = p2;
    if (int.TryParse(s["extra_http_port"], out var p3)) o.Server.ExtraHttpPort = p3;
    if (s["log_dir"] is { } v2) o.Server.LogDir = v2;
    if (s["resources_dir"] is { } rd) o.Server.ResourcesDir = rd;
    var d = cfg.GetSection("dns");
    if (bool.TryParse(d["enabled"], out var db)) o.Dns.Enabled = db;
    if (int.TryParse(d["port"], out var dp)) o.Dns.Port = dp;
    var t = cfg.GetSection("tls");
    if (bool.TryParse(t["enabled"], out var tb)) o.Tls.Enabled = tb;
    if (t["cert_file"] is { } cf) o.Tls.CertFile = cf;
    if (t["key_file"] is { } kf) o.Tls.KeyFile = kf;
    var g = cfg.GetSection("grim");
    if (bool.TryParse(g["maintenance"], out var mb)) o.Grim.Maintenance = mb;
    if (g["maintenance_message"] is { } mm) o.Grim.MaintenanceMessage = mm;
    if (bool.TryParse(g["strict_mode"], out var sm)) o.Grim.StrictMode = sm;
    if (int.TryParse(g["session_lifetime_sec"], out var sl)) o.Grim.SessionLifetimeSec = sl;
    if (g["server_id"] is { } si) o.Grim.ServerId = si;
    if (g["region"] is { } rg) o.Grim.Region = rg;
    var m = cfg.GetSection("matching");
    if (int.TryParse(m["polling_time_sec"], out var pt)) o.Matching.PollingTimeSec = pt;
    if (int.TryParse(m["remaining_time_sec"], out var rt)) o.Matching.RemainingTimeSec = rt;
    var r = cfg.GetSection("rtime");
    if (bool.TryParse(r["enabled"], out var rb)) o.Rtime.Enabled = rb;
    if (int.TryParse(r["port"], out var rp)) o.Rtime.Port = rp;
    if (r["reply_mode"] is { } rm) o.Rtime.ReplyMode = rm;
    _ = S("", "");
}

sealed class FreshXmlResult(string path, string contentType) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        // Media keeps its conditional/range behavior; XML discovery must be
        // re-parsed while its recovered schema is changing.
        await Results.Bytes(await File.ReadAllBytesAsync(path, context.RequestAborted), contentType).ExecuteAsync(context);
    }
}
