using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>Kinds of organization, their fields and what their people are called (as the JavaScript page).</summary>
public static class OrgKinds
{
    public sealed record Kind(string Key, string Name, Market.Field[] Fields, string People);
    static Market.Field F(string k, string l, string t = "", string[]? c = null) => new(k, l, t, c);
    public static readonly Kind[] All =
    [
        new("corporation", "Corporation", [F("registration", "Registration number"), F("founded", "Incorporated (date)", "date"), F("incorporatedIn", "Incorporated in"), F("shares", "Shares issued", "number"), F("website", "Website")], "Officers and shareholders"),
        new("llc", "LLC / limited company", [F("registration", "Registration number"), F("founded", "Formed (date)", "date"), F("website", "Website")], "Members and managers"),
        new("partnership", "Partnership", [F("form", "Form", "", ["general", "limited", "limited liability"]), F("registration", "Registration number"), F("founded", "Founded (date)", "date"), F("website", "Website")], "Partners"),
        new("sole", "Sole proprietorship", [F("registration", "Business registration"), F("website", "Website")], "Owner"),
        new("nonprofit", "Nonprofit / charity", [F("taxId", "Charity / tax-exempt number"), F("founded", "Founded (date)", "date"), F("mission", "Mission"), F("website", "Website")], "Board and officers"),
        new("cooperative", "Cooperative", [F("registration", "Registration number"), F("members", "Members (count)", "number"), F("website", "Website")], "Board"),
        new("government", "Government body", [F("level", "Level", "", ["national", "state / province", "county", "city", "district", "agency"]), F("website", "Website")], "Officials"),
        new("club", "Club / association", [F("founded", "Founded (date)", "date"), F("members", "Members (count)", "number"), F("website", "Website")], "Officers"),
        new("arbitration", "Arbitration body", [F("rules", "Rules it follows"), F("website", "Website")], "Arbitrators and staff"),
        new("other", "Other", [F("website", "Website")], "People"),
    ];
    public static Kind Of(string? key) => All.FirstOrDefault(k => k.Key == key) ?? All[^1];
    /// <summary>The details object from typed-in text (numbers and dates converted), empty fields left out.</summary>
    public static JsonObject Details(string orgType, Dictionary<string, string> values)
    {
        var o = new JsonObject();
        foreach (var f in Of(orgType).Fields)
        {
            if (!values.TryGetValue(f.Key, out var v) || v.Trim().Length == 0) continue;
            o[f.Key] = f.Type switch
            {
                "number" => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
                "date" => DateTimeOffset.TryParse(v, out var d) ? d.ToUnixTimeMilliseconds() : 0,
                _ => v.Trim(),
            };
        }
        return o;
    }
    /// <summary>A details value as the form shows it (dates as yyyy-MM-dd).</summary>
    public static string Show(Market.Field f, JsonNode? v) => v == null ? "" : f.Type == "date" && Js.Num(v) is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("yyyy-MM-dd") : v.ToString();
}
