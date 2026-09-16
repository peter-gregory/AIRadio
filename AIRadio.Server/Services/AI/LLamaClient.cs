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
        private readonly IToolExecutor _toolExecutor;
        private readonly List<LlamaMessage> _history = [];
        private readonly HashSet<string> _activeToolNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private string? _conversationSystemPrompt;
        private string? _toolExecutionSystemPrompt;
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
        public Task<LlamaResponse> ContinueAsync(string userMessage, CancellationToken cancellationToken = default) => ExecuteConversationAsync(userMessage, false, cancellationToken);

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

                SetSystemPrompt(BuildToolExecutionPrompt());
                var requestToken = BeginRequest(cancellationToken);
                try
                {
                    foreach (var result in results) AddToolResultToHistory(result);
                    return await CompleteAsync(requestToken);
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
                    return await CompleteAsync(requestToken);
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
            if (_conversationSystemPrompt is not null && _toolExecutionSystemPrompt is not null) return;

            var configuredPath = _configuration["Application:PromptsDirectory"]
                ?? throw new InvalidOperationException("Application:PromptsDirectory is not configured.");
            var promptsPath = Path.IsPathRooted(configuredPath) ? configuredPath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

            var conversationFile = Path.Combine(promptsPath, "conversation-system.txt");
            var executionFile = Path.Combine(promptsPath, "tool-execution-system.txt");
            if (!File.Exists(conversationFile)) throw new FileNotFoundException("Conversation Llama system prompt was not found.", conversationFile);
            if (!File.Exists(executionFile)) throw new FileNotFoundException("Tool execution Llama system prompt was not found.", executionFile);

            var soundsDirectory = _configuration["Application:SoundsDirectory"]
                ?? throw new InvalidOperationException("Application:SoundsDirectory is not configured.");
            soundsDirectory = Path.IsPathRooted(soundsDirectory) ? soundsDirectory : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, soundsDirectory));
            await _soundEffectManager.InitializeAsync(soundsDirectory, cancellationToken);

            var conversationPrompt = (await File.ReadAllTextAsync(conversationFile, cancellationToken)).Trim();
            var executionPrompt = (await File.ReadAllTextAsync(executionFile, cancellationToken)).Trim();
            var toolCatalog = _toolExecutor.GetLlmCatalog();
            var soundUsage = _soundEffectManager.GetPromptText();

            var conversationSections = new List<string>
            {
                conversationPrompt,
                toolCatalog
            };
            if (!string.IsNullOrWhiteSpace(soundUsage)) conversationSections.Add("SOUNDS\n" + soundUsage);

            var executionSections = new List<string>
            {
                executionPrompt
            };
            if (!string.IsNullOrWhiteSpace(soundUsage)) executionSections.Add("SOUNDS\n" + soundUsage);

            _conversationSystemPrompt = string.Join("\n", conversationSections);
            _toolExecutionSystemPrompt = string.Join("\n", executionSections);
        }

        private string BuildToolExecutionPrompt()
        {
            if (_toolExecutionSystemPrompt is null)
                throw new InvalidOperationException("The tool execution system prompt has not been initialized.");

            var toolInstructions = _toolExecutor.GetLlmInstructions(_activeToolNames);
            if (string.IsNullOrWhiteSpace(toolInstructions))
                return _toolExecutionSystemPrompt;

            return _toolExecutionSystemPrompt + "\n" + toolInstructions;
        }

        private async Task WarmSystemPromptAsync(CancellationToken cancellationToken)
        {
            if (_conversationSystemPrompt is null) throw new InvalidOperationException("The conversation system prompt has not been initialized.");

            _logger.LogInformation("Warming Llama system prompt cache.");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var request = new LlamaCompletionRequest
            {
                Messages =
                [
                    new LlamaMessage
                    {
                        Role = LlamaMessageRoles.System,
                        Content = _conversationSystemPrompt
                    }
                ],
                MaxTokens = 1,
                Stream = false
            };

            await _llama.CompleteAsync(request, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation("Llama system prompt cache warmed in {ElapsedSeconds:F1} seconds.", stopwatch.Elapsed.TotalSeconds);
        }

        private async Task<LlamaResponse> CompleteAsync(CancellationToken cancellationToken)
        {
            var message = new LlamaCompletionRequest { Messages = _history.ToList() };
            var completion = await _llama.CompleteAsync(message, cancellationToken);
            AddAssistantResponse(completion);
            return ParseResponse(completion);
        }

        private void ResetHistoryInternal()
        {
            if (_conversationSystemPrompt is null) throw new InvalidOperationException("The conversation system prompt has not been initialized.");
            _history.Clear();
            _activeToolNames.Clear();
            SetSystemPrompt(_conversationSystemPrompt);
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

        private LlamaResponse ParseResponse(LlamaCompletionResponse completion)
        {
            if (string.IsNullOrWhiteSpace(completion.Content))
                throw new InvalidOperationException("Llama completion contained no content.");

            var response = LlamaResponseParser.Parse(completion.Content);
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
