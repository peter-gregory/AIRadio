using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Events;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Radio
{
    public interface IConversationService
    {
        ConversationState State { get; }
        bool IsWaitingForInput { get; }
        bool WasCancelled { get; }
        event EventHandler<ConversationStateChangedEventArgs>? StateChanged;
        event EventHandler<ConversationCompletedEventArgs>? ConversationCompleted;

        Task ProcessAsync(string text, CancellationToken cancellationToken = default);
        Task ProcessToolAsync(ToolRequest request, CancellationToken cancellationToken = default);
        Task PlayWakeAcknowledgementAsync(CancellationToken cancellationToken = default);
        Task<Guid> StartAlarmAsync(string text, CancellationToken cancellationToken = default);
        Task ProcessAndWaitAsync(string text, CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
        void ResetCancellation();
    }

    public sealed class ConversationService : IConversationService, IAsyncDisposable
    {
        private readonly ILogger<ConversationService> _logger;
        private readonly IConversationLlamaClient _llama;
        private readonly IAudioManager _audioManager;
        private readonly IReadOnlyDictionary<string, ITool> _tools;
        private readonly AsyncWorkQueue<ConversationRequest> _queue;
        private ToolRequest? _pendingToolRequest;
        private string? _completionPrompt;
        private ConversationState _state = ConversationState.Idle;
        private Guid _conversationId;
        private bool _wasCancelled;
        private Guid _idleConversationId;
        private Guid _playbackCompletedConversationId;
        private Guid _completedEventConversationId;

        public ConversationState State => _state;
        public bool IsWaitingForInput => _state == ConversationState.WaitingForInput;
        public bool WasCancelled => _wasCancelled;
        public event EventHandler<ConversationStateChangedEventArgs>? StateChanged;
        public event EventHandler<ConversationCompletedEventArgs>? ConversationCompleted;

        public ConversationService(ILogger<ConversationService> logger, IConversationLlamaClient llama, IAudioManager audioManager, IEnumerable<ITool> tools)
        {
            _logger = logger;
            _llama = llama;
            _audioManager = audioManager;
            ArgumentNullException.ThrowIfNull(tools);
            _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
            _queue = new AsyncWorkQueue<ConversationRequest>();
            _queue.Start(ProcessRequestAsync);
            _audioManager.PlaybackCompleted += OnAudioPlaybackCompleted;
        }

        public Task ProcessAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queue.TryEnqueue(new ConversationRequest(Guid.NewGuid(), text, false, null)))
                _logger.LogDebug("Conversation request rejected because the conversation queue is not accepting work.");
            return Task.CompletedTask;
        }

        public Task ProcessToolAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_queue.TryEnqueue(new ConversationRequest(
                    Guid.NewGuid(),
                    string.Empty,
                    false,
                    request)))
            {
                _logger.LogDebug(
                    "Pre-LLM tool request rejected because the conversation queue is not accepting work.");
            }

            return Task.CompletedTask;
        }

        public Task PlayWakeAcknowledgementAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _audioManager.PlaySpeechAsync(GetRandomPhrase(WakeAcknowledgements), cancellationToken);
        }

        public Task<Guid> StartAlarmAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();

            var conversationId = Guid.NewGuid();

            if (!_queue.TryEnqueue(new ConversationRequest(conversationId, text, true, null)))
                throw new InvalidOperationException(
                    "Conversation request was rejected because the conversation queue is not accepting work.");

            return Task.FromResult(conversationId);
        }

        public async Task ProcessAndWaitAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var conversationId = Guid.Empty;

            EventHandler<ConversationCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (args.ConversationId == conversationId)
                    completion.TrySetResult(true);
            };

            ConversationCompleted += handler;
            try
            {
                conversationId = await StartAlarmAsync(text, cancellationToken);

                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                ConversationCompleted -= handler;
            }
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Preserve the user cancellation after the active conversation has
            // been cancelled so an alarm can stop its remaining actions.
            _wasCancelled = true;

            await _queue.CancelAsync(CancellationToken.None);
            await _audioManager.CancelAsync(CancellationToken.None);
            _pendingToolRequest = null;
            SetState(ConversationState.Complete, _conversationId);
            SetState(ConversationState.Idle, _conversationId);
            _queue.Resume();
        }

        public void ResetCancellation()
        {
            _wasCancelled = false;
        }

        private static readonly string[] WakeAcknowledgements =
        [
            "Got it",
            "Okay",
            "Sure",
            "Yes"
        ];

        private static readonly IReadOnlyDictionary<string, string[]> IntentPreambles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["news"] =
                [
                    "Getting the latest news headlines",
                    "Let me check the latest news",
                    "Checking for the latest headlines"
                ],
                ["weather"] =
                [
                    "Checking the current weather conditions",
                    "Let me check the weather for you",
                    "Getting the current weather conditions"
                ],
                ["alarm"] =
                [
                    "Let's set up a new alarm",
                    "I'll set up that alarm for you",
                    "Let's get your alarm set up"
                ],
                ["events"] =
                [
                    "Getting your events for the day {sound:event-intro}"
                ],
                ["conversation"] =
                [
                    "Give me a second",
                    "Just a moment",
                    "Let me think about that"
                ]
            };

        private static string GetRandomPhrase(IReadOnlyList<string> phrases) =>
            phrases[Random.Shared.Next(phrases.Count)];

        private static string? GetIntentPreamble(string intent) =>
            IntentPreambles.TryGetValue(intent, out var preambles)
                ? GetRandomPhrase(preambles)
                : null;

        private async Task ProcessRequestAsync(ConversationRequest request, CancellationToken cancellationToken)
        {
            var conversationComplete = false;
            var conversationId = request.IsAlarm
                ? request.ConversationId
                : _pendingToolRequest is not null
                    ? _conversationId
                    : request.ConversationId;
            _conversationId = conversationId;

            try
            {
                if (request.IsAlarm)
                {
                    // Alarm actions are independent commands. Start each one
                    // with a clean conversation state so an outstanding
                    // interactive question cannot consume the alarm action.
                    _pendingToolRequest = null;
                    await _llama.ResetAsync(cancellationToken);
                }

                SetState(ConversationState.Processing, conversationId);

                // Alarm activation preambles are already fully rendered speech.
                // Do not send them through intent classification or argument
                // parsing; that would turn the preamble into a new alarm request.
                if (request.IsAlarm &&
                    request.Text.StartsWith("{sound:alarm-alarm}", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Playing alarm activation preamble directly: {Text}",
                        request.Text);

                    await _audioManager.PlaySpeechAsync(
                        request.Text,
                        cancellationToken);

                    conversationComplete = true;
                }
                else if (_pendingToolRequest is not null)
                {
                    var pendingRequest = ApplyPendingToolInput(request.Text);
                    _pendingToolRequest = null;
                    conversationComplete = await ExecutePendingToolAsync(pendingRequest, cancellationToken);
                }
                else if (request.InitialToolRequest is not null)
                {
                    _logger.LogInformation(
                        "Executing pre-LLM tool request directly: {ToolName}",
                        request.InitialToolRequest.Name);

                    var result = await ExecuteToolAsync(
                        request.InitialToolRequest,
                        cancellationToken);

                    var handling = await HandleToolResultAsync(
                        result,
                        cancellationToken);

                    if (handling.Waiting)
                    {
                        conversationComplete = false;
                    }
                    else if (handling.Result is not null)
                    {
                        var response = await _llama.ContinueAsync(
                            [handling.Result],
                            cancellationToken);

                        conversationComplete = await ProcessLlamaResponseAsync(
                            response,
                            cancellationToken);
                    }
                    else
                    {
                        conversationComplete = true;
                    }
                }
                else
                {
                    // Round 1 only selects the tool. Execute parameterless tools
                    // immediately. Parameterized tools enter the argument-parsing
                    // state so the selected tool's full instructions can extract
                    // arguments from the original utterance before execution.
                    var response = await _llama.StartConversationAsync(request.Text, cancellationToken);

                    if (response.ToolRequests.Count == 1)
                    {
                        var selected = response.ToolRequests[0];

                        // Intent is now known. Queue a short, deterministic-purpose
                        // preamble before starting any additional LLM/tool work.
                        var intentPreamble = GetIntentPreamble(selected.Name);
                        if (!string.IsNullOrWhiteSpace(intentPreamble))
                        {
                            await _audioManager.PlaySpeechAsync(
                                intentPreamble,
                                cancellationToken);
                        }

                        if (_tools.TryGetValue(selected.Name, out var selectedTool) && selectedTool.HasParameters)
                        {
                            // radioPlay owns its tuning transition and preamble. Run
                            // that initial tool state before the slow argument-parsing
                            // LLM so the user gets immediate feedback and the old
                            // station is stopped/muted while the new station is resolved.
                            var radioPreamblePrepared =
                                string.Equals(selected.Name, "radioPlay", StringComparison.OrdinalIgnoreCase);

                            if (radioPreamblePrepared)
                            {
                                var preambleRequest = new ToolRequest
                                {
                                    Name = selected.Name,
                                    Arguments = new JObject(),
                                    State = ToolRequestState.Initial
                                };

                                var preambleResult = await selectedTool.ExecuteAsync(
                                    preambleRequest,
                                    cancellationToken);

                                if (preambleResult.Status != ToolResultStatus.Preamble)
                                {
                                    throw new InvalidOperationException(
                                        $"Tool '{selected.Name}' did not return its expected preamble state.");
                                }
                            }
                            // Events have a deterministic parser. The request contains
                            // only a date/category selection, so there is no reason to
                            // spend another LLM round extracting arguments.
                            if (string.Equals(selected.Name, "events", StringComparison.OrdinalIgnoreCase))
                            {
                                var parsed = EventsParser.Parse(request.Text);

                                if (parsed is not null)
                                {
                                    _logger.LogInformation(
                                        "Tool {ToolName} parsed deterministically: Date={Date}, IncludeAlarms={IncludeAlarms}, IncludeReminders={IncludeReminders}.",
                                        selected.Name,
                                        parsed.Date,
                                        parsed.IncludeAlarms,
                                        parsed.IncludeReminders);

                                    response.ToolRequests.Clear();
                                    response.ToolRequests.Add(new ToolRequest
                                    {
                                        Name = selected.Name,
                                        Arguments = new JObject
                                        {
                                            ["timestamp"] = parsed.Date,
                                            ["includeAlarms"] = parsed.IncludeAlarms,
                                            ["includeReminders"] = parsed.IncludeReminders
                                        }
                                    });
                                }
                                else
                                {
                                    _logger.LogInformation(
                                        "Tool {ToolName} deterministic parser could not parse the request; falling back to LLM argument parsing.",
                                        selected.Name);

                                    response = await _llama.ContinueToolAsync(cancellationToken);

                                    var parsedRequests = response.ToolRequests
                                        .Select(toolRequest => toolRequest.WithState(ToolRequestState.ArgumentParsing))
                                        .ToList();

                                    response.ToolRequests.Clear();
                                    response.ToolRequests.AddRange(parsedRequests);
                                }
                            }
                            // radioPlay has only optional arguments. For the saved-list
                            // phrases, there is nothing to extract, so avoid a second
                            // LLM round that can incorrectly turn command wording into
                            // a station name.
                            else if (string.Equals(selected.Name, "radioPlay", StringComparison.OrdinalIgnoreCase) &&
                                     IsSavedRadioPlaybackRequest(request.Text))
                            {
                                _logger.LogInformation(
                                    "Tool {ToolName} selected saved-station playback; skipping argument parsing.",
                                    selected.Name);

                                response.ToolRequests.Clear();
                                response.ToolRequests.Add(new ToolRequest
                                {
                                    Name = selected.Name,
                                    Arguments = new JObject(),
                                    State = ToolRequestState.PreambleComplete
                                });
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Tool {ToolName} requires argument parsing from the original utterance.",
                                    selected.Name);

                                response = await _llama.ContinueToolAsync(cancellationToken);

                                var parsedRequests = response.ToolRequests
                                    .Select(toolRequest => toolRequest.WithState(
                                        string.Equals(selected.Name, "radioPlay", StringComparison.OrdinalIgnoreCase)
                                            ? ToolRequestState.PreambleComplete
                                            : ToolRequestState.ArgumentParsing))
                                    .ToList();

                                response.ToolRequests.Clear();
                                response.ToolRequests.AddRange(parsedRequests);
                            }
                        }
                    }

                    conversationComplete = await ProcessLlamaResponseAsync(response, cancellationToken);
                }

                // Queue deterministic completion speech after the response audio.
                if (!string.IsNullOrWhiteSpace(_completionPrompt))
                {
                    var completionPrompt = _completionPrompt;
                    _completionPrompt = null;
                    await _audioManager.PlaySpeechAsync(completionPrompt, cancellationToken);
                }

                // Alarm actions can legitimately require user input. Keep the
                // pending tool request alive so the user's next utterance can
                // complete the action before the alarm continues to its next action.
                if (conversationComplete)
                {
                    // Conversation state becomes Idle when command processing is
                    // finished. ConversationCompleted is raised separately once
                    // the final PipeWire playback callback has also arrived.
                    SetState(ConversationState.Complete, conversationId);
                    SetState(ConversationState.Idle, conversationId);
                }
                else
                {
                    SetState(ConversationState.WaitingForInput, conversationId);
                }

                _logger.LogInformation(
                    conversationComplete
                        ? "Llama conversation is complete."
                        : "Llama conversation is waiting for additional tool input.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Conversation processing cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation processing failed.");
                conversationComplete = true;
            }
            finally
            {
                if (conversationComplete)
                {
                    if (_state != ConversationState.Idle)
                    {
                        if (_state != ConversationState.Complete)
                            SetState(ConversationState.Complete, conversationId);

                        if (_state == ConversationState.Complete)
                            SetState(ConversationState.Idle, conversationId);
                    }

                    _pendingToolRequest = null;
                    try
                    {
                        await _llama.ResetAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reset the Llama conversation.");
                    }
                }

            }
        }

        private static bool IsSavedRadioPlaybackRequest(string text)
        {
            var normalized = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();

            // The conversation request still contains the wake phrase even though
            // wake-up detection has already matched it. Remove the common wake
            // phrases before matching the actual radio command.
            foreach (var wakePhrase in new[] { "hello radio", "hey radio" })
            {
                if (normalized.StartsWith(wakePhrase, StringComparison.Ordinal))
                {
                    normalized = normalized[wakePhrase.Length..]
                        .TrimStart(' ', ',', '.', ':', ';', '-');
                    break;
                }
            }

            return normalized is
                "play the radio" or
                "play some music" or
                "play my favorites" or
                "play my saved stations" or
                "play my saved radio stations";
        }

        private async Task<bool> ExecutePendingToolAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var result = await ExecuteToolAsync(request, cancellationToken);
            var handling = await HandleToolResultAsync(result, cancellationToken);

            if (handling.Waiting)
                return false;

            if (handling.Result is null)
                return true;

            var response = await _llama.ContinueAsync([handling.Result], cancellationToken);
            return await ProcessLlamaResponseAsync(response, cancellationToken);
        }

        private ToolRequest ApplyPendingToolInput(string text)
        {
            if (_pendingToolRequest is null)
                throw new InvalidOperationException("No pending tool request exists.");

            var arguments = (JObject)_pendingToolRequest.Arguments.DeepClone();
            var missing = arguments.Properties()
                .FirstOrDefault(property =>
                    string.Equals(
                        property.Value.Value<string>(),
                        ToolRequest.RequiredValue,
                        StringComparison.Ordinal));

            if (missing is null)
                throw new InvalidOperationException("The pending tool request has no missing parameter.");

            arguments[missing.Name] = text.Trim();

            return new ToolRequest
            {
                Name = _pendingToolRequest.Name,
                Arguments = arguments,
                State = _pendingToolRequest.State
            };
        }

        private async Task<bool> ProcessLlamaResponseAsync(LlamaResponse response, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.HasToolRequests)
            {
                if (response.HasSpeech)
                    await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);
                return true;
            }

            return await ExecuteToolsAsync(
                response.ToolRequests,
                response.SpokenText,
                response.HasSpeech,
                cancellationToken);
        }

        private async Task<bool> ExecuteToolsAsync(
            IEnumerable<ToolRequest> toolRequests,
            string? preToolSpeech,
            bool hasPreToolSpeech,
            CancellationToken cancellationToken)
        {
            var requests = toolRequests.ToList();
            _logger.LogInformation("Processing " + requests.Count + " tool requests");
            if (requests.Count == 0) return true;

            // ArgumentParsing is a transient state used only while the model
            // extracts arguments from the original utterance. Once parsing has
            // produced the request, execution starts from the tool's normal
            // initial state so tools can perform their own state transitions.
            requests = requests
                .Select(request =>
                    request.State == ToolRequestState.ArgumentParsing
                        ? request.WithState(ToolRequestState.Initial)
                        : request)
                .ToList();

            var resultTasks = requests.Select(request => ExecuteToolAsync(request, cancellationToken)).ToArray();
            var results = await Task.WhenAll(resultTasks);
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Count() == 1)
            {
                var handling = await HandleToolResultAsync(results[0], cancellationToken);
                if (handling.Waiting)
                    return false;

                if (handling.Result is not null)
                {
                    var continueResponse = await _llama.ContinueAsync([handling.Result], cancellationToken);
                    return await ProcessLlamaResponseAsync(continueResponse, cancellationToken);
                }

                return true;
            }

            if (hasPreToolSpeech && !string.IsNullOrWhiteSpace(preToolSpeech))
                await _audioManager.PlaySpeechAsync(preToolSpeech, cancellationToken);

            var response = await _llama.ContinueAsync(results, cancellationToken);
            return await ProcessLlamaResponseAsync(response, cancellationToken);
        }

        private async Task<(bool Waiting, ToolResult? Result)> HandleToolResultAsync(
            ToolResult result,
            CancellationToken cancellationToken)
        {
            if (result.LlmCommands.Count > 0)
            {
                await ProcessLlmCommandsAsync(result.LlmCommands, cancellationToken);
                return (false, null);
            }

            switch (result.Status)
            {
                case ToolResultStatus.MissingParameter:
                    if (!string.IsNullOrWhiteSpace(result.ExactPrompt))
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);

                    if (result.PendingRequest is not null)
                    {
                        _pendingToolRequest = result.PendingRequest;
                        return (true, null);
                    }

                    return (false, result);

                case ToolResultStatus.Preamble:
                    if (!string.IsNullOrWhiteSpace(result.ExactPrompt))
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);

                    if (result.PendingRequest is null)
                        return (false, null);

                    var continuation = await ExecuteToolAsync(result.PendingRequest, cancellationToken);
                    if (continuation.Status == ToolResultStatus.Preamble)
                        throw new InvalidOperationException(
                            $"Tool '{result.ToolName}' returned a repeated preamble without advancing state.");

                    return await HandleToolResultAsync(continuation, cancellationToken);

                case ToolResultStatus.Result:
                    // The tool owns the completion decision. ExactPrompt is direct
                    // speech, while Complete determines whether the result is
                    // terminal or should continue through the response LLM.
                    _completionPrompt = result.CompletionPrompt;
                    if (!string.IsNullOrWhiteSpace(result.ExactPrompt))
                        await _audioManager.PlaySpeechAsync(result.ExactPrompt, cancellationToken);

                    return result.Complete
                        ? (false, null)
                        : (false, result);

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private async Task ProcessLlmCommandsAsync(
            IEnumerable<LlmCommand> commands,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(commands);

            var commandList = commands.ToList();
            if (commandList.Count == 0)
                return;

            _logger.LogInformation(
                "Processing {CommandCount} isolated LLM commands.",
                commandList.Count);

            foreach (var command in commandList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await _llama.ExecuteCommandAsync(command, cancellationToken);

                if (response.HasSpeech)
                    await _audioManager.PlaySpeechAsync(response.SpokenText, cancellationToken);
            }
        }

        private async Task<ToolResult> ExecuteToolAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Processing tool {ToolName} (state: {ToolState})",
                request.Name,
                request.State);

            if (string.IsNullOrWhiteSpace(request.Name))
                return ToolResult.Failed("tool_dispatcher", "The tool request did not specify a tool name.");

            if (!_tools.TryGetValue(request.Name, out var tool))
                return ToolResult.Failed("tool_dispatcher", $"Tool '{request.Name}' is not available.");

            try
            {
                _logger.LogInformation("Execute tool " + tool.Name);
                var result = await tool.ExecuteAsync(request, cancellationToken);
                return result ?? ToolResult.Failed(request.Name, $"Tool '{request.Name}' returned no result.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool {ToolName} failed.", request.Name);
                return ToolResult.Failed(request.Name, ex.Message);
            }
        }

        private void OnAudioPlaybackCompleted(object? sender, EventArgs e)
        {
            _playbackCompletedConversationId = _conversationId;

            _logger.LogDebug(
                "Conversation {ConversationId} playback completed.",
                _conversationId);

            _ = TryCompleteConversationAsync(_conversationId);
        }

        private async Task TryCompleteConversationAsync(Guid conversationId)
        {
            if (conversationId == Guid.Empty ||
                _state != ConversationState.Idle ||
                _idleConversationId != conversationId ||
                _playbackCompletedConversationId != conversationId ||
                _completedEventConversationId == conversationId)
                return;

            _completedEventConversationId = conversationId;

            try
            {
                if (_audioManager.IsDucked)
                    await _audioManager.UnduckAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to restore radio volume after conversation {ConversationId}.",
                    conversationId);
            }

            _logger.LogInformation(
                "Conversation {ConversationId} completely finished: idle and playback complete.",
                conversationId);

            ConversationCompleted?.Invoke(
                this,
                new ConversationCompletedEventArgs(conversationId));
        }

        private void OnConversationStateIdle(Guid conversationId)
        {
            _idleConversationId = conversationId;
            _ = TryCompleteConversationAsync(conversationId);
        }

        public ValueTask DisposeAsync()
        {
            _audioManager.PlaybackCompleted -= OnAudioPlaybackCompleted;
            return _queue.DisposeAsync();
        }

        private void SetState(ConversationState state, Guid conversationId)
        {
            if (_state == state)
                return;

            var previous = _state;
            _state = state;

            _logger.LogDebug(
                "Conversation state changed from {Previous} to {Current} ({ConversationId}).",
                previous,
                state,
                conversationId);

            StateChanged?.Invoke(
                this,
                new ConversationStateChangedEventArgs(previous, state, conversationId));

            if (state == ConversationState.Idle)
                OnConversationStateIdle(conversationId);
        }

        private sealed record ConversationRequest(
            Guid ConversationId,
            string Text,
            bool IsAlarm,
            ToolRequest? InitialToolRequest);
    }
}