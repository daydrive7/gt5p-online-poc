using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Gt5pOnlineServer.Grim;

// Port of grim_http.py GrimServer.grim_envelope / parse_grim_envelope / xml_decline / xml_maintenance.
public static class GrimXml
{
    public static string Envelope(string result = "1", IDictionary<string, string>? fields = null)
    {
        var root = new XElement("grim", new XElement("result", result));
        if (fields != null)
            foreach (var (k, v) in fields)
            {
                // Native matching start-element callback 0x79bd30 builds the
                // driver vector from user attributes (not user_id text nodes).
                if (k == "users")
                    foreach (var user in JsonSerializer.Deserialize<MatchingUser[]>(v) ?? [])
                        root.Add(new XElement("user",
                            new XAttribute("user_id", user.OnlineId),
                            new XAttribute("user_no", user.UserNumber),
                            new XAttribute("sce_np_id", user.PeerId)));
                else
                    root.Add(new XElement(k, v));
            }
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root + "\n";
    }

    public static (string Command, Dictionary<string, string> Params) ParseEnvelope(byte[] body)
    {
        try
        {
            var text = Encoding.UTF8.GetString(body);
            var root = XElement.Parse(text);
            if (root.Name.LocalName != "grim") return ("", new());
            var cmd = root.Element("command")?.Value.Trim() ?? "";
            var ps = new Dictionary<string, string>();
            var paramsEl = root.Element("params");
            if (paramsEl != null)
                foreach (var p in paramsEl.Elements("param"))
                    ps[p.Attribute("key")?.Value ?? ""] = (p.Value ?? "").Trim();
            return (cmd, ps);
        }
        catch
        {
            return ("", new());
        }
    }

    public static string Decline(string msg)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
         + $"<grim><result>0</result><message>{Escape(msg)}</message></grim>\n";

    public static string Maintenance(string msg)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
         + $"<grim><result>0</result><maintenance>1</maintenance><message>{Escape(msg)}</message></grim>\n";

    public static string TicketResult(string nickname, string userId, long userNumber, string serverTime)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
         + "<TicketResult>\n"
         + "  <result>1</result>\n"
         + $"  <Nickname>{Escape(nickname)}</Nickname>\n"
         + $"  <UserId>{Escape(userId)}</UserId>\n"
         + $"  <UserNumber>{userNumber}</UserNumber>\n"
         // Native ticket parser 0x793e18 uses lowercase identity fields.
         + $"  <user_id>{Escape(userId)}</user_id>\n"
         + $"  <user_no>{userNumber}</user_no>\n"
         + $"  <ServerTime>{Escape(serverTime)}</ServerTime>\n"
         + "</TicketResult>\n";

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
