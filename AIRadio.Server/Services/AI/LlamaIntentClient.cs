using AIRadio.Server.Models.LLama;
using AIRadio.Server.Services.Radio;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.AI
{
    public interface ILlamaIntentClient : IDisposable
    {
        bool IsInitialized { get; }

        Task InitializeAsync(
            CancellationToken cancellationToken = default);

        Task<IntentResult> DetermineIntentAsync(
            string userText,
            CancellationToken cancellationToken = default);

        Task<BargeInResult> CheckBargeInAsync(
            string userText,
            CancellationToken cancellationToken = default);
    }

    public sealed class LlamaIntentClient : ILlamaIntentClient
    {
        private const string PromptFileName =
            "intent-system.txt";

        private readonly IConfiguration _configuration;
        private readonly ILlamaHttpClient _httpClient;
        private readonly ILogger<LlamaIntentClient> _logger;

        private readonly SemaphoreSlim _requestLock =
            new(1, 1);

        private string? _systemPrompt;

        private bool _initialized;
        private bool _disposed;

        public LlamaIntentClient(
            IConfiguration configuration,
            ILlamaHttpClient httpClient,
            ILogger<LlamaIntentClient> logger)
        {
            _configuration = configuration;
            _httpClient = httpClient;
            _logger = logger;
        }

        public bool IsInitialized =>
            _initialized;

        private string PromptsDirectory =>
            _configuration[
                "Application:PromptsDirectory"]
            ?? throw new InvalidOperationException(
                "Application:PromptsDirectory is not configured.");

        private string SystemPromptPath =>
            Path.Combine(
                PromptsDirectory,
                PromptFileName);

        public async Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_initialized)
                return;

            await _httpClient.InitializeAsync(
                cancellationToken);

            await LoadSystemPromptAsync(
                cancellationToken);

            _initialized = true;

            _logger.LogInformation(
                "Llama intent client initialized.");
        }

        private async Task LoadSystemPromptAsync(
            CancellationToken cancellationToken)
        {
            var path =
                SystemPromptPath;

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "The Llama intent system prompt was not found.",
                    path);
            }

            var prompt =
                await File.ReadAllTextAsync(
                    path,
                    cancellationToken);

            prompt =
                prompt.Trim();

            if (prompt.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The Llama intent system prompt is empty: {path}");
            }

            _systemPrompt =
                prompt;

            _logger.LogInformation(
                "Loaded Llama intent system prompt from {Path}.",
                path);
        }

        private string GetSystemPrompt()
        {
            return _systemPrompt
                ?? throw new InvalidOperationException(
                    "LlamaIntentClient has not been initialized.");
        }

        public async Task<IntentResult> DetermineIntentAsync(
            string userText,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ArgumentException.ThrowIfNullOrWhiteSpace(
                userText);

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                EnsureInitialized();

                var request =
                    new LlamaCompletionRequest
                    {
                        Messages =
                        [
                            LlamaMessage.System(
                            GetSystemPrompt()),

                        LlamaMessage.User(
                            userText)
                        ],

                        Temperature = 0.0,
                        MaxTokens = 512,
                        Stream = false
                    };

                var response =
                    await _httpClient.CompleteAsync(
                        request,
                        cancellationToken);

                if (response is null)
                {
                    throw new InvalidOperationException(
                        "Llama returned no intent response.");
                }

                var content =
                    response.Content?.Trim();

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException(
                        "Llama returned an empty intent response.");
                }

                _logger.LogDebug(
                    "Llama intent response: {Response}",
                    content);

                return ParseIntentResult(
                    content);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public async Task<BargeInResult> CheckBargeInAsync(
            string userText,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ArgumentException.ThrowIfNullOrWhiteSpace(
                userText);

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                EnsureInitialized();

                var request =
                    new LlamaCompletionRequest
                    {
                        Messages =
                        [
                            LlamaMessage.System(
                            GetSystemPrompt()),

                        LlamaMessage.User(
                            userText)
                        ],

                        Temperature = 0.0,
                        MaxTokens = 256,
                        Stream = false
                    };

                var response =
                    await _httpClient.CompleteAsync(
                        request,
                        cancellationToken);

                if (response is null)
                {
                    return new BargeInResult
                    {
                        IsBargeIn = false
                    };
                }

                var content =
                    response.Content?.Trim();

                if (string.IsNullOrWhiteSpace(content))
                {
                    return new BargeInResult
                    {
                        IsBargeIn = false
                    };
                }

                return ParseBargeInResult(
                    content);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private static IntentResult ParseIntentResult(
            string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new IntentResult
                {
                    Intent = "Unknown",
                    RequiresConversation = false,
                    RequiresTool = false,
                    ToolName = null,
                    Parameters = new(),
                    MissingParameters = [],
                    UserPrompt = null,
                    SystemPrompt = null,
                    UserText = null,
                    Response = null
                };
            }

            try
            {
                var result =
                    LlamaJsonOptions.Deserialize<IntentResult>(
                        content);

                if (result is null)
                {
                    return CreateUnknownResult();
                }

                /*
                 * Normalize collections in case the model returned
                 * null values despite the expected JSON structure.
                 */
                result.Parameters ??=
                    new Dictionary<string, object?>();

                result.MissingParameters ??=
                    [];

                /*
                 * Normalize the intent so downstream code always
                 * has a usable value.
                 */
                if (string.IsNullOrWhiteSpace(
                        result.Intent))
                {
                    result.Intent =
                        "Unknown";
                }

                /*
                 * A tool cannot be required without a tool name.
                 */
                if (result.RequiresTool &&
                    string.IsNullOrWhiteSpace(
                        result.ToolName))
                {
                    result.RequiresTool =
                        false;

                    result.Intent =
                        "Unknown";
                }

                /*
                 * A missing parameter means the request cannot yet
                 * be executed. Conversation handling may be required
                 * to ask the user for the missing information.
                 */
                if (result.MissingParameters.Count > 0)
                {
                    result.RequiresTool =
                        false;

                    result.RequiresConversation =
                        true;
                }

                return result;
            }
            catch (Exception ex)
                when (ex is Newtonsoft.Json.JsonException ||
                      ex is ArgumentException)
            {
                return CreateUnknownResult();
            }
        }

        private static IntentResult CreateUnknownResult()
        {
            return new IntentResult
            {
                Intent = "Unknown",

                Parameters =
                    new Dictionary<string, object?>(),

                RequiresConversation =
                    false,

                RequiresTool =
                    false,

                ToolName =
                    null,

                MissingParameters =
                    [],

                SystemPrompt =
                    null,

                UserPrompt =
                    null,

                UserText =
                    null,

                Response =
                    null
            };
        }

        private static BargeInResult ParseBargeInResult(
            string content)
        {
            try
            {
                var result =
                    JsonConvert.DeserializeObject<BargeInResult>(
                        content);

                if (result is not null)
                    return result;
            }
            catch
            {
                // Fall through to conservative fallback.
            }

            return new BargeInResult
            {
                IsBargeIn = false
            };
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "LlamaIntentClient has not been initialized.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _requestLock.Dispose();

            // _httpClient is owned by DI.
        }
    }

    public sealed class IntentResult
    {
        [JsonProperty("intent")]
        public string Intent { get; set; } =
            "Unknown";

        [JsonProperty("parameters")]
        public Dictionary<string, object?> Parameters { get; set; } =
            new();

        [JsonProperty("requiresConversation")]
        public bool RequiresConversation { get; set; }

        [JsonProperty("requiresTool")]
        public bool RequiresTool { get; set; }

        [JsonProperty("toolName")]
        public string? ToolName { get; set; }

        [JsonProperty("missingParameters")]
        public List<string> MissingParameters { get; set; } =
            [];

        [JsonProperty("systemPrompt")]
        public string? SystemPrompt { get; set; }

        [JsonProperty("userPrompt")]
        public string? UserPrompt { get; set; }

        [JsonProperty("userText")]
        public string? UserText { get; set; }

        [JsonProperty("response")]
        public string? Response { get; set; }

        public bool HasMissingParameters =>
            MissingParameters.Count > 0;

        public bool IsConversation =>
            string.Equals(
                Intent,
                "Conversation",
                StringComparison.OrdinalIgnoreCase);

        public bool IsUnknown =>
            string.Equals(
                Intent,
                "Unknown",
                StringComparison.OrdinalIgnoreCase);
    }
}
