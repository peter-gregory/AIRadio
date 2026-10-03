using AIRadio.Server.Models.Radio;
using AIRadio.Server.Models.Stt;
using AIRadio.Server.Services.Radio;
using Microsoft.AspNetCore.Mvc;

namespace AIRadio.Server.Controllers
{
    [ApiController]
    [Route("api/radio")]
    public class RadioController : Controller
    {
        private readonly IRadioService _radioService;
        private readonly IRadioManagerService _radioManager;
        private readonly ILogger<RadioController> _logger;

        public RadioController(
            IRadioService radioService,
            IRadioManagerService radioManager,
            ILogger<RadioController> logger)
        {
            _radioService = radioService;
            _radioManager = radioManager;
            _logger = logger;
        }

        [Route("text")]
        [HttpPost]
        public async Task<ActionResult> Stt([FromBody] SttData prompt)
        {
            try
            {
                _logger.LogInformation("Received POST message " + prompt.Text);
                var result = await _radioService.Stt(prompt.Text!);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in get recordings");
                return new BadRequestObjectResult(ex.Message);
            }
        }

        [Route("stations")]
        [HttpGet]
        public ActionResult<IReadOnlyList<RadioStation>> GetStations()
        {
            return Ok(_radioManager.GetRadioState().RadioPlaylist);
        }

        [Route("playing")]
        [HttpGet]
        public ActionResult<PlayingRadioState> GetPlaying()
        {
            var state = _radioManager.GetRadioState();

            return Ok(new PlayingRadioState
            {
                StationId = state.IsPlaying ? state.RadioStation?.Id : null,
                Station = state.IsPlaying ? state.RadioStation : null,
                Playing = state.IsPlaying,
                Title = state.Title,
                Artist = state.Artist,
                Album = state.Album
            });
        }

        [Route("play")]
        [HttpPost]
        public async Task<ActionResult> Play([FromBody] PlayStationRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.StationId))
                {
                    await _radioManager.StopRadioAsync();
                    return Ok();
                }

                var state = _radioManager.GetRadioState();
                var index = state.RadioPlaylist
                    .Select((station, index) => new { station, index })
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.station.Id,
                            request.StationId,
                            StringComparison.OrdinalIgnoreCase));

                if (index is null)
                {
                    return NotFound($"Radio station '{request.StationId}' was not found.");
                }

                await _radioManager.PlayPlaylistStationAsync(index.index);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to play radio station {StationId}.", request.StationId);
                return BadRequest(ex.Message);
            }
        }

        [Route("volume")]
        [HttpGet]
        public ActionResult<int> GetVolume()
        {
            return Ok(_radioManager.GetRadioState().Volume);
        }

        [Route("volume")]
        [HttpPost]
        public async Task<ActionResult> SetVolume([FromBody] SetVolumeRequest request)
        {
            try
            {
                var volume = Math.Clamp(request.Volume, 0, 100);
                await _radioManager.SetVolumeAsync(volume);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to set radio volume.");
                return BadRequest(ex.Message);
            }
        }
    }

    public sealed class PlayStationRequest
    {
        public string? StationId { get; set; }
    }

    public sealed class SetVolumeRequest
    {
        public int Volume { get; set; }
    }

    public sealed class PlayingRadioState
    {
        public string? StationId { get; init; }

        public RadioStation? Station { get; init; }

        public bool Playing { get; init; }

        public string? Title { get; init; }

        public string? Artist { get; init; }

        public string? Album { get; init; }
    }
}
