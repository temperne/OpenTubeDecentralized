using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTube.Core;

/// <summary>
/// JavaScript's semantics over System.Text.Json nodes, so ported rules decide exactly as the JavaScript core does:
/// "undefined" (a missing key) is not "null", typeof checks are strict, numbers print as JavaScript prints them,
/// String.prototype.trim uses JavaScript's whitespace, and regular expressions use ECMAScript rules.
/// </summary>
public static partial class Js
{
    public static bool Has(JsonObject o, string k) => o.ContainsKey(k);
    /// <summary>x === undefined: the key is absent (JSON has no undefined; null is a value).</summary>
    public static bool Undef(JsonObject? o, string k) => o == null || !o.ContainsKey(k);
    public static JsonNode? Get(JsonNode? n, string k) => n is JsonObject o && o.TryGetPropertyValue(k, out var v) ? v : null;

    public static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;
    public static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
    public static bool IsBool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
    public static bool IsTrue(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;
    public static bool IsNull(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && v == null;
    /// <summary>!!x && typeof x === 'object' (arrays count).</summary>
    public static bool IsObjectLike(JsonNode? n) => n is JsonObject or JsonArray;
    public static string? Str(JsonNode? n) => IsString(n) ? n!.GetValue<string>() : null;
    public static double? Num(JsonNode? n) => IsNumber(n) ? NumOf((JsonValue)n!) : null;
    /// <summary>A JSON number as a double, whatever .NET type it was created from (parsed JSON, int, long, double…).</summary>
    public static double NumOf(JsonValue v) =>
        v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<decimal>(out var m) ? (double)m : v.TryGetValue<float>(out var f) ? f : double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>JavaScript truthiness of a JSON value.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonObject or JsonArray => true,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => NumOf(v) is var d && d != 0 && !double.IsNaN(d),
            _ => false,
        },
        _ => false,
    };

    // ---- the core's primitives
    public static bool IsStr(JsonNode? v, int max) => Str(v) is { } s && s.Length <= max;
    public static bool IsHex(JsonNode? v, int len) => Str(v) is { } s && s.Length == len && HexRe().IsMatch(s);
    public static bool IsKey(JsonNode? v) => Str(v) is { } s && KeyRe().IsMatch(s);
    public static bool IsImg(JsonNode? v) => Str(v) is { } s && (s == "" || (s.Length <= Limits.Image && ImgRe().IsMatch(s)));
    public static bool IsSafeInteger(JsonNode? v) => Num(v) is double d && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) <= 9007199254740991;
    public static bool IsFinite(JsonNode? v) => Num(v) is double d && double.IsFinite(d);
    /// <summary>x === n for a number.</summary>
    public static bool NumIs(JsonNode? v, double n) => Num(v) == n;

    /// <summary>String.prototype.trim: JavaScript's whitespace (which includes U+FEFF and not U+0085, unlike .NET's).</summary>
    public static string Trim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsJsSpace(s[a])) a++;
        while (b > a && IsJsSpace(s[b - 1])) b--;
        return s[a..b];
    }
    static bool IsJsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' || (int)c is 0xa0 or 0x1680 or 0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 or 0xfeff or (>= 0x2000 and <= 0x200a);

    /// <summary>Number.prototype.toString (and JSON.stringify of a number): shortest round-trip digits, JavaScript's layout.</summary>
    public static string NumberToString(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";   // JSON.stringify writes null for these
        if (d == 0) return "0";
        // shortest round-trip digits and the decimal exponent, then JavaScript's rules for fixed vs exponential
        var shortest = d.ToString("R", CultureInfo.InvariantCulture);
        var neg = d < 0;
        var e = shortest.ToUpperInvariant().Split('E');
        string digits; int exp;   // value = 0.digits × 10^exp
        {
            var mant = e[0].TrimStart('-'); var e10 = e.Length > 1 ? int.Parse(e[1], CultureInfo.InvariantCulture) : 0;
            var dot = mant.IndexOf('.'); var intPart = dot < 0 ? mant : mant[..dot]; var frac = dot < 0 ? "" : mant[(dot + 1)..];
            var all = (intPart + frac).TrimStart('0'); var lead = (intPart + frac).Length - all.Length;
            digits = all.TrimEnd('0'); exp = intPart.Length + e10 - lead;
        }
        int k = digits.Length, n = exp;
        string s;
        if (k <= n && n <= 21) s = digits + new string('0', n - k);
        else if (0 < n && n <= 21) s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
        else
        {
            var e1 = n - 1; var sign = e1 < 0 ? "-" : "+";
            s = (k == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + sign + Math.Abs(e1).ToString(CultureInfo.InvariantCulture);
        }
        return neg ? "-" + s : s;
    }

    [GeneratedRegex("^[0-9a-f]+$")] private static partial Regex HexRe();
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")] private static partial Regex KeyRe();
    [GeneratedRegex("^data:image/(jpeg|png|webp);base64,[A-Za-z0-9+/=]+$")] private static partial Regex ImgRe();
}

/// <summary>The core's LIMITS.</summary>
public static class Limits
{
    public const int BlockBytes = 300000, Name = 60, About = 2000, Title = 140, Text = 5000, Comment = 1000, Image = 60000, Parents = 8, BatchBytes = 4000000, Orphans = 5000;
}
