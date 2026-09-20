using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Tools;

namespace AIRadio.Server.Services.AI
{
    public interface IConversationLlamaClient : IDisposable
    {
        bool IsInitialized { get; }
        bool IsBusy { get; }
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<LlamaResponse> StartConversationAsync(string userMessage, CancellationToken cancellationToken = default);
        Task<LlamaResponse> ContinueToolAsync(CancellationToken cancellationToken = default);
        Task<LlamaResponse> ContinueToolAsync(string userMessage, CancellationToken cancellationToken = default);
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
        private readonly IToolExecutor _toolExecutor;
        private readonly List<LlamaMessage> _history = [];
        private readonly HashSet<string> _activeToolNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private string? _intentSystemPrompt;
        private string? _toolExecutionSystemPrompt;
        private string? _toolResponseSystemPrompt;
        private string? _conversationResponseSystemPrompt;
        private CancellationTokenSource? _requestCancellation;
        private bool _isInitialized;
        private bool _disposed;

        public ConversationLlamaClient(ILogger<ConversationLlamaClient> logger, ILlamaHttpClient llama, IConfiguration configuration, ISoundEffectManager soundEffectManager, IToolExecutor toolExecutor)
        {
            _logger = logger;
            _llama = llama;
            _configuration = configuration;
            _soundEffectManager = soundEffectManager;
            _toolExecutor = toolExecutor;
        }

        public bool IsInitialized => _isInitialized;
        public bool IsBusy => _requestCancellation is not null;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized) return;
                await EnsureSystemPromptsAsync(cancellationToken);
                await _llama.InitializeAsync(cancellationToken);
                await WarmSystemPromptAsync(cancellationToken);
                ResetHistoryInternal();
                _logger.LogInformation("Conversation Llama client initialized.");
            }
            finally { _requestLock.Release(); }
        }

        public Task<LlamaResponse> StartConversationAsync(string userMessage, CancellationToken cancellationToken = default) => ExecuteConversationAsync(userMessage, true, cancellationToken);
        public Task<LlamaResponse> ContinueToolAsync(CancellationToken cancellationToken = default) => ExecuteToolRoundAsync(null, cancellationToken);
        public Task<LlamaResponse> ContinueToolAsync(string userMessage, CancellationToken cancellationToken = default) => ExecuteToolRoundAsync(userMessage, cancellationToken);

        public async Task<LlamaResponse> ContinueAsync(IEnumerable<ToolResult> toolResults, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(toolResults);
            var results = toolResults.ToList();
            if (results.Count == 0) throw new ArgumentException("At least one tool result is required.", nameof(toolResults));

            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                await EnsureInitializedAsync(cancellationToken);
                if (_activeToolNames.Count == 0)
                    throw new InvalidOperationException("Tool results were supplied without an active tool round.");

                var isConversation = results.Count == 1 &&
                    results[0].ToolName.Equals("conversation", StringComparison.OrdinalIgnoreCase);

                if (isConversation)
                {
                    SetSystemPrompt(BuildConversationResponsePrompt());
                    RemoveLastAssistantResponse();
                }
                else
                {
                    SetSystemPrompt(BuildToolResponsePrompt());
                    foreach (var result in results) AddToolResultToHistory(result);
                }

                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    return await CompleteAsync(requestToken, isConversation ? 40 : 48);
                }
                finally { EndRequest(); }
            }
            finally { _requestLock.Release(); }
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
                await EnsureSystemPromptsAsync(cancellationToken);
                ResetHistoryInternal();
            }
            finally { _requestLock.Release(); }
        }

        public IReadOnlyList<LlamaMessage> GetHistory()
        {
            ThrowIfDisposed();
            return _history.ToList();
        }

        private async Task<LlamaResponse> ExecuteConversationAsync(string userMessage, bool resetConversation, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                await EnsureSystemPromptsAsync(cancellationToken);
                if (resetConversation) ResetHistoryInternal();
                else await EnsureInitializedAsync(cancellationToken);

                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    AddUserMessage(userMessage);
                    return await CompleteAsync(requestToken, 12, fallbackToConversation: true);
                }
                finally { EndRequest(); }
            }
            finally { _requestLock.Release(); }
        }

        private async Task<LlamaResponse> ExecuteToolRoundAsync(string? userMessage, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                await EnsureInitializedAsync(cancellationToken);
                if (_activeToolNames.Count == 0)
                    throw new InvalidOperationException("A tool round was requested without an active tool.");

                // The intent response is an intermediate classification result, not
                // conversation history. Remove it before asking the model to parse
                // arguments so the chat template sees the original user utterance
                // as the latest turn rather than continuing the {tool:NAME} response.
                RemoveLastAssistantResponse();
                SetSystemPrompt(BuildToolExecutionPrompt());
                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    if (!string.IsNullOrWhiteSpace(userMessage))
                        AddUserMessage(userMessage);

                    return await CompleteAsync(requestToken, 24);
                }
                finally { EndRequest(); }
            }
            finally { _requestLock.Release(); }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_isInitialized) return;
            await EnsureSystemPromptsAsync(cancellationToken);
            await _llama.InitializeAsync(cancellationToken);
            await WarmSystemPromptAsync(cancellationToken);
            ResetHistoryInternal();
            _logger.LogInformation("Conversation Llama client initialized on first use.");
        }

        private async Task EnsureSystemPromptsAsync(CancellationToken cancellationToken)
        {
            if (_intentSystemPrompt is not null &&
                _toolExecutionSystemPrompt is not null &&
                _toolResponseSystemPrompt is not null &&
                _conversationResponseSystemPrompt is not null)
                return;

            var configuredPath = _configuration["Application:PromptsDirectory"]
                ?? throw new InvalidOperationException("Application:PromptsDirectory is not configured.");
            var promptsPath = Path.IsPathRooted(configuredPath) ? configuredPath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

            var intentFile = Path.Combine(promptsPath, "intent-system.txt");
            var executionFile = Path.Combine(promptsPath, "tool-execution-system.txt");
            var responseFile = Path.Combine(promptsPath, "tool-response-system.txt");
            var conversationResponseFile = Path.Combine(promptsPath, "conversation-response-system.txt");

            if (!File.Exists(intentFile)) throw new FileNotFoundException("Intent Llama system prompt was not found.", intentFile);
            if (!File.Exists(executionFile)) throw new FileNotFoundException("Tool execution Llama system prompt was not found.", executionFile);
            if (!File.Exists(responseFile)) throw new FileNotFoundException("Tool response Llama system prompt was not found.", responseFile);
            if (!File.Exists(conversationResponseFile)) throw new FileNotFoundException("Conversation response Llama system prompt was not found.", conversationResponseFile);

            var soundsDirectory = _configuration["Application:SoundsDirectory"]
                ?? throw new InvalidOperationException("Application:SoundsDirectory is not configured.");
            soundsDirectory = Path.IsPathRooted(soundsDirectory) ? soundsDirectory : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, soundsDirectory));
            await _soundEffectManager.InitializeAsync(soundsDirectory, cancellationToken);

            var intentPrompt = (await File.ReadAllTextAsync(intentFile, cancellationToken)).Trim();
            var executionPrompt = (await File.ReadAllTextAsync(executionFile, cancellationToken)).Trim();
            var responsePrompt = (await File.ReadAllTextAsync(responseFile, cancellationToken)).Trim();
            var conversationResponsePrompt = (await File.ReadAllTextAsync(conversationResponseFile, cancellationToken)).Trim();
            var toolCatalog = _toolExecutor.GetLlmCatalog();

            var intentSections = new List<string>
            {
                intentPrompt,
                toolCatalog
            };

            _intentSystemPrompt = string.Join("\n", intentSections);
            _toolExecutionSystemPrompt = executionPrompt;
            _toolResponseSystemPrompt = responsePrompt;
            _conversationResponseSystemPrompt = conversationResponsePrompt;
        }

        private string BuildToolExecutionPrompt()
        {
            if (_toolExecutionSystemPrompt is null)
                throw new InvalidOperationException("The tool execution system prompt has not been initialized.");

            var sections = new List<string> { _toolExecutionSystemPrompt };

            var toolInstructions = _toolExecutor.GetLlmInstructions(_activeToolNames);
            if (!string.IsNullOrWhiteSpace(toolInstructions))
                sections.Add(toolInstructions);

            return string.Join("\n", sections);
        }

        private string BuildConversationResponsePrompt()
        {
            if (_conversationResponseSystemPrompt is null)
                throw new InvalidOperationException("The conversation response system prompt has not been initialized.");

            return _conversationResponseSystemPrompt;
        }

        private string BuildToolResponsePrompt()
        {
            if (_toolResponseSystemPrompt is null)
                throw new InvalidOperationException("The tool response system prompt has not been initialized.");

            var sections = new List<string> { _toolResponseSystemPrompt };

            var toolInstructions = _toolExecutor.GetLlmResponseInstructions(_activeToolNames);
            if (!string.IsNullOrWhiteSpace(toolInstructions))
                sections.Add(toolInstructions);

            var soundUsage = _soundEffectManager.GetPromptText(_activeToolNames);
            if (!string.IsNullOrWhiteSpace(soundUsage))
                sections.Add("AVAILABLE SOUND EFFECTS\n" + soundUsage);

            return string.Join("\n", sections);
        }

        private async Task WarmSystemPromptAsync(CancellationToken cancellationToken)
        {
            if (_intentSystemPrompt is null) throw new InvalidOperationException("The intent system prompt has not been initialized.");

            _logger.LogInformation("Warming Llama intent prompt cache.");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var request = new LlamaCompletionRequest
            {
                Messages =
                [
                    new LlamaMessage
                    {
                        Role = LlamaMessageRoles.System,
                        Content = _intentSystemPrompt
                    }
                ],
                MaxTokens = 1,
                Stream = false
            };

            await _llama.CompleteAsync(request, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation("Llama system prompt cache warmed in {ElapsedSeconds:F1} seconds.", stopwatch.Elapsed.TotalSeconds);
        }

        private async Task<LlamaResponse> CompleteAsync(CancellationToken cancellationToken, int maxTokens, bool fallbackToConversation = false)
        {
            var message = new LlamaCompletionRequest
            {
                Messages = _history.ToList(),
                MaxTokens = maxTokens,
                Temperature = 0.2
            };
            var completion = await _llama.CompleteAsync(message, cancellationToken);
            AddAssistantResponse(completion);
            return ParseResponse(completion, fallbackToConversation);
        }

        private void ResetHistoryInternal()
        {
            if (_intentSystemPrompt is null) throw new InvalidOperationException("The intent system prompt has not been initialized.");
            _history.Clear();
            _activeToolNames.Clear();
            SetSystemPrompt(_intentSystemPrompt);
            _isInitialized = true;
        }

        private void SetSystemPrompt(string prompt)
        {
            if (_history.Count > 0 && _history[0].Role == LlamaMessageRoles.System)
            {
                _history[0] = new LlamaMessage { Role = LlamaMessageRoles.System, Content = prompt };
                return;
            }

            _history.Insert(0, new LlamaMessage { Role = LlamaMessageRoles.System, Content = prompt });
        }

        private void AddUserMessage(string message) => _history.Add(new LlamaMessage { Role = LlamaMessageRoles.User, Content = message });

        private void AddToolResultToHistory(ToolResult result) =>
            _history.Add(new LlamaMessage
            {
                Role = LlamaMessageRoles.Tool,
                Name = result.ToolName,
                Content = result.ToJson()
            });

        private void RemoveLastAssistantResponse()
        {
            for (var i = _history.Count - 1; i >= 1; i--)
            {
                if (_history[i].Role == LlamaMessageRoles.Assistant)
                {
                    _history.RemoveAt(i);
                    return;
                }
            }
        }

        private void AddAssistantResponse(LlamaCompletionResponse completion)
        {
            if (!string.IsNullOrWhiteSpace(completion.Content))
                _history.Add(new LlamaMessage { Role = LlamaMessageRoles.Assistant, Content = completion.Content });
        }

        private CancellationToken BeginRequest(CancellationToken cancellationToken)
        {
            _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _requestCancellation.Token;
        }

        private void EndRequest()
        {
            _requestCancellation?.Dispose();
            _requestCancellation = null;
        }

        private LlamaResponse ParseResponse(LlamaCompletionResponse completion, bool fallbackToConversation = false)
        {
            if (string.IsNullOrWhiteSpace(completion.Content))
                throw new InvalidOperationException("Llama completion contained no content.");

            var response = LlamaResponseParser.Parse(completion.Content, fallbackToConversation);
            _activeToolNames.Clear();
            foreach (var request in response.ToolRequests)
            {
                if (!string.IsNullOrWhiteSpace(request.Name))
                    _activeToolNames.Add(request.Name);
            }

            return response;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestLock.Dispose();
        }
    }

    public enum LlamaMessageRole { System, User, Assistant, Tool }

    public static class LlamaMessageRoles
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }
}
