using System.Buffers.Binary;
using System.Text;

namespace Gt5pOnlineServer.Grim;

/// <summary>
/// PlayStation Network ticket parser (and forger, for offline testing).
///
/// GT5P login flow:
///     NPUtil::requestTicket()  -> raw NP ticket blob (binary, TLV)
///     Grim.login(ticket)       -> POST /ap/ticket/login/{pfsVersion}/ with that blob
///
/// Like every community Grim server, the ticket is NOT cryptographically
/// verified: on RPCS3 it comes from RPCN, on a real console from PSN - either
/// way only the identity fields matter for the game database.
///
/// Format reference: Nenkai/Grimoire GTGrimServer/Sony/NPTicket.cs
/// </summary>
public sealed record NpTicket
{
    public int VersionMajor { get; init; }
    public int VersionMinor { get; init; }
    public byte[] Signature { get; init; } = Array.Empty<byte>();
    public long IssuedDate { get; init; }
    public long ExpiryDate { get; init; }
    public long UserId { get; init; }
    public string OnlineId { get; init; } = "";
    public string Region { get; init; } = "";
    public int Language { get; init; }
    public string Domain { get; init; } = "";
    public byte[] ServiceId { get; init; } = Array.Empty<byte>();

    public override string ToString()
        => $"NPTicket(v{VersionMajor}.{VersionMinor} online_id='{OnlineId}' region='{Region}' domain='{Domain}' user_id={UserId})";

    // ------------------------------------------------------------------ //

    public static NpTicket FromBuffer(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 0x10) throw new InvalidDataException("ticket too short");

        var t = new NpTicket
        {
            VersionMajor = buf[0] >> 4,
            VersionMinor = buf[1],
        };

        // Header layout differs by ticket version (see Grimoire's NPTicket.cs)
        var off = t.VersionMajor >= 4 ? 0x10 : 0x0C;

        var signature = ReadNext(buf, ref off);
        _ = ReadNext(buf, ref off);                    // unknown field
        var issued = ReadNext(buf, ref off);
        var expiry = ReadNext(buf, ref off);
        var userId = ReadNext(buf, ref off);
        var onlineId = ReadNext(buf, ref off);
        var locale = ReadNext(buf, ref off);
        var domain = ReadNext(buf, ref off);
        var serviceId = ReadNext(buf, ref off);

        var sigBytes = signature as byte[];
        var region = "";
        var language = 0;
        if (locale is byte[] loc)
        {
            region = loc.Length >= 2 ? Encoding.UTF8.GetString(loc, 0, 2) : "";
            language = loc.Length >= 4 ? BinaryPrimitives.ReadUInt16BigEndian(loc.AsSpan(2, 2)) : 0;
        }

        return t with
        {
            Signature = sigBytes ?? Array.Empty<byte>(),
            IssuedDate = Convert.ToInt64(issued),
            ExpiryDate = Convert.ToInt64(expiry),
            UserId = Convert.ToInt64(userId),
            OnlineId = onlineId?.ToString() ?? "",
            Region = region,
            Language = language,
            Domain = domain?.ToString() ?? "",
            ServiceId = serviceId as byte[] ?? Array.Empty<byte>(),
        };
    }

    /// <summary>Read one TLV field -&gt; boxed value (byte[]/long/string).
    /// Field codes are Nenkai/Grimoire's (GT5/GT6 logins prove them against
    /// real tickets, incl. GT5P v2.1): Empty=0, UInt32=1, UInt64=2, String=4,
    /// Timestamp=7, Binary=8.</summary>
    private static object? ReadNext(ReadOnlySpan<byte> buf, ref int off)
    {
        if (off + 4 > buf.Length) throw new InvalidDataException("truncated TLV");
        ushort ftype = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(off, 2));
        ushort dlen = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(off + 2, 2));
        off += 4;
        switch (ftype)
        {
            case 0: return null;                                          // Empty
            case 1:                                                       // UInt32
                if (off + 4 > buf.Length) throw new InvalidDataException("truncated TLV data");
                var u32 = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(off, 4));
                off += 4;
                return (long)u32;
            case 2:                                                       // UInt64
            case 7:                                                       // Timestamp (cell-epoch ms)
                if (off + 8 > buf.Length) throw new InvalidDataException("truncated TLV data");
                var u64 = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(off, 8));
                off += 8;
                return (long)u64;
            case 4:                                                       // String (NUL-terminated)
                if (off + dlen > buf.Length) throw new InvalidDataException("truncated TLV data");
                var data = buf.Slice(off, dlen).ToArray();
                off += dlen;
                var nul = Array.IndexOf(data, (byte)0);
                return Encoding.UTF8.GetString(data, 0, nul < 0 ? data.Length : nul);
            case 8:                                                       // Binary
                if (off + dlen > buf.Length) throw new InvalidDataException("truncated TLV data");
                var raw = buf.Slice(off, dlen).ToArray();
                off += dlen;
                return raw;
            default: return null;
        }
    }

    // ------------------------------------------------------------------ //

    /// <summary>Build a syntactically-valid ticket - OFFLINE TESTING ONLY
    /// (used by GT5P.TestClient to exercise the login path without a PS3).</summary>
    public static byte[] Forge(string onlineId = "GT5P_Player", string region = "jp",
        string domain = "ps3", long userId = 0x10000)
    {
        byte[] Tlv(ushort ftype, byte[] data)
        {
            var head = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(0, 2), ftype);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2, 2), (ushort)data.Length);
            return head.Concat(data).ToArray();
        }
        byte[] Str(string s) => Encoding.UTF8.GetBytes(s).Append((byte)0).ToArray();
        byte[] Ulong(long v)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            return b;
        }

        var body = new List<byte[]>();
        body.Add(Tlv(8, new byte[0x38]));            // signature (Binary)
        body.Add(Tlv(0, Array.Empty<byte>()));       // unknown field (Empty)
        body.Add(Tlv(7, Ulong(0x12345678)));         // issued date (Timestamp)
        body.Add(Tlv(7, Ulong(0x12345999)));         // expiry date (Timestamp)
        body.Add(Tlv(2, Ulong(userId)));             // user id (UInt64)
        body.Add(Tlv(4, Str(onlineId)));             // online id (String)
        var locale = Encoding.UTF8.GetBytes(region)
            .Concat(new byte[] { 0, 1 }).ToArray();   // region + language
        body.Add(Tlv(8, locale));                    // locale (Binary)
        body.Add(Tlv(4, Str(domain)));               // domain (String)
        body.Add(Tlv(8, Encoding.UTF8.GetBytes("GT5P-TEST")));  // service id (Binary)

        var bodyBytes = body.SelectMany(b => b).ToArray();

        // v2-style header (matches Grimoire's NPTicket.cs layout): 0x21, minor,
        // bodyLen+8 (u16), two u32 zeros -> 12 bytes total (0x0C)
        var headBuf = new byte[12];
        headBuf[0] = 0x21;
        headBuf[2] = (byte)((bodyBytes.Length + 8) >> 8);
        headBuf[3] = (byte)(bodyBytes.Length + 8);
        return headBuf.Concat(bodyBytes).ToArray();
    }
}
