using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Sounds;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.AI
{
    public interface IConversationLlamaClient : IDisposable
    {
        bool IsInitialized { get; }
        bool IsBusy { get; }

        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<LlamaResponse> StartConversationAsync(string userMessage, CancellationToken cancellationToken = default);
        Task<LlamaResponse> ContinueAsync(string userMessage, CancellationToken cancellationToken = default);
        Task<LlamaResponse> ContinueAsync(IEnumerable<ToolResult> toolResults, CancellationToken cancellationToken = default);
        Task CancelAsync();
        Task ResetAsync(CancellationToken cancellationToken = default);
        IReadOnlyList<LlamaMessage> GetHistory();
    }

    public sealed class ConversationLlamaClient : IConversationLlamaClient
    {
        private readonly ILogger<ConversationLlamaClient> _logger;
        private readonly ILlamaHttpClient _llama;
        private readonly IConfiguration _configuration;
        private readonly ISoundEffectManager _soundEffectManager;
        private readonly List<LlamaMessage> _history = [];
        private readonly string _systemPrompt;
        private readonly SemaphoreSlim _requestLock = new(1, 1);

        private CancellationTokenSource? _requestCancellation;
        private bool _isInitialized;
        private bool _disposed;

        public ConversationLlamaClient(
            ILogger<ConversationLlamaClient> logger,
            ILlamaHttpClient llama,
            IConfiguration configuration,
            ISoundEffectManager soundEffectManager)
        {
            _logger = logger;
            _llama = llama;
            _configuration = configuration;
            _soundEffectManager = soundEffectManager;
            _systemPrompt = LoadSystemPrompt();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsBusy => _requestCancellation is not null;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized)
                    return;

                await _llama.InitializeAsync(cancellationToken);
                ResetHistoryInternal();
                _logger.LogInformation("Conversation Llama client initialized.");
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public Task<LlamaResponse> StartConversationAsync(
            string userMessage,
            CancellationToken cancellationToken = default) =>
            ExecuteConversationAsync(userMessage, true, cancellationToken);

        public Task<LlamaResponse> ContinueAsync(
            string userMessage,
            CancellationToken cancellationToken = default) =>
            ExecuteConversationAsync(userMessage, false, cancellationToken);

        public async Task<LlamaResponse> ContinueAsync(
            IEnumerable<ToolResult> toolResults,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(toolResults);

            var results = toolResults.ToList();
            if (results.Count == 0)
                throw new ArgumentException(
                    "At least one tool result is required.",
                    nameof(toolResults));

            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                EnsureInitialized();
                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    foreach (var result in results)
                        AddToolResultToHistory(result);

                    return await CompleteAsync(requestToken);
                }
                finally
                {
                    EndRequest();
                }
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public Task CancelAsync()
        {
            ThrowIfDisposed();
            return _requestCancellation?.CancelAsync() ?? Task.CompletedTask;
        }

        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                _requestCancellation?.Cancel();
                _requestCancellation?.Dispose();
                _requestCancellation = null;
                ResetHistoryInternal();
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public IReadOnlyList<LlamaMessage> GetHistory()
        {
            ThrowIfDisposed();
            return _history.ToList();
        }

        private async Task<LlamaResponse> ExecuteConversationAsync(
            string userMessage,
            bool resetConversation,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                if (resetConversation)
                    ResetHistoryInternal();
                else
                    EnsureInitialized();

                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    AddUserMessage(userMessage);
                    return await CompleteAsync(requestToken);
                }
                finally
                {
                    EndRequest();
                }
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private async Task<LlamaResponse> CompleteAsync(
            CancellationToken cancellationToken)
        {
            var completion = await _llama.CompleteAsync(
                new LlamaCompletionRequest
                {
                    Messages = _history.ToList()
                },
                cancellationToken);

            AddAssistantResponse(completion);
            return ParseResponse(completion);
        }

        private void ResetHistoryInternal()
        {
            _history.Clear();

            _history.Add(new LlamaMessage
            {
                Role = LlamaMessageRole.System.ToString(),
                Content = _systemPrompt
            });

            _isInitialized = true;
        }

        private void AddUserMessage(string message) =>
            _history.Add(new LlamaMessage
            {
                Role = LlamaMessageRole.User.ToString(),
                Content = message
            });

        private void AddToolResultToHistory(ToolResult result) =>
            _history.Add(new LlamaMessage
            {
                Role = LlamaMessageRole.Tool.ToString(),
                Content = result.ToJson()
            });

        private void AddAssistantResponse(
            LlamaCompletionResponse completion)
        {
            if (!string.IsNullOrWhiteSpace(completion.Content))
            {
                _history.Add(new LlamaMessage
                {
                    Role = LlamaMessageRoles.Assistant,
                    Content = completion.Content
                });
            }
        }

        private CancellationToken BeginRequest(
            CancellationToken cancellationToken)
        {
            _requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            return _requestCancellation.Token;
        }

        private void EndRequest()
        {
            _requestCancellation?.Dispose();
            _requestCancellation = null;
        }

        private static LlamaResponse ParseResponse(
            LlamaCompletionResponse completion)
        {
            if (string.IsNullOrWhiteSpace(completion.Content))
                throw new InvalidOperationException(
                    "Llama completion contained no content.");

            var dto = JsonConvert.DeserializeObject<LlamaResponseDto>(
                          completion.Content)
                      ?? throw new InvalidOperationException(
                          "Unable to parse Llama response.");

            var response = new LlamaResponse
            {
                SpokenText = dto.SpokenText ?? string.Empty
            };

            if (dto.SoundEvents is not null)
                response.SoundEvents.AddRange(dto.SoundEvents);

            if (dto.ToolRequests is not null)
                response.ToolRequests.AddRange(dto.ToolRequests);

            return response;
        }

        private string LoadSystemPrompt()
        {
            var configuredPath =
                _configuration["Application:PromptsDirectory"]
                ?? throw new InvalidOperationException(
                    "Application:PromptsDirectory is not configured.");

            var promptsPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        configuredPath));

            var promptFile = Path.Combine(
                promptsPath,
                "conversation-system.txt");

            if (!File.Exists(promptFile))
                throw new FileNotFoundException(
                    "Conversation Llama system prompt was not found.",
                    promptFile);

            var prompt = File.ReadAllText(promptFile).Trim();
            var soundUsage = _soundEffectManager.GetPromptText();

            if (string.IsNullOrWhiteSpace(soundUsage))
                return prompt;

            return string.Join(
                Environment.NewLine + Environment.NewLine,
                prompt,
                "# Available Sound Effects",
                "Use these sound tags in the response soundEvents array when appropriate.",
                soundUsage);
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException(
                    "Conversation Llama client has not been initialized.");
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestLock.Dispose();
        }
    }

    public enum LlamaMessageRole
    {
        System,
        User,
        Assistant,
        Tool
    }

    public static class LlamaMessageRoles
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }
}
