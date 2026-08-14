using AIRadio.Server.Models.LLama;
using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Mpv;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioEngineService
    {
        Task ProcessSpeechAsync(
            string userMessage,
            CancellationToken cancellationToken = default);

        Task RenderAlarmAsync(
            string content,
            CancellationToken cancellationToken = default);

        Task PlayStationAsync(
            RadioStation station,
            CancellationToken cancellationToken = default);

        Task PlayPlaylistStationAsync(
            int index,
            CancellationToken cancellationToken = default);

        Task PlayNextStationAsync(
            CancellationToken cancellationToken = default);

        Task PlayPreviousStationAsync(
            CancellationToken cancellationToken = default);

        Task StopRadioAsync(
            CancellationToken cancellationToken = default);

        void SetRadioPlaylist(
            IReadOnlyList<RadioStation> stations,
            RadioPlaylistSource source);

        IMpvState GetRadioState();
    }

    public sealed class RadioEngineService : BackgroundService, IRadioEngineService
    {
        private readonly ILogger<RadioEngineService> _logger;

        private readonly IConversationLlamaClient _llama;
        private readonly ILlamaIntentClient _intentClient;
        private readonly IMpvManager _mpvManager;
        private readonly IMpvState _mpvState;
        private readonly IAudioManager _audioManager;

        private readonly IReadOnlyDictionary<string, ITool> _tools;

        public RadioEngineService(
            ILogger<RadioEngineService> logger,
            IConversationLlamaClient llama,
            ILlamaIntentClient intentClient,
            IMpvManager mpvManager,
            IMpvState mpvState,
            IAudioManager audioManager,
            IEnumerable<ITool> tools)
        {
            _logger = logger;

            _llama = llama;
            _intentClient = intentClient;

            _mpvManager = mpvManager;
            _mpvState = mpvState;
            _audioManager = audioManager;

            _tools = tools.ToDictionary(
                tool => tool.Name,
                StringComparer.OrdinalIgnoreCase);
        }

        // ============================================================
        // BACKGROUND SERVICE
        // ============================================================

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "RadioEngineService starting.");

            try
            {
                await InitializeAsync(
                    stoppingToken);

                await RunAsync(
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "RadioEngineService terminated unexpectedly.");
            }
            finally
            {
                await ShutdownAsync();
            }

            _logger.LogInformation(
                "RadioEngineService stopped.");
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        private async Task InitializeAsync(
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Initializing RadioEngineService.");

            await _intentClient.InitializeAsync(
                cancellationToken);

            await _llama.InitializeAsync(
                cancellationToken);

            _logger.LogInformation(
                "RadioEngineService initialized.");
        }

        // ============================================================
        // MAIN LOOP
        // ============================================================

        private async Task RunAsync(
            CancellationToken cancellationToken)
        {
            /*
             * The STT/event input mechanism is responsible for feeding
             * recognized speech into the engine.
             *
             * The actual input queue can be connected here as the
             * current STT service is finalized.
             */

            await Task.Delay(
                Timeout.Infinite,
                cancellationToken);
        }

        // ============================================================
        // PROCESS USER SPEECH
        // ============================================================

        public async Task ProcessSpeechAsync(
            string userMessage,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                userMessage);

            _logger.LogInformation(
                "Processing user speech: {UserMessage}",
                userMessage);

            if (await HandleBargeInAsync(
                    userMessage,
                    cancellationToken))
            {
                return;
            }

            LlamaResponse response;

            if (_llama.IsInitialized)
            {
                response =
                    await _llama.ContinueAsync(
                        userMessage,
                        cancellationToken);
            }
            else
            {
                response =
                    await _llama.StartConversationAsync(
                        userMessage,
                        cancellationToken);
            }

            await ProcessLlamaResponseAsync(
                response,
                cancellationToken);
        }

        // ============================================================
        // PROCESS ALARM
        // ============================================================

        public async Task RenderAlarmAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                content);

            _logger.LogInformation(
                "Rendering alarm: {Content}",
                content);

            /*
             * An alarm is equivalent to a user utterance once it reaches
             * the RadioEngine.
             *
             * Unlike user speech:
             *
             *  - It does not require keyword activation.
             *  - It does not go through the barge-in intent detector.
             *  - It always interrupts the current operation.
             */

            try
            {
                /*
                 * Cancel any Llama operation currently in progress.
                 */
                await _llama.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to cancel existing Llama operation for alarm.");
            }

            /*
             * Stop currently spoken audio.
             *
             * We intentionally do not stop MPV/radio here. The alarm
             * should be allowed to interrupt speech while the AudioManager
             * decides how the alarm audio interacts with the radio.
             */
            try
            {
                await _audioManager.StopSpeechAsync(
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to stop speech before alarm.");
            }

            LlamaResponse response;

            if (_llama.IsInitialized)
            {
                response =
                    await _llama.ContinueAsync(
                        content,
                        cancellationToken);
            }
            else
            {
                response =
                    await _llama.StartConversationAsync(
                        content,
                        cancellationToken);
            }

            await ProcessLlamaResponseAsync(
                response,
                cancellationToken);
        }

        // ============================================================
        // BARGE-IN
        // ============================================================

        private async Task<bool> HandleBargeInAsync(
            string userMessage,
            CancellationToken cancellationToken)
        {
            var result =
                await _intentClient.CheckBargeInAsync(
                    userMessage,
                    cancellationToken);

            if (!result.IsBargeIn)
            {
                return false;
            }

            _logger.LogInformation(
                "Barge-in detected. " +
                "Confidence={Confidence}, " +
                "Command={Command}, " +
                "Reason={Reason}.",
                result.Confidence,
                result.Command,
                result.Reason);

            var command =
                result.Command?.Trim();

            switch (command?.ToLowerInvariant())
            {
                case "stop":
                case "cancel":
                case "be quiet":
                case "stop talking":

                    await _llama.CancelAsync();

                    await _audioManager.StopSpeechAsync(
                        cancellationToken);

                    return true;

                case "stop radio":
                case "turn off radio":
                case "turn off the radio":

                    await _llama.CancelAsync();

                    await _audioManager.StopSpeechAsync(
                        cancellationToken);

                    await _mpvManager.StopAsync(
                        cancellationToken);

                    return true;

                default:

                    /*
                     * The intent client identified this as a barge-in,
                     * but did not return a command that this service
                     * handles directly.
                     */
                    _logger.LogDebug(
                        "Unhandled barge-in command: {Command}.",
                        result.Command);

                    return true;
            }
        }

        // ============================================================
        // LLAMA RESPONSE
        // ============================================================

        private async Task ProcessLlamaResponseAsync(
            LlamaResponse response,
            CancellationToken cancellationToken)
        {
            /*
             * Sound events and spoken text are handled independently.
             */
            if (response.HasSoundEvents)
            {
                await PlaySoundEventsAsync(
                    response.SoundEvents,
                    cancellationToken);
            }

            if (response.HasSpeech)
            {
                await _audioManager.PlaySpeechAsync(
                    response.SpokenText,
                    cancellationToken);
            }

            if (response.HasToolRequests)
            {
                await ExecuteToolsAsync(
                    response.ToolRequests,
                    cancellationToken);
            }
        }

        // ============================================================
        // TOOL EXECUTION
        // ============================================================

        private async Task ExecuteToolsAsync(
            IEnumerable<ToolRequest> toolRequests,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                toolRequests);

            var results =
                new List<ToolResult>();

            foreach (var request in toolRequests)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    _logger.LogWarning(
                        "Llama returned a tool request with no tool name.");

                    continue;
                }

                if (!_tools.TryGetValue(
                        request.Name,
                        out var tool))
                {
                    _logger.LogWarning(
                        "Llama requested unknown tool {ToolName}.",
                        request.Name);

                    results.Add(
                        ToolResult.Failed(
                            request.Name,
                            $"Tool '{request.Name}' is not available."));

                    continue;
                }

                try
                {
                    _logger.LogDebug(
                        "Executing tool {ToolName}.",
                        request.Name);

                    var result =
                        await tool.ExecuteAsync(
                            request,
                            cancellationToken);

                    if (result is null)
                    {
                        _logger.LogWarning(
                            "Tool {ToolName} returned a null result.",
                            request.Name);

                        results.Add(
                            ToolResult.Failed(
                                request.Name,
                                $"Tool '{request.Name}' returned no result."));

                        continue;
                    }

                    results.Add(result);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Tool {ToolName} failed.",
                        request.Name);

                    results.Add(
                        ToolResult.Failed(
                            request.Name,
                            ex.Message));
                }
            }

            if (results.Count == 0)
            {
                return;
            }

            /*
             * The tool results are added to the existing Llama
             * conversation and Llama generates the next response.
             */
            var response =
                await _llama.ContinueAsync(
                    results,
                    cancellationToken);

            await ProcessLlamaResponseAsync(
                response,
                cancellationToken);
        }

        // ============================================================
        // SOUND EVENTS
        // ============================================================

        private async Task PlaySoundEventsAsync(
            IEnumerable<string> soundEvents,
            CancellationToken cancellationToken)
        {
            foreach (var soundEvent in soundEvents)
            {
                if (string.IsNullOrWhiteSpace(
                        soundEvent))
                {
                    continue;
                }

                await _audioManager.PlaySoundAsync(
                    soundEvent,
                    cancellationToken);
            }
        }

        // ============================================================
        // RADIO OPERATIONS
        // ============================================================

        public Task PlayStationAsync(
            RadioStation station,
            CancellationToken cancellationToken = default)
        {
            return _mpvManager.PlayAsync(
                station,
                cancellationToken);
        }

        public Task PlayPlaylistStationAsync(
            int index,
            CancellationToken cancellationToken = default)
        {
            return _mpvManager.PlayPlaylistStationAsync(
                index,
                cancellationToken);
        }

        public Task PlayNextStationAsync(
            CancellationToken cancellationToken = default)
        {
            return _mpvManager.PlayNextRadioStationAsync(
                cancellationToken);
        }

        public Task PlayPreviousStationAsync(
            CancellationToken cancellationToken = default)
        {
            return _mpvManager.PlayPreviousRadioStationAsync(
                cancellationToken);
        }

        public Task StopRadioAsync(
            CancellationToken cancellationToken = default)
        {
            return _mpvManager.StopAsync(
                cancellationToken);
        }

        public void SetRadioPlaylist(
            IReadOnlyList<RadioStation> stations,
            RadioPlaylistSource source)
        {
            _mpvManager.SetRadioPlaylist(
                stations,
                source);
        }

        // ============================================================
        // RADIO STATE
        // ============================================================

        public IMpvState GetRadioState()
        {
            return _mpvState;
        }

        // ============================================================
        // SHUTDOWN
        // ============================================================

        private async Task ShutdownAsync()
        {
            try
            {
                await _llama.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to cancel Llama request during shutdown.");
            }

            try
            {
                await _audioManager.StopSpeechAsync(
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to stop speech during shutdown.");
            }
        }
    }

    public enum ConversationState
    {
        Idle,
        Starting,
        Continue,
        Processing,
        Error
    }

    public sealed record SttEntry(
        long Sequence,
        string Text,
        DateTimeOffset ReceivedAt);

    public sealed record AudioStateSnapshot(
        bool RadioPlaying,
        bool SpeechPlaying);

    public sealed record RadioEngineState(
        RadioConversationState ConversationState,
        ActiveOperation ActiveOperation,
        AudioStateSnapshot AudioState,
        bool HasActiveRequest);

    public enum RadioConversationState
    {
        Starting,
        Continue
    }

    public enum ActiveOperation
    {
        Idle,
        Thinking,
        ToolExecution,
        Speaking,
        Radio
    }

    public enum RadioAction
    {
        None,
        Duck,
        Stop,
        Replace,
        Start
    }

    public enum SpeechAction
    {
        None,
        Stop,
        Replace,
        Start
    }

    public sealed class BargeInResult
    {
        [JsonProperty("isBargeIn")]
        public bool IsBargeIn { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("reason")]
        public string? Reason { get; set; }

        [JsonProperty("command")]
        public string? Command { get; set; }

        [JsonProperty("text")]
        public string? Text { get; set; }
    }

    public enum CancelTarget
    {
        None,
        Speech,
        Radio,
        All
    }

    public static class LlamaResponseParser
    {
        private static readonly Regex SoundTagRegex =
            new(
                @"\{sound:(?<sound>[^}]+)\}",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        private static readonly Regex ToolJsonRegex =
            new(
                @"\{\s*""(?:tool|name)""\s*:\s*""[^""]+""[\s\S]*?\}",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex =
            new(
                @"\s+",
                RegexOptions.Compiled);

        public static LlamaResponse Parse(
            string response)
        {
            ArgumentNullException.ThrowIfNull(
                response);

            var result =
                new LlamaResponse();

            if (string.IsNullOrWhiteSpace(response))
                return result;

            var remaining =
                response.Trim();

            ParseSoundEvents(
                ref remaining,
                result);

            ParseToolRequests(
                ref remaining,
                result);

            result.SpokenText =
                NormalizeSpokenText(
                    remaining);

            return result;
        }

        private static void ParseSoundEvents(
            ref string text,
            LlamaResponse result)
        {
            var matches =
                SoundTagRegex.Matches(text);

            foreach (Match match in matches)
            {
                var sound =
                    match.Groups["sound"]
                        .Value
                        .Trim();

                if (!string.IsNullOrWhiteSpace(sound))
                {
                    result.SoundEvents.Add(
                        sound);
                }
            }

            text =
                SoundTagRegex.Replace(
                    text,
                    string.Empty);
        }

        private static void ParseToolRequests(
            ref string text,
            LlamaResponse result)
        {
            var matches =
                ToolJsonRegex.Matches(text);

            foreach (Match match in matches)
            {
                var json =
                    match.Value;

                try
                {
                    var root =
                        JObject.Parse(json);

                    var toolName =
                        root["name"]?.Value<string>()
                        ?? root["tool"]?.Value<string>();

                    if (string.IsNullOrWhiteSpace(
                            toolName))
                    {
                        continue;
                    }

                    var arguments =
                        root["arguments"] as JObject
                        ?? new JObject();

                    result.ToolRequests.Add(
                        new ToolRequest
                        {
                            Name = toolName,
                            Arguments = arguments
                        });
                }
                catch (JsonException)
                {
                    /*
                     * Ignore malformed tool objects.
                     *
                     * The text remains available to the model's
                     * spoken response rather than causing the
                     * entire response to fail.
                     */
                }
            }

            text =
                ToolJsonRegex.Replace(
                    text,
                    string.Empty);
        }

        private static string NormalizeSpokenText(
            string text)
        {
            text =
                text.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text =
                WhitespaceRegex.Replace(
                    text,
                    " ");

            return text.Trim();
        }
    }
}
