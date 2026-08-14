using AIRadio.Server.Models.Stt;
using AIRadio.Server.Services.Radio;
using Microsoft.AspNetCore.Mvc;

namespace AIRadio.Server.Controllers
{
    [ApiController]
    [Route("api/radio")]
    public class RadioController : Controller
    {
        private IRadioService _radioService;
        private ILogger<RadioController> _logger;

        public RadioController(IRadioService radioService, ILogger<RadioController> logger)
        {
            _radioService = radioService;
            _logger = logger;
        }

        [Route("stt")]
        [HttpPost]
        public async Task<ActionResult> Stt([FromBody] SttData prompt)
        {
            try
            {
                var result = await _radioService.Stt(prompt.Text!);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in get recordings");
                return new BadRequestObjectResult(ex.Message);
            }
        }
    }
}
