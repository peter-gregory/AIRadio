using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Models.Tools
{
    public sealed class ToolRequest
    {
        [JsonProperty("name")]
        public string Name { get; init; } = string.Empty;

        [JsonProperty("arguments")]
        public JObject Arguments { get; init; } = new();

        public T? GetArgument<T>(
            string name)
        {
            var token =
                Arguments[name];

            if (token is null)
                return default;

            return token.ToObject<T>();
        }

        public string? GetString(
            string name)
        {
            return GetArgument<string>(name);
        }

        public int? GetInt32(
            string name)
        {
            return GetArgument<int?>(name);
        }

        public bool? GetBoolean(
            string name)
        {
            return GetArgument<bool?>(name);
        }

        public bool HasArgument(
            string name)
        {
            return Arguments.TryGetValue(
                name,
                StringComparison.OrdinalIgnoreCase,
                out _);
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(
                this);
        }
    }
}
