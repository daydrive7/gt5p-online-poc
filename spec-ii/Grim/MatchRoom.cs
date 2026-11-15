namespace Gt5pOnlineServer.Grim;

// One matchmaking pool per (event_id, online_type).
// State machine ported from grim_state.py MatchRoom (recovered from matching.adc):
//   enter -> {polling_time, remaining_time}
//   join  -> matching | create (you host) | join (join host blob) | nohost
//   create -> confirms hosting or joins the winner
//   leave -> removes player, clears host, drops empty room.
public sealed class MatchRoom
{
    public string EventId { get; }
    public string OnlineType { get; }
    public int MaxEntry { get; set; }
    public Dictionary<string, RoomPlayer> Players { get; } = new();
    public string? HostUserId { get; set; }
    public string? HostGameId { get; set; }
    public string? HostBlob { get; set; }
    public bool Started { get; set; }
    public DateTime Created { get; } = DateTime.UtcNow;

    public MatchRoom(string eventId, string onlineType, int maxEntry = 16)
    {
        EventId = eventId;
        OnlineType = onlineType;
        MaxEntry = maxEntry;
    }
}

public sealed class RoomPlayer
{
    public string OnlineId { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string Nat { get; set; } = "1";
    public DateTime EnteredAt { get; set; } = DateTime.UtcNow;
}

public sealed record MatchingUser(string OnlineId, string UserNumber, string PeerId);
