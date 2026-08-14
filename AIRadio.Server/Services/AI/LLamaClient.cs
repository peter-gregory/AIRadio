using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace AIRadio.Server.Services.AI
{
    public interface IConversationLlamaClient : IDisposable
    {
        bool IsInitialized { get; }

        bool IsBusy { get; }

        Task InitializeAsync(
            CancellationToken cancellationToken = default);

        Task<LlamaResponse> StartConversationAsync(
            string userMessage,
            CancellationToken cancellationToken = default);

        Task<LlamaResponse> ContinueAsync(
            string userMessage,
            CancellationToken cancellationToken = default);

        Task<LlamaResponse> ContinueAsync(
            IEnumerable<ToolResult> toolResults,
            CancellationToken cancellationToken = default);

        Task CancelAsync();

        Task ResetAsync(
            CancellationToken cancellationToken = default);

        IReadOnlyList<LlamaMessage> GetHistory();
    }

    public sealed class ConversationLlamaClient
        : IConversationLlamaClient
    {
        private readonly ILogger<ConversationLlamaClient> _logger;
        private readonly ILlamaHttpClient _llama;
        private readonly IConfiguration _configuration;

        private readonly List<LlamaMessage> _history = [];

        private readonly string _systemPrompt;

        private readonly SemaphoreSlim _requestLock =
            new(1, 1);

        private CancellationTokenSource? _requestCancellation;

        private bool _isInitialized;
        private bool _disposed;

        public ConversationLlamaClient(
            ILogger<ConversationLlamaClient> logger,
            ILlamaHttpClient llama,
            IConfiguration configuration)
        {
            _logger = logger;
            _llama = llama;
            _configuration = configuration;

            _systemPrompt =
                LoadSystemPrompt();
        }

        // ============================================================
        // STATE
        // ============================================================

        public bool IsInitialized =>
            _isInitialized;

        public bool IsBusy =>
            _requestCancellation is not null;

        // ============================================================
        // INITIALIZE
        // ============================================================

        public async Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                ThrowIfDisposed();

                if (_isInitialized)
                {
                    return;
                }

                _history.Clear();

                _history.Add(
                    new LlamaMessage
                    {
                        Role =
                            LlamaMessageRole.System.ToString(),

                        Content =
                            _systemPrompt
                    });

                _isInitialized = true;

                _logger.LogInformation(
                    "Conversation Llama client initialized.");
            }
            finally
            {
                _requestLock.Release();
            }
        }

        // ============================================================
        // START CONVERSATION
        // ============================================================

        public Task<LlamaResponse> StartConversationAsync(
            string userMessage,
            CancellationToken cancellationToken = default)
        {
            return ExecuteConversationAsync(
                userMessage,
                resetConversation: true,
                cancellationToken);
        }

        // ============================================================
        // CONTINUE
        // ============================================================

        public Task<LlamaResponse> ContinueAsync(
            string userMessage,
            CancellationToken cancellationToken = default)
        {
            return ExecuteConversationAsync(
                userMessage,
                resetConversation: false,
                cancellationToken);
        }

        // ============================================================
        // CONTINUE WITH TOOL RESULTS
        // ============================================================

        public async Task<LlamaResponse> ContinueAsync(
            IEnumerable<ToolResult> toolResults,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ArgumentNullException.ThrowIfNull(
                toolResults);

            var results =
                toolResults.ToList();

            if (results.Count == 0)
            {
                throw new ArgumentException(
                    "At least one tool result is required.",
                    nameof(toolResults));
            }

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                ThrowIfDisposed();

                EnsureInitialized();

                var requestToken =
                    BeginRequest(
                        cancellationToken);

                try
                {
                    foreach (var result in results)
                    {
                        AddToolResultToHistory(
                            result);
                    }

                    return await CompleteAsync(
                        requestToken);
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

        // ============================================================
        // CANCEL
        // ============================================================

        public Task CancelAsync()
        {
            ThrowIfDisposed();

            _requestCancellation?
                .Cancel();

            return Task.CompletedTask;
        }

        // ============================================================
        // RESET
        // ============================================================

        public async Task ResetAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                ThrowIfDisposed();

                _requestCancellation?
                    .Cancel();

                _requestCancellation?.Dispose();

                _requestCancellation = null;

                _history.Clear();

                _history.Add(
                    new LlamaMessage
                    {
                        Role =
                            LlamaMessageRole.System.ToString(),

                        Content =
                            _systemPrompt
                    });

                _isInitialized = true;

                _logger.LogDebug(
                    "Conversation Llama client reset.");
            }
            finally
            {
                _requestLock.Release();
            }
        }

        // ============================================================
        // HISTORY
        // ============================================================

        public IReadOnlyList<LlamaMessage> GetHistory()
        {
            ThrowIfDisposed();

            return _history.ToList();
        }

        // ============================================================
        // CONVERSATION EXECUTION
        // ============================================================

        private async Task<LlamaResponse>
            ExecuteConversationAsync(
                string userMessage,
                bool resetConversation,
                CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            ArgumentException.ThrowIfNullOrWhiteSpace(
                userMessage);

            await _requestLock.WaitAsync(
                cancellationToken);

            try
            {
                ThrowIfDisposed();

                if (resetConversation)
                {
                    ResetHistoryInternal();
                }
                else
                {
                    EnsureInitialized();
                }

                var requestToken =
                    BeginRequest(
                        cancellationToken);

                try
                {
                    AddUserMessage(
                        userMessage);

                    return await CompleteAsync(
                        requestToken);
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

        // ============================================================
        // LLAMA COMPLETION
        // ============================================================

        private async Task<LlamaResponse> CompleteAsync(
            CancellationToken cancellationToken)
        {
            var request =
                new LlamaCompletionRequest
                {
                    Messages =
                        _history.ToList()
                };

            var completion =
                await _llama.CompleteAsync(
                    request,
                    cancellationToken);

            AddAssistantResponse(
                completion);

            return ParseResponse(
                completion);
        }

        // ============================================================
        // HISTORY
        // ============================================================

        private void ResetHistoryInternal()
        {
            _history.Clear();

            _history.Add(
                new LlamaMessage
                {
                    Role =
                        LlamaMessageRole.System.ToString(),

                    Content =
                        _systemPrompt
                });

            _isInitialized = true;
        }

        private void AddUserMessage(
            string message)
        {
            _history.Add(
                new LlamaMessage
                {
                    Role =
                        LlamaMessageRole.User.ToString(),

                    Content =
                        message
                });
        }

        private void AddToolResultToHistory(
            ToolResult result)
        {
            _history.Add(
                new LlamaMessage
                {
                    Role =
                        LlamaMessageRole.Tool.ToString(),

                    Content =
                        result.ToJson()
                });
        }

        private void AddAssistantResponse(
            LlamaCompletionResponse completion)
        {
            if (string.IsNullOrWhiteSpace(
                    completion.Content))
            {
                return;
            }

            _history.Add(
                new LlamaMessage
                {
                    Role =
                        LlamaMessageRoles.Assistant,

                    Content =
                        completion.Content
                });
        }

        // ============================================================
        // REQUEST LIFETIME
        // ============================================================

        private CancellationToken BeginRequest(
            CancellationToken cancellationToken)
        {
            _requestCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);

            return _requestCancellation.Token;
        }

        private void EndRequest()
        {
            _requestCancellation?.Dispose();

            _requestCancellation = null;
        }

        // ============================================================
        // RESPONSE PARSING
        // ============================================================

        private static LlamaResponse ParseResponse(
            LlamaCompletionResponse completion)
        {
            if (string.IsNullOrWhiteSpace(
                    completion.Content))
            {
                throw new InvalidOperationException(
                    "Llama completion contained no content.");
            }

            var dto =
                JsonConvert.DeserializeObject<LlamaResponseDto>(
                    completion.Content);

            if (dto is null)
            {
                throw new InvalidOperationException(
                    "Unable to parse Llama response.");
            }

            var response =
                new LlamaResponse
                {
                    SpokenText =
                        dto.SpokenText ?? string.Empty
                };

            if (dto.SoundEvents is not null)
            {
                response.SoundEvents.AddRange(
                    dto.SoundEvents);
            }

            if (dto.ToolRequests is not null)
            {
                response.ToolRequests.AddRange(
                    dto.ToolRequests);
            }

            return response;
        }

        // ============================================================
        // PROMPT
        // ============================================================

        private string LoadSystemPrompt()
        {
            var promptsPath =
                _configuration[
                    "Radio:Paths:Prompts"];

            if (string.IsNullOrWhiteSpace(
                    promptsPath))
            {
                throw new InvalidOperationException(
                    "Configuration value " +
                    "'Radio:Paths:Prompts' is required.");
            }

            var promptFile =
                Path.Combine(
                    promptsPath,
                    "conversation-system.txt");

            if (!File.Exists(promptFile))
            {
                throw new FileNotFoundException(
                    "Conversation Llama system prompt was not found.",
                    promptFile);
            }

            return File.ReadAllText(
                promptFile)
                .Trim();
        }

        // ============================================================
        // VALIDATION
        // ============================================================

        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException(
                    "Conversation Llama client has not been initialized.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);
        }

        // ============================================================
        // DISPOSE
        // ============================================================

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _requestCancellation?
                .Cancel();

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
