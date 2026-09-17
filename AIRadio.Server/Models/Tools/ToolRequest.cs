using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Models.Tools
{
    public sealed class ToolRequest
    {
        public const string RequiredValue = "!required!";

        [JsonProperty("name")]
        public string Name { get; init; } = string.Empty;

        [JsonProperty("arguments")]
        public JObject Arguments { get; init; } = new();

        public T? GetArgument<T>(string name)
        {
            var token = Arguments[name];
            if (token is null)
                return default;
            return token.ToObject<T>();
        }

        public string? GetString(string name) => GetArgument<string>(name);

        public int? GetInt32(string name) => GetArgument<int?>(name);

        public bool? GetBoolean(string name) => GetArgument<bool?>(name);

        public bool HasArgument(string name) =>
            Arguments.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out _);

        public bool HasMissingRequiredArguments =>
            Arguments.Properties().Any(property =>
                string.Equals(property.Value.Value<string>(), RequiredValue, StringComparison.Ordinal));

        public IReadOnlyList<string> MissingRequiredArguments =>
            Arguments.Properties()
                .Where(property => string.Equals(property.Value.Value<string>(), RequiredValue, StringComparison.Ordinal))
                .Select(property => property.Name)
                .ToArray();

        public override string ToString() => JsonConvert.SerializeObject(this);
    }
}
