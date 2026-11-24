namespace OpenTube.Client;

/// <summary>Reading a Lightning invoice (BOLT11): its payment hash (what locks the coins), amount, description and expiry.</summary>
public static class Bolt11
{
    public sealed record Invoice(string Hash, double? Sats, string Desc, string Network, DateTimeOffset Expires);
    const string Ch = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    public static Invoice Decode(string inv)
    {
        var s = inv.Trim().ToLowerInvariant(); if (s.StartsWith("lightning:")) s = s[10..];
        var sep = s.LastIndexOf('1');
        if (!s.StartsWith("ln") || sep < 4) throw new ArgumentException("not a Lightning invoice");
        var words = s[(sep + 1)..].Select(c => { var v = Ch.IndexOf(c); if (v < 0) throw new ArgumentException("not a Lightning invoice"); return v; }).ToList();
        if (words.Count < 7 + 6) throw new ArgumentException("not a Lightning invoice");
        words = words.Take(words.Count - 6).ToList();
        var m = System.Text.RegularExpressions.Regex.Match(s[..sep], "^ln(bc|tb|bcrt|tbs)(\\d+)?([munp])?$");
        double? sats = null;
        if (m.Success && m.Groups[2].Success) sats = double.Parse(m.Groups[2].Value) * (m.Groups[3].Value switch { "m" => 1e5, "u" => 100, "n" => 0.1, "p" => 1e-4, _ => 1e8 });
        static List<byte> Bytes(IEnumerable<int> ws) { int acc = 0, bits = 0; var o = new List<byte>(); foreach (var w in ws) { acc = (acc << 5) | w; bits += 5; while (bits >= 8) { bits -= 8; o.Add((byte)((acc >> bits) & 255)); } acc &= (1 << bits) - 1; } return o; }
        long ts = 0; for (int k = 0; k < 7; k++) ts = ts * 32 + words[k];
        int i = 7; string? hash = null; var desc = ""; long expiry = 3600;
        while (i + 3 <= words.Count)
        {
            int type = words[i], len = words[i + 1] * 32 + words[i + 2]; var data = words.Skip(i + 3).Take(len).ToList(); i += 3 + len;
            if (type == 1 && len == 52) hash = Convert.ToHexStringLower(Bytes(data).Take(32).ToArray());
            if (type == 13) desc = System.Text.Encoding.UTF8.GetString(Bytes(data).ToArray());
            if (type == 6) expiry = data.Aggregate(0L, (a, w) => a * 32 + w);
        }
        if (hash == null) throw new ArgumentException("the invoice has no payment hash");
        return new(hash, sats, desc, m.Success ? m.Groups[1].Value : "?", DateTimeOffset.FromUnixTimeSeconds(ts + expiry));
    }
}
