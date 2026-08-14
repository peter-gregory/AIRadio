using AIRadio.Server.Models.LLama;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net;

namespace AIRadio.Server.Services.AI
{
    public interface ILlamaHttpClient
    {
        bool IsInitialized { get; }

        Task InitializeAsync(
            CancellationToken cancellationToken = default);

        Task<LlamaCompletionResponse> CompleteAsync(
            LlamaCompletionRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class LlamaHttpClient : ILlamaHttpClient
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<LlamaHttpClient> _logger;
        private readonly HttpClient _httpClient;

        private string? _endpoint;

        private bool _initialized;
        private bool _disposed;

        public LlamaHttpClient(
            IConfiguration configuration,
            ILogger<LlamaHttpClient> logger,
            HttpClient httpClient)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClient = httpClient;
        }

        public bool IsInitialized =>
            _initialized;

        // ============================================================
        // INITIALIZATION
        // ============================================================

        public Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_initialized)
                return Task.CompletedTask;

            var endpoint =
                _configuration[
                    "Llama:Endpoint"];

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException(
                    "Llama:Endpoint is not configured.");
            }

            _endpoint =
                endpoint.TrimEnd('/');

            _initialized = true;

            _logger.LogInformation(
                "Llama HTTP client initialized with endpoint {Endpoint}.",
                _endpoint);

            return Task.CompletedTask;
        }

        // ============================================================
        // COMPLETE
        // ============================================================

        public async Task<LlamaCompletionResponse> CompleteAsync(
            LlamaCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ArgumentNullException.ThrowIfNull(request);

            EnsureInitialized();

            var endpoint =
                _endpoint!;

            var json =
                LlamaJsonOptions.Serialize(
                    request);

            using var content =
                new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json");

            _logger.LogDebug(
                "Sending Llama completion request to {Endpoint}.",
                endpoint);

            using var response =
                await _httpClient.PostAsync(
                    endpoint,
                    content,
                    cancellationToken);

            var responseJson =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Llama request failed with HTTP {StatusCode}: {Response}",
                    (int)response.StatusCode,
                    responseJson);

                throw new HttpRequestException(
                    $"Llama request failed with HTTP " +
                    $"{(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}. " +
                    $"{responseJson}");
            }

            if (string.IsNullOrWhiteSpace(
                    responseJson))
            {
                throw new InvalidOperationException(
                    "Llama returned an empty HTTP response.");
            }

            LlamaCompletionResponse? result;

            try
            {
                result =
                    LlamaJsonOptions.Deserialize<
                        LlamaCompletionResponse>(
                            responseJson);
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to deserialize Llama completion response: {Response}",
                    responseJson);

                throw new InvalidOperationException(
                    "Llama returned an invalid completion response.",
                    ex);
            }

            if (result is null)
            {
                throw new InvalidOperationException(
                    "Llama returned a null completion response.");
            }

            _logger.LogDebug(
                "Llama completion completed successfully.");

            return result;
        }

        // ============================================================
        // VALIDATION
        // ============================================================

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "LlamaHttpClient has not been initialized.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
        }

        // ============================================================
        // DISPOSAL
        // ============================================================

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            /*
             * HttpClient is supplied by dependency injection.
             * Do not dispose it here.
             */
        }
    }

    public sealed class LlamaCompletionRequest
    {
        [JsonProperty("messages")]
        public List<LlamaMessage> Messages { get; set; } = [];

        [JsonProperty("temperature")]
        public double? Temperature { get; set; }

        [JsonProperty("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonProperty("stream")]
        public bool Stream { get; set; }

        [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)]
        public List<LlamaToolDefinition>? Tools { get; set; }

        [JsonProperty("tool_choice", NullValueHandling = NullValueHandling.Ignore)]
        public object? ToolChoice { get; set; }
    }

    public sealed class LlamaToolDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "function";

        [JsonProperty("function")]
        public LlamaFunctionDefinition Function { get; set; } = new();
    }

    public sealed class LlamaFunctionDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("parameters")]
        public LlamaToolParameters Parameters { get; set; } = new();
    }

    public sealed class LlamaToolParameters
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "object";

        [JsonProperty("properties")]
        public Dictionary<string, LlamaToolParameter> Properties { get; set; } = new();

        [JsonProperty("required")]
        public List<string> Required { get; set; } = [];
    }

    public sealed class LlamaToolParameter
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "string";

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("enum", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? Enum { get; set; }
    }

    public sealed class LlamaCompletionResponse
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("object")]
        public string? Object { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("model")]
        public string? Model { get; set; }

        [JsonProperty("choices")]
        public List<LlamaCompletionChoice> Choices { get; set; } = [];

        [JsonProperty("usage")]
        public LlamaUsage? Usage { get; set; }

        [JsonIgnore]
        public string? Content =>
            Choices.FirstOrDefault()?
                .Message?
                .Content;

        [JsonIgnore]
        public string? FinishReason =>
            Choices.FirstOrDefault()?
                .FinishReason;
    }

    public sealed class LlamaCompletionChoice
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("message")]
        public LlamaCompletionMessage? Message { get; set; }

        [JsonProperty("finish_reason")]
        public string? FinishReason { get; set; }
    }

    public sealed class LlamaCompletionMessage
    {
        [JsonProperty("role")]
        public string? Role { get; set; }

        [JsonProperty("content")]
        public string? Content { get; set; }

        [JsonProperty("tool_calls")]
        public List<LlamaToolCall> ToolCalls { get; set; } = [];
    }

    public sealed class LlamaToolCall
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("function")]
        public LlamaFunctionCall? Function { get; set; }
    }

    public sealed class LlamaFunctionCall
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("arguments")]
        public string? Arguments { get; set; }
    }

    public sealed class LlamaUsage
    {
        [JsonProperty("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonProperty("total_tokens")]
        public int TotalTokens { get; set; }
    }

    public sealed class LlamaErrorResponse
    {
        [JsonProperty("error")]
        public LlamaError? Error { get; init; }
    }

    public sealed class LlamaError
    {
        [JsonProperty("message")]
        public string? Message { get; init; }

        [JsonProperty("type")]
        public string? Type { get; init; }

        [JsonProperty("code")]
        public string? Code { get; init; }
    }

    public static class LlamaJsonOptions
    {
        public static readonly JsonSerializerSettings Settings =
            new()
            {
                ContractResolver =
                    new CamelCasePropertyNamesContractResolver(),

                NullValueHandling =
                    NullValueHandling.Ignore,

                MissingMemberHandling =
                    MissingMemberHandling.Ignore,

                DefaultValueHandling =
                    DefaultValueHandling.Include,

                Formatting =
                    Formatting.None
            };

        public static string Serialize(
            object value)
        {
            ArgumentNullException.ThrowIfNull(
                value);

            return JsonConvert.SerializeObject(
                value,
                Settings);
        }

        public static T? Deserialize<T>(
            string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                json);

            return JsonConvert.DeserializeObject<T>(
                json,
                Settings);
        }
    }
}
