using System.Text.Json;
using System.Xml.Linq;
using Gt5pOnlineServer.Data;
using Gt5pOnlineServer.Services;

namespace Gt5pOnlineServer.Grim;

public static class RankingProtocol
{
    public static async Task<(string Xml, int Status)> ExecuteAsync(string command, Dictionary<string, string> parameters, string? onlineId, GrimStore store, ReplayStore replays)
    {
        if (command == "ranking.getListMultiBoard")
        {
            // Native serializer 0x8fab68 emits comma-separated board_id values.
            var ids = parameters.GetValueOrDefault("board_id", "").Split(',');
            if (ids.Length is < 1 or > 32 || ids.Any(id => !int.TryParse(id, out var parsed) || parsed <= 0))
                return (GrimXml.Decline("invalid board_id list"), 400);
            if (!int.TryParse(parameters.GetValueOrDefault("begin"), out var begin) ||
                !int.TryParse(parameters.GetValueOrDefault("end"), out var end) || begin < 1 || end < begin || (long)end - begin > 499)
                return (GrimXml.Decline("invalid ranking range"), 400);
            var combined = new List<RankingEntry>();
            foreach (var id in ids.Select(id => int.Parse(id).ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct())
                combined.AddRange((await store.GetNativeBoardAsync(id)).Skip(begin - 1).Take(end - begin + 1));
            return (Records(combined, replays), 200);
        }
        if (!int.TryParse(parameters.GetValueOrDefault("board_id"), out var boardNumber) || boardNumber <= 0)
            return (GrimXml.Decline("invalid board_id"), 400);
        var board = boardNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (command == "ranking.update")
        {
            if (onlineId is null) return (GrimXml.Decline("login required"), 401);
            if (!int.TryParse(parameters.GetValueOrDefault("score"), out var score) || score <= 0)
                return (GrimXml.Decline("invalid lap time"), 400);
            var stats = parameters.GetValueOrDefault("stats", "");
            try { if (Convert.FromBase64String(stats).Length < 24) throw new FormatException(); }
            catch (FormatException) { return (GrimXml.Decline("invalid ranking statistics"), 400); }
            var metadata = JsonSerializer.Serialize(new { gt5p = 1, stats, comment = parameters.GetValueOrDefault("comment", "") });
            if (metadata.Length > 256) return (GrimXml.Decline("ranking statistics exceed storage limit"), 400);
            await store.SaveNativeRankingAsync(board, onlineId, score, metadata);
            return (GrimXml.Envelope("1"), 200);
        }
        var rows = await store.GetNativeBoardAsync(board);
        if (command is "ranking.getCount" or "getCount") return (GrimXml.Envelope(rows.Count.ToString()), 200);
        var user = parameters.GetValueOrDefault("user_id", onlineId ?? "");
        if (command is "ranking.calc" or "ranking.calc2" or "calc2")
        {
            int index = rows.FindIndex(r => r.OnlineId == user);
            return (GrimXml.Envelope(index < 0 ? "-1" : (index + 1).ToString()), 200);
        }
        if (command == "ranking.calcTemporary")
        {
            if (!int.TryParse(parameters.GetValueOrDefault("score"), out var score) || score <= 0)
                return (GrimXml.Decline("invalid lap time"), 400);
            return (GrimXml.Envelope((rows.Count(r => r.TimeMs < score) + 1).ToString()), 200);
        }
        if (command == "ranking.get")
            return (Records(rows.Where(r => r.OnlineId == user).Take(1), replays), 200);
        if (command == "ranking.getList")
        {
            if (!int.TryParse(parameters.GetValueOrDefault("begin"), out int begin) ||
                !int.TryParse(parameters.GetValueOrDefault("end"), out int end) || begin < 1 || end < begin || (long)end - begin > 499)
                return (GrimXml.Decline("invalid ranking range"), 400);
            return (Records(rows.Skip(begin - 1).Take(end - begin + 1), replays), 200);
        }
        return (GrimXml.Decline($"unimplemented ranking command: {command}"), 501);
    }

    private static string Records(IEnumerable<RankingEntry> rows, ReplayStore replays)
    {
        var root = new XElement("grim", new XElement("result", 1));
        foreach (var row in rows)
        {
            using var metadata = JsonDocument.Parse(row.Meta);
            root.Add(new XElement("ranking", new XAttribute("board_id", row.BoardId),
                new XAttribute("user_id", row.OnlineId), new XAttribute("score", row.TimeMs),
                new XAttribute("create_time", row.Date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new XAttribute("comment", metadata.RootElement.GetProperty("comment").GetString() ?? ""),
                new XAttribute("stats", metadata.RootElement.GetProperty("stats").GetString()!),
                new XAttribute("replay_id", replays.Find(row) is null ? 0 : row.Id)));
        }
        return root.ToString();
    }
}
