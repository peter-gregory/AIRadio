using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Models.Tools;

public static class ToolReportFormatter
{
    private static readonly string[] PreferredOrder =
    [
        "Name", "Station", "Location", "Source", "Title", "Summary",
        "Date", "Time", "Type", "Content", "Status", "Condition",
        "Temperature", "FeelsLike", "High", "Low", "RainProbability",
        "WindSpeed", "WindDirection", "WindGusts", "Humidity",
        "Precipitation", "Connected", "Ssid", "SignalStrength", "Security",
        "Enabled"
    ];

    private static readonly HashSet<string> HiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Url", "PublishedAt", "RetrievedAt", "CreatedAt", "UpdatedAt",
        "Latitude", "Longitude", "Raw", "Action", "IsSecured",
        "StreamUrl", "Homepage", "Favicon", "SourceId", "CountryCode", "IsHttps",
        "Votes", "LastPlayed", "PlayCount", "Codec"
    };

    public static string Format(string toolName, object? data, string? fallback = null)
    {
        var body = data is null ? string.Empty : FormatToken(JToken.FromObject(data));

        if (string.IsNullOrWhiteSpace(body))
            body = fallback?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        return $"{FormatToolName(toolName)} REPORT\n\n{body.Trim()}";
    }

    private static string FormatToken(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Object => FormatObject((JObject)token),
            JTokenType.Array => FormatArray((JArray)token),
            JTokenType.Null => string.Empty,
            JTokenType.Boolean => token.Value<bool>() ? "Yes" : "No",
            JTokenType.String => token.Value<string>() ?? string.Empty,
            _ => token.ToString(Newtonsoft.Json.Formatting.None)
        };
    }

    private static string FormatObject(JObject value)
    {
        var properties = value.Properties()
            .Where(p => !HiddenProperties.Contains(p.Name))
            .OrderBy(p => PropertyOrder(p.Name))
            .ToList();

        var sections = new List<string>();

        foreach (var property in properties)
        {
            var formatted = FormatToken(property.Value);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            if (property.Value.Type == JTokenType.Array &&
                property.Value is JArray array &&
                array.Count > 0 &&
                array.All(x => x.Type == JTokenType.Object))
            {
                var items = array
                    .Select(FormatToken)
                    .Where(s => !string.IsNullOrWhiteSpace(s));

                sections.Add($"{Label(property.Name)}\n\n{string.Join("\n\n", items)}");
                continue;
            }

            sections.Add($"{Label(property.Name)}\n{formatted}");
        }

        return string.Join("\n\n", sections);
    }

    private static string FormatArray(JArray value)
    {
        return string.Join(
            "\n\n",
            value.Select(FormatToken).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static int PropertyOrder(string name)
    {
        var index = Array.FindIndex(
            PreferredOrder,
            x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

        return index < 0 ? PreferredOrder.Length + 100 : index;
    }

    private static string Label(string name)
    {
        return name switch
        {
            "Ssid" => "Network",
            "SignalStrength" => "Signal Strength",
            "WindSpeed" => "Wind",
            "WindGusts" => "Wind Gusts",
            "WindDirection" => "Wind Direction",
            "RainProbability" => "Chance of Rain",
            "FeelsLike" => "Feels Like",
            "DayOfWeek" => "Day",
            "TimeOfDay" => "Time",
            _ => SplitWords(name)
        };
    }

    private static string SplitWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var chars = new List<char>(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];

            if (i > 0 && char.IsUpper(current) && !char.IsUpper(value[i - 1]))
                chars.Add(' ');

            chars.Add(current);
        }

        return new string(chars.ToArray());
    }

    private static string FormatToolName(string name)
    {
        return SplitWords(name.Replace("-", " ", StringComparison.Ordinal)).Trim();
    }
}
