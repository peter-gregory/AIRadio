using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Radio
{
    public interface IConversationService
    {
        bool IsWaitingForInput { get; }

        Task ProcessAsync(string text, CancellationToken cancellationToken = default);
        Task ProcessAndWaitAsync(string text, CancellationToken cancellationToken = default);
        Task CancelAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ConversationService : IConversationService, IAsyncDisposable
    {
        private readonly ILogger<ConversationService> _logger;
        private readonly IConversationLlamaClient _llama;
        private readonly IAudioManager _audioManager;
        private readonly IReadOnlyDictionary<string, ITool> _tools;
        private readonly AsyncWorkQueue<ConversationRequest> _queue;
        private ToolRequest? _pendingToolRequest;

        public bool IsWaitingForInput => _pendingToolRequest is not null;

        public ConversationService(ILogger<ConversationService> logger, IConversationLlamaClient llama, IAudioManager audioManager, IEnumerable<ITool> tools)
        {
            _logger = logger;
            _llama = llama;
            _audioManager = audioManager;
            ArgumentNullException.ThrowIfNull(tools);
            _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
            _queue = new AsyncWorkQueue<ConversationRequest>();
            _queue.Start(ProcessRequestAsync);
        }

        public Task ProcessAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queue.TryEnqueue(new ConversationRequest(text, null, false)))
                _logger.LogDebug("Conversation request rejected because the conversation queue is not accepting work.");
            return Task.CompletedTask;
        }

        public async Task ProcessAndWaitAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_queue.TryEnqueue(new ConversationRequest(text, completion, true)))
                throw new InvalidOperationException(
                    "Conversation request was rejected because the conversation queue is not accepting work.");

            await completion.Task.WaitAsync(cancellationToken);
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _queue.CancelAsync(CancellationToken.None);
            await _audioManager.CancelAsync(CancellationToken.None);
            _pendingToolRequest = null;
            _queue.Resume();
        }

        private async Task ProcessRequestAsync(ConversationRequest request, CancellationToken cancellationToken)
        {
            var conversationComplete = false;

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

                if (_pendingToolRequest is not null)
                {
                    var pendingRequest = ApplyPendingToolInput(request.Text);
                    _pendingToolRequest = null;
                    conversationComplete = await ExecutePendingToolAsync(pendingRequest, cancellationToken);
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

                        if (_tools.TryGetValue(selected.Name, out var selectedTool) && selectedTool.HasParameters)
                        {
                            // radioPlay has only optional arguments. For the saved-list
                            // phrases, there is nothing to extract, so avoid a second
                            // LLM round that can incorrectly turn command wording into
                            // a station name.
                            if (string.Equals(selected.Name, "radioPlay", StringComparison.OrdinalIgnoreCase) &&
                                IsSavedRadioPlaybackRequest(request.Text))
                            {
                                _logger.LogInformation(
                                    "Tool {ToolName} selected saved-station playback; skipping argument parsing.",
                                    selected.Name);

                                response.ToolRequests.Clear();
                                response.ToolRequests.Add(new ToolRequest
                                {
                                    Name = selected.Name,
                                    Arguments = new JObject()
                                });
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Tool {ToolName} requires argument parsing from the original utterance.",
                                    selected.Name);

                                response = await _llama.ContinueToolAsync(cancellationToken);

                                var parsedRequests = response.ToolRequests
                                    .Select(toolRequest => toolRequest.WithState(ToolRequestState.ArgumentParsing))
                                    .ToList();

                                response.ToolRequests.Clear();
                                response.ToolRequests.AddRange(parsedRequests);
                            }
                        }
                    }

                    conversationComplete = await ProcessLlamaResponseAsync(response, cancellationToken);
                }

                await _audioManager.EndUtteranceAsync(cancel: false, cancellationToken);

                // Alarm actions are independent commands, not interactive
                // conversation turns. Never allow a missing parameter from one
                // alarm action to consume the next scheduled action.
                if (request.Completion is not null && !conversationComplete)
                {
                    _pendingToolRequest = null;
                    conversationComplete = true;
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

                request.Completion?.TrySetResult(conversationComplete);
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

        public ValueTask DisposeAsync() => _queue.DisposeAsync();

        private sealed record ConversationRequest(
            string Text,
            TaskCompletionSource<bool>? Completion,
            bool IsAlarm);
    }
}