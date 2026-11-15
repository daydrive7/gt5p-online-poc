using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using Gt5pOnlineServer.Data;
using Gt5pOnlineServer.Grim;
using Microsoft.EntityFrameworkCore;

namespace Gt5pOnlineServer.Services;

// Business logic: users/sessions/rankings in Postgres, match rooms in-memory.
// Mirrors grim_state.py GrimState semantics.
public sealed class GrimStore(IServiceScopeFactory scopes)
{
    // ponytail: process-local rooms and per-request pool scans; use shared indexed storage before multi-instance hosting.
    private readonly HashSet<MatchRoom> _rooms = [];
    private readonly Dictionary<string, MatchRoom> _activeRooms = new(StringComparer.Ordinal);
    private readonly object _matchLock = new();
    private readonly SemaphoreSlim _userLock = new(1, 1);
    private readonly SemaphoreSlim _rankingLock = new(1, 1);

    public async Task SaveNativeRankingAsync(string board, string onlineId, int score, string metadata)
    {
        await _rankingLock.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GrimDbContext>();
            var row = await db.Rankings.FirstOrDefaultAsync(r => r.BoardId == board && r.OnlineId == onlineId && r.Meta.StartsWith("{\"gt5p\":1,"));
            if (row is not null && row.TimeMs <= score) return;
            if (row is null)
            {
                row = new RankingEntry { BoardId = board, OnlineId = onlineId };
                db.Rankings.Add(row);
            }
            row.TimeMs = score;
            row.Meta = metadata;
            row.Date = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        finally { _rankingLock.Release(); }
    }

    public async Task<List<RankingEntry>> GetNativeBoardAsync(string board)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrimDbContext>();
        // Old synthetic records lack the game's encrypted statistics and cannot be rendered.
        // Keep them in the database and admin view, without mixing them into native boards.
        return await db.Rankings.Where(r => r.BoardId == board && r.Meta.StartsWith("{\"gt5p\":1,"))
            .OrderBy(r => r.TimeMs).ThenBy(r => r.Id).ToListAsync();
    }

    public async Task<RankingEntry?> GetRankingByIdAsync(int id)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GrimDbContext>().Rankings.FindAsync(id);
    }

    public async Task<UserEntity> GetOrCreateUserAsync(string onlineId, string region = "", CancellationToken ct = default)
    {
        await _userLock.WaitAsync(ct);
        try
        {
            using var s = scopes.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
            var u = await db.Users.SingleOrDefaultAsync(x => x.OnlineId == onlineId, ct);
            if (u is null)
            {
                int next = await db.Users.AnyAsync(ct)
                    ? await db.Users.MaxAsync(x => x.UserNumber, ct) + 1
                    : 1;
                u = new UserEntity
                {
                    OnlineId = onlineId,
                    UserNumber = next,
                    Region = region,
                    Country = region,
                    Nickname = onlineId,
                    CreatedAt = DateTime.UtcNow,
                    LastLogin = DateTime.UtcNow,
                };
                db.Users.Add(u);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                u.LastLogin = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(region) && string.IsNullOrEmpty(u.Region)) u.Region = region;
                await db.SaveChangesAsync(ct);
            }
            return u;
        }
        finally { _userLock.Release(); }
    }

    public async Task<SessionEntity> OpenSessionAsync(UserEntity user, int lifetimeSec, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        var old = db.Sessions.Where(x => x.OnlineId == user.OnlineId);
        db.Sessions.RemoveRange(old);
        var token = RandomNumberGenerator.GetHexString(48).ToLowerInvariant(); // = token_hex(24)
        var sess = new SessionEntity
        {
            Token = token,
            OnlineId = user.OnlineId,
            UserNumber = user.UserNumber,
            ExpiresAt = DateTime.UtcNow.AddSeconds(lifetimeSec),
        };
        db.Sessions.Add(sess);
        await db.SaveChangesAsync(ct);
        return sess;
    }

    public async Task<(SessionEntity Sess, UserEntity User)?> GetSessionAsync(string token, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        var sess = await db.Sessions.SingleOrDefaultAsync(x => x.Token == token, ct);
        if (sess is null) return null;
        if (sess.ExpiresAt <= DateTime.UtcNow)
        {
            db.Sessions.Remove(sess);
            await db.SaveChangesAsync(ct);
            return null;
        }
        var user = await db.Users.SingleOrDefaultAsync(x => x.OnlineId == sess.OnlineId, ct);
        if (user is null) return null;
        return (sess, user);
    }

    public async Task<SessionEntity?> ExtendSessionAsync(string token, int lifetimeSec, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        var sess = await db.Sessions.SingleOrDefaultAsync(x => x.Token == token, ct);
        if (sess is null || sess.ExpiresAt <= DateTime.UtcNow) return null;
        sess.ExpiresAt = DateTime.UtcNow.AddSeconds(lifetimeSec);
        await db.SaveChangesAsync(ct);
        return sess;
    }

    public async Task<UserEntity?> FindUserAsync(string? onlineId = null, int? userNumber = null)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrimDbContext>();
        return await db.Users.SingleOrDefaultAsync(user => userNumber != null ? user.UserNumber == userNumber : user.OnlineId == onlineId);
    }

    public async Task CloseSessionAsync(string token)
    {
        var session = await GetSessionAsync(token);
        if (session is null) return;
        var uid = session.Value.User.UserNumber.ToString(CultureInfo.InvariantCulture);
        if (TryGetActiveRoom(uid, out var eventId, out var type)) LeaveGame(eventId, type, uid);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GrimDbContext>();
        var row = await db.Sessions.FindAsync(token);
        if (row is not null)
        {
            db.Sessions.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    public async Task<int?> SubmitRecordAsync(string boardId, string onlineId, long timeMs, string car = "", string meta = "", CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        db.Rankings.Add(new RankingEntry
        {
            BoardId = boardId,
            OnlineId = onlineId,
            TimeMs = timeMs,
            Car = car,
            Meta = meta,
            Date = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        // Keep top 500 per board like Python (sort by time_ms, del board[500:]).
        var excess = await db.Rankings.Where(x => x.BoardId == boardId)
            .OrderBy(x => x.TimeMs).ThenBy(x => x.Id)
            .Skip(500).ToListAsync(ct);
        if (excess.Count > 0)
        {
            db.Rankings.RemoveRange(excess);
            await db.SaveChangesAsync(ct);
        }
        var rank = await db.Rankings.Where(x => x.BoardId == boardId && x.TimeMs < timeMs).CountAsync(ct) + 1;
        return rank;
    }

    public async Task<List<RankingEntry>> GetBoardAsync(string boardId, int limit = 50, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        return await db.Rankings.Where(x => x.BoardId == boardId)
            .OrderBy(x => x.TimeMs).ThenBy(x => x.Id)
            .Take(limit).ToListAsync(ct);
    }

    public async Task<int> GetCountAsync(string boardId, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        return await db.Rankings.Where(x => x.BoardId == boardId).CountAsync(ct);
    }

    // ---- admin UI reads ----
    public async Task<List<UserEntity>> ListUsersAsync(int limit = 100, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        return await db.Users.OrderByDescending(x => x.LastLogin).Take(limit).ToListAsync(ct);
    }

    public async Task<List<RankingEntry>> ListRankingsAsync(string? boardId = null, int limit = 100, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        var q = db.Rankings.AsQueryable();
        if (!string.IsNullOrEmpty(boardId)) q = q.Where(x => x.BoardId == boardId);
        return await q.OrderBy(x => x.TimeMs).ThenBy(x => x.Id).Take(limit).ToListAsync(ct);
    }

    public object RoomsSnapshot()
    {
        lock (_matchLock)
        {
            return _rooms.Select(r => new
            {
                event_id = r.EventId,
                online_type = r.OnlineType,
                max_entry = r.MaxEntry,
                started = r.Started,
                host_user_id = r.HostUserId,
                host_game_id = r.HostGameId,
                players = r.Players.OrderBy(p => p.Value.EnteredAt).Select(p => new
                {
                    user_id = p.Key,
                    online_id = p.Value.OnlineId,
                    nat = p.Value.Nat,
                }).ToList(),
            }).ToList();
        }
    }

    public string LobbyStatusXml(string? onlineType = null)
    {
        lock (_matchLock)
        {
            return new XElement("grim", new XElement("result", 1),
                _rooms.Where(room => onlineType is null || room.OnlineType == onlineType)
                    .GroupBy(room => room.EventId).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new XElement("event", new XAttribute("event_id", group.Key),
                        new XAttribute("entry", group.Sum(room => room.Players.Count))))).ToString();
        }
    }

    public bool TryGetActiveRoom(string userId, out string eventId, out string onlineType)
    {
        lock (_matchLock)
        {
            if (_activeRooms.TryGetValue(userId, out var room))
            {
                eventId = room.EventId;
                onlineType = room.OnlineType;
                return true;
            }
        }

        eventId = "";
        onlineType = "";
        return false;
    }

    public bool TryEnterGame(string eventId, string onlineType, int maxEntry, string nat, string userId, string onlineId, out MatchRoom? entered, string? peerId = null, int vehiclePacketInterval = 100)
    {
        entered = null;
        var requestedMax = maxEntry > 0 ? maxEntry : 16;
        lock (_matchLock)
        {
            _activeRooms.TryGetValue(userId, out var active);
            if (active is not null && active.EventId == eventId && active.OnlineType == onlineType)
            {
                entered = active;
                return active.Players.TryGetValue(userId, out var player) && player.OnlineId == onlineId &&
                    player.PeerId == (peerId ?? onlineId);
            }

            var room = _rooms.OrderBy(candidate => candidate.Created).FirstOrDefault(candidate =>
                candidate.EventId == eventId && candidate.OnlineType == onlineType && !candidate.Started &&
                candidate.HostUserId is null && candidate.Players.Count < candidate.MaxEntry &&
                candidate.MaxEntry == requestedMax && candidate.VehiclePacketInterval == vehiclePacketInterval);
            if (room is null)
            {
                room = new MatchRoom(eventId, onlineType, requestedMax) { VehiclePacketInterval = vehiclePacketInterval };
                _rooms.Add(room);
            }
            if (active is not null) RemovePlayerLocked(active, userId);

            room.Players[userId] = new RoomPlayer
            {
                OnlineId = onlineId,
                PeerId = peerId ?? onlineId,
                Nat = nat,
                EnteredAt = DateTime.UtcNow,
            };
            _activeRooms[userId] = room;
            entered = room;
            return true;
        }
    }

    public MatchRoom EnterGame(string eventId, string onlineType, int maxEntry, string nat, string userId, string onlineId)
    {
        if (!TryEnterGame(eventId, onlineType, maxEntry, nat, userId, onlineId, out var room) || room is null)
            throw new InvalidOperationException("match is full");
        return room;
    }

    public (string State, Dictionary<string, string>? Payload) JoinGame(string eventId, string onlineType, string userId, bool matchAlone = false, bool specIII = false)
    {
        lock (_matchLock)
        {
            var room = FindRoomLocked(eventId, onlineType, userId);
            if (room is null) return ("nohost", null);

            // Spec III parser 0x8eefa8 appends every <user>; the Adhoc layer
            // builds entry_max from this complete roster and looks up itself.
            var users = System.Text.Json.JsonSerializer.Serialize(room.Players.OrderBy(p => p.Value.EnteredAt).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select((p, grid) => new MatchingUser(p.Value.OnlineId, p.Key, p.Value.PeerId, specIII ? grid : null, specIII ? 0 : null))
                .Where(p => specIII || p.UserNumber != userId).ToArray());
            Dictionary<string, string> Payload() => new()
            {
                ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                ["users"] = users,
                ["vehicle_packet_interval"] = room.VehiclePacketInterval.ToString(CultureInfo.InvariantCulture),
                ["network_topology"] = "0", // Spec III GameType table 0x1241648: P2P = 0.
            };
            if (room.HostUserId is null)
            {
                if (room.Started) return ("nohost", null);
                var oldest = room.Players.OrderBy(p => p.Value.EnteredAt).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                bool ready = matchAlone || room.Players.Count >= 2;
                if (ready && userId == oldest)
                {
                    room.HostUserId = userId;
                    return ("create", Payload());
                }
                return ("matching", new Dictionary<string, string>
                {
                    ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                });
            }

            if (room.HostUserId != userId && room.HostBlob is null)
                return ("matching", new Dictionary<string, string>
                {
                    ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                });

            var payload = Payload();
            payload["game_id"] = room.HostGameId ?? "";
            if (room.HostBlob is not null) payload["game_stats"] = room.HostBlob;
            if (room.HostUserId == userId) return ("create", payload);
            payload["game_stats"] = room.HostBlob ?? "";
            return ("join", payload);
        }
    }

    public (string State, Dictionary<string, string>? Payload) CreateGame(string eventId, string onlineType, string userId, string gameId, string blob, bool specIII = false)
    {
        lock (_matchLock)
        {
            var room = FindRoomLocked(eventId, onlineType, userId);
            if (room is null) return ("nohost", null);

            if (room.HostUserId is null)
            {
                if (room.Started) return ("nohost", null);
                var oldest = room.Players.OrderBy(p => p.Value.EnteredAt).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                if (oldest != userId) return JoinGame(eventId, onlineType, userId, specIII: specIII);
                room.HostUserId = userId;
            }

            if (room.HostUserId == userId)
            {
                if (room.HostGameId is not null && (room.HostGameId != gameId || room.HostBlob != blob))
                    return ("nohost", null);
                room.HostGameId = gameId;
                room.HostBlob = blob;
            }
            return JoinGame(eventId, onlineType, userId, specIII: specIII);
        }
    }

    public bool StartGame(string eventId, string onlineType, string userId, string gameId)
    {
        lock (_matchLock)
        {
            var room = FindRoomLocked(eventId, onlineType, userId);
            if (room is null || room.HostUserId != userId) return false;
            if (string.IsNullOrEmpty(room.HostGameId) || !string.Equals(room.HostGameId, gameId, StringComparison.Ordinal)) return false;
            room.Started = true;
            return true;
        }
    }

    public void LeaveGame(string eventId, string onlineType, string userId)
    {
        lock (_matchLock)
        {
            if (_activeRooms.TryGetValue(userId, out var active))
                RemovePlayerLocked(active, userId);
        }
    }

    private MatchRoom? FindRoomLocked(string eventId, string onlineType, string userId)
        => _activeRooms.TryGetValue(userId, out var room) && room.EventId == eventId && room.OnlineType == onlineType ? room : null;

    private void RemovePlayerLocked(MatchRoom room, string userId)
    {
        if (room.Players.Remove(userId))
        {
            if (room.HostUserId == userId)
            {
                room.HostUserId = null;
                room.HostGameId = null;
                room.HostBlob = null;
            }
            if (room.Players.Count == 0) _rooms.Remove(room);
        }
        _activeRooms.Remove(userId);
    }
}
