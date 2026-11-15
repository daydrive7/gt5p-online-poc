using System.Text.Json;
using System.Xml.Linq;
using Gt5pOnlineServer.Data;

namespace Gt5pOnlineServer.Services;

public sealed record NativeProfile(string Comment = "", long Send = 0, long Receive = 0, DateTimeOffset? BandUpdated = null);

// Spec III profile parser 0x8e9960 and band.set sender 0x901128.
// Keep added fields in an atomic sidecar, compatible with existing DB schemas.
public sealed class NativeProfileStore
{
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, NativeProfile> profiles;

    public NativeProfileStore(string logDir)
    {
        path = Path.Combine(logDir, "speciii-profiles.json");
        profiles = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, NativeProfile>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public void Update(string onlineId, string? comment = null, long? send = null, long? receive = null)
    {
        lock (gate)
        {
            var previous = profiles.GetValueOrDefault(onlineId, new());
            var next = previous with
            {
                Comment = comment ?? previous.Comment,
                Send = send ?? previous.Send,
                Receive = receive ?? previous.Receive,
                BandUpdated = send is not null && receive is not null ? DateTimeOffset.UtcNow : previous.BandUpdated,
            };
            // Persist before publishing state, so an I/O failure is not acknowledged.
            var snapshot = new Dictionary<string, NativeProfile>(profiles) { [onlineId] = next };
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
            File.Move(path + ".tmp", path, overwrite: true);
            profiles[onlineId] = next;
        }
    }

    public string Xml(UserEntity user)
    {
        NativeProfile profile;
        lock (gate) profile = profiles.GetValueOrDefault(user.OnlineId, new());
        var root = new XElement("profile",
            new XElement("id", user.OnlineId), new XElement("number", user.UserNumber),
            new XElement("comment", profile.Comment),
            new XElement("band_test", profile.BandUpdated is null ? 0 : 1),
            new XElement("band_up", profile.Send), new XElement("band_down", profile.Receive));
        if (profile.BandUpdated is { } timestamp)
            root.Add(new XElement("band_update_time", timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root + "\n";
    }
}
