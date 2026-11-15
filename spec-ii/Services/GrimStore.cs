using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Gt5pOnlineServer.Data;
using Gt5pOnlineServer.Grim;
using Microsoft.EntityFrameworkCore;

namespace Gt5pOnlineServer.Services;

// Business logic: users/sessions/rankings in Postgres, match rooms in-memory.
// Mirrors grim_state.py GrimState semantics.
public sealed class GrimStore(IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<(string Event, string Type), MatchRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, (string Event, string Type)> _activeRooms = new();
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

    public async Task<bool> ExtendSessionAsync(string token, int lifetimeSec, CancellationToken ct = default)
    {
        using var s = scopes.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<GrimDbContext>();
        var sess = await db.Sessions.SingleOrDefaultAsync(x => x.Token == token, ct);
        if (sess is null || sess.ExpiresAt <= DateTime.UtcNow) return false;
        sess.ExpiresAt = DateTime.UtcNow.AddSeconds(lifetimeSec);
        await db.SaveChangesAsync(ct);
        return true;
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
            return _rooms.Values.Select(r => new
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

    public bool TryGetActiveRoom(string userId, out string eventId, out string onlineType)
    {
        lock (_matchLock)
        {
            if (_activeRooms.TryGetValue(userId, out var room))
            {
                eventId = room.Event;
                onlineType = room.Type;
                return true;
            }
        }

        eventId = "";
        onlineType = "";
        return false;
    }

    public bool TryEnterGame(string eventId, string onlineType, int maxEntry, string nat, string userId, string onlineId, out MatchRoom? entered, string? peerId = null)
    {
        entered = null;
        var requestedMax = maxEntry > 0 ? maxEntry : 16;
        var key = (eventId, onlineType);

        lock (_matchLock)
        {
            foreach (var pair in _rooms.ToArray())
            {
                if (pair.Value.Players.ContainsKey(userId))
                    RemovePlayerLocked(pair.Key, userId);
            }
            _activeRooms.TryRemove(userId, out _);

            if (!_rooms.TryGetValue(key, out var room))
            {
                room = new MatchRoom(eventId, onlineType, requestedMax);
                _rooms[key] = room;
            }
            else if (room.Players.Count == 0 && room.HostUserId is null)
            {
                room.MaxEntry = requestedMax;
                room.Started = false;
            }

            if (room.Started) return false;
            if (!room.Players.ContainsKey(userId) && room.Players.Count >= room.MaxEntry) return false;

            room.Players[userId] = new RoomPlayer
            {
                OnlineId = onlineId,
                PeerId = peerId ?? onlineId,
                Nat = nat,
                EnteredAt = DateTime.UtcNow,
            };
            _activeRooms[userId] = key;
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

    public (string State, Dictionary<string, string>? Payload) JoinGame(string eventId, string onlineType, string userId, bool matchAlone = false)
    {
        lock (_matchLock)
        {
            if (!_rooms.TryGetValue((eventId, onlineType), out var room)) return ("nohost", null);
            if (!room.Players.ContainsKey(userId)) return ("nohost", null);

            // The local driver is added by the game; only return remote network identities.
            var users = System.Text.Json.JsonSerializer.Serialize(room.Players.Where(p => p.Key != userId).OrderBy(p => p.Value.EnteredAt).Select(p => new MatchingUser(p.Value.OnlineId, p.Key, p.Value.PeerId)).ToArray());
            if (room.HostUserId is null)
            {
                var oldest = room.Players.OrderBy(p => p.Value.EnteredAt).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                bool ready = matchAlone || room.Players.Count >= 2;
                if (ready && userId == oldest)
                {
                    return ("create", new Dictionary<string, string>
                    {
                        ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                        ["users"] = users,
                    });
                }
                return ("matching", new Dictionary<string, string>
                {
                    ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                });
            }

            var payload = new Dictionary<string, string>
            {
                ["game_id"] = room.HostGameId ?? "",
                ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                ["users"] = users,
            };
            if (room.HostBlob is not null) payload["game_stats"] = room.HostBlob;
            if (room.HostUserId == userId) return ("create", payload);
            payload["game_stats"] = room.HostBlob ?? "";
            return ("join", payload);
        }
    }

    public (string State, Dictionary<string, string>? Payload) CreateGame(string eventId, string onlineType, string userId, string gameId, string blob)
    {
        lock (_matchLock)
        {
            if (!_rooms.TryGetValue((eventId, onlineType), out var room)) return ("nohost", null);
            if (room.Started || !room.Players.ContainsKey(userId)) return ("nohost", null);

            if (room.HostUserId is null)
            {
                room.HostUserId = userId;
                room.HostGameId = gameId;
                room.HostBlob = blob;
            }

            // The local driver is added by the game; only return remote network identities.
            var users = System.Text.Json.JsonSerializer.Serialize(room.Players.Where(p => p.Key != userId).OrderBy(p => p.Value.EnteredAt).Select(p => new MatchingUser(p.Value.OnlineId, p.Key, p.Value.PeerId)).ToArray());
            var payload = new Dictionary<string, string>
            {
                ["game_id"] = room.HostGameId ?? gameId,
                ["pregrid_size"] = room.Players.Count.ToString(CultureInfo.InvariantCulture),
                ["users"] = users,
            };
            if (room.HostBlob is not null) payload["game_stats"] = room.HostBlob;
            return (room.HostUserId == userId ? "create" : "join", payload);
        }
    }

    public bool StartGame(string eventId, string onlineType, string userId, string gameId)
    {
        lock (_matchLock)
        {
            if (!_rooms.TryGetValue((eventId, onlineType), out var room)) return false;
            if (room.Started || !room.Players.ContainsKey(userId) || room.HostUserId != userId) return false;
            if (string.IsNullOrEmpty(room.HostGameId) || !string.Equals(room.HostGameId, gameId, StringComparison.Ordinal)) return false;
            room.Started = true;
            return true;
        }
    }

    public void LeaveGame(string eventId, string onlineType, string userId)
    {
        var key = (eventId, onlineType);
        lock (_matchLock)
        {
            if (_activeRooms.TryGetValue(userId, out var active))
                RemovePlayerLocked(active, userId);
            else
                RemovePlayerLocked(key, userId);
        }
    }

    private void RemovePlayerLocked((string Event, string Type) key, string userId)
    {
        if (_rooms.TryGetValue(key, out var room) && room.Players.Remove(userId))
        {
            if (room.HostUserId == userId)
            {
                room.HostUserId = null;
                room.HostGameId = null;
                room.HostBlob = null;
                room.Started = false;
            }
            if (room.Players.Count == 0) _rooms.TryRemove(key, out _);
        }
        _activeRooms.TryRemove(userId, out _);
    }
}
