using Microsoft.AspNetCore.Mvc;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioService
    {
        Task<ActionResult> Stt(string prompt);
    }

    public sealed class RadioService : IRadioService
    {
        private readonly IRadioManagerService _radioManager;

        public RadioService(IRadioManagerService radioManager)
        {
            _radioManager = radioManager;
        }

        public async Task<ActionResult> Stt(string prompt)
        {
            await _radioManager.ProcessSpeechAsync(prompt);
            return new OkResult();
        }
    }
}
