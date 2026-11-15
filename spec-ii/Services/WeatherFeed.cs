using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Gt5pOnlineServer.Services;

public enum WeatherUnit
{
    Celsius,
    Fahrenheit
}

public sealed class WeatherFeed : BackgroundService
{
    public sealed record City(int Id, string Name, double Latitude, double Longitude);
    public sealed record Reading(int Id, int Temperature, int Icon, DateTimeOffset Updated);

    private readonly City[] cities;
    private readonly string cachePath;
    private readonly bool enabled;
    private readonly int refreshMinutes;
    private readonly WeatherUnit defaultUnit;
    private readonly GrimFileLogger log;
    private readonly object stateGate = new();
    private Reading[] readings = [];
    private long lastRefreshTicks;
    private long nextRefreshTicks;

    public WeatherFeed(string resources, string cacheDirectory, bool enabled, int refreshMinutes, string unit, GrimFileLogger log)
    {
        cities = JsonSerializer.Deserialize<City[]>(File.ReadAllText(Path.Combine(resources, "weather-cities.json"))) ?? [];
        cachePath = Path.Combine(cacheDirectory, "weather-cache.json");
        this.enabled = enabled;
        this.refreshMinutes = Math.Clamp(refreshMinutes, 1, 1440);
        this.log = log;
        defaultUnit = ParseUnit(unit);
        LoadCache();
    }

    public WeatherUnit DefaultUnit => defaultUnit;
    public int RefreshMinutes => refreshMinutes;
    public string UnitName => UnitLabel(defaultUnit);

    public static WeatherUnit ParseUnit(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "f" or "fahrenheit" or "us" => WeatherUnit.Fahrenheit,
            _ => WeatherUnit.Celsius
        };

    public static string UnitLabel(WeatherUnit unit)
        => unit == WeatherUnit.Fahrenheit ? "fahrenheit" : "celsius";

    public static int ToFahrenheit(int celsius)
        => (int)Math.Round(celsius * 9.0 / 5.0 + 32.0, MidpointRounding.AwayFromZero);

    public string RootXml()
    {
        return new XElement("categories", cities.Select(city => new XElement("category",
            new XAttribute("id1", city.Id),
            new XAttribute("id2", 0),
            new XAttribute("name", city.Name)))).ToString();
    }

    public string? ItemXml(int id) => ItemXml(id, defaultUnit);

    public string? ItemXml(int id, WeatherUnit unit)
    {
        var city = cities.FirstOrDefault(c => c.Id == id);
        var reading = Volatile.Read(ref readings).FirstOrDefault(r => r.Id == id);
        if (city is null || reading is null) return null;
        var temperature = unit == WeatherUnit.Fahrenheit
            ? ToFahrenheit(reading.Temperature)
            : reading.Temperature;
        return new XElement("weather",
            new XElement("region_id", id),
            new XElement("region_name", city.Name),
            new XElement("temperature", temperature),
            new XElement("weather_id", reading.Icon),
            new XElement("update_time", reading.Updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).ToString();
    }

    public object Status()
    {
        var snapshot = Volatile.Read(ref readings);
        lock (stateGate)
        {
            return new
            {
                source = "Open-Meteo",
                attribution = "https://open-meteo.com/",
                unit = UnitName,
                refresh_minutes = refreshMinutes,
                last_refresh = LastRefresh(),
                next_refresh = NextRefresh(),
                cities,
                readings = snapshot
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadCache();
        if (!enabled) return;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lat = string.Join(',', cities.Select(city => city.Latitude.ToString(CultureInfo.InvariantCulture)));
        var lon = string.Join(',', cities.Select(city => city.Longitude.ToString(CultureInfo.InvariantCulture)));
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,is_day&timezone=GMT&timeformat=unixtime";
        var interval = TimeSpan.FromMinutes(refreshMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            var refreshed = false;
            try
            {
                using var data = JsonDocument.Parse(await client.GetStringAsync(url, stoppingToken));
                var locations = data.RootElement.EnumerateArray().ToArray();
                if (locations.Length != cities.Length) throw new InvalidDataException("Incomplete weather response");
                var fresh = cities.Select((city, index) =>
                {
                    var current = locations[index].GetProperty("current");
                    var updated = DateTimeOffset.FromUnixTimeSeconds(current.GetProperty("time").GetInt64());
                    var temperature = current.GetProperty("temperature_2m").GetDouble();
                    if (!double.IsFinite(temperature) || temperature is < -100 or > 70 || updated > DateTimeOffset.UtcNow.AddMinutes(10))
                        throw new InvalidDataException("Invalid weather reading");
                    return new Reading(city.Id, (int)Math.Round(temperature, MidpointRounding.AwayFromZero),
                        Icon(current.GetProperty("weather_code").GetInt32(), current.GetProperty("is_day").GetInt32() == 1), updated);
                }).ToArray();
                await File.WriteAllTextAsync(cachePath + ".tmp", JsonSerializer.Serialize(fresh), stoppingToken);
                File.Move(cachePath + ".tmp", cachePath, true);
                Volatile.Write(ref readings, fresh);
                lock (stateGate)
                {
                    Interlocked.Exchange(ref lastRefreshTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
                }
                refreshed = true;
                log.Log($"[weather] refreshed {fresh.Length} cities; provider time {fresh[0].Updated:O}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Log($"[weather] refresh failed; retaining original timestamps: {ex.Message}");
            }

            var delay = refreshed ? interval : TimeSpan.FromMinutes(Math.Min(5, refreshMinutes));
            var next = DateTimeOffset.UtcNow.Add(delay);
            lock (stateGate)
            {
                Interlocked.Exchange(ref nextRefreshTicks, next.UtcDateTime.Ticks);
            }
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void LoadCache()
    {
        if (!File.Exists(cachePath)) return;
        try
        {
            var cached = JsonSerializer.Deserialize<Reading[]>(File.ReadAllText(cachePath)) ?? [];
            Volatile.Write(ref readings, cached);
            if (cached.Length > 0)
            {
                lock (stateGate)
                {
                    Interlocked.Exchange(ref lastRefreshTicks, cached.Max(reading => reading.Updated).UtcDateTime.Ticks);
                }
            }
        }
        catch (Exception ex)
        {
            log.Log($"[weather] cache ignored: {ex.Message}");
        }
    }

    private DateTimeOffset? LastRefresh()
    {
        var ticks = Interlocked.Read(ref lastRefreshTicks);
        return ticks == 0 ? null : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private DateTimeOffset? NextRefresh()
    {
        var ticks = Interlocked.Read(ref nextRefreshTicks);
        return ticks == 0 ? null : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private static int Icon(int code, bool day) => code switch
    {
        0 => day ? 1 : 18,
        1 => day ? 4 : 19,
        2 or 3 => day ? 5 : 20,
        45 or 48 => 16,
        51 or 53 or 55 => 6,
        56 or 57 or 66 or 67 => 11,
        61 or 80 => 6,
        63 or 81 => 7,
        65 or 82 => day ? 8 : 21,
        71 or 77 or 85 => 12,
        73 or 75 or 86 => day ? 13 : 22,
        95 or 96 or 99 => day ? 9 : 23,
        _ => 17
    };
}
