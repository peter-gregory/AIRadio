using AIRadio.Server.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIRadio.Server.Services.Radio
{
    public interface IRadioService
    {
        Task<ActionResult> Stt(string prompt);
    }

    public class RadioService : IRadioService
    {
        IRadioEngineService _engine;

        public RadioService(IRadioEngineService engine)
        {
            _engine = engine;
        }

        public async Task<ActionResult> Stt(string prompt)
        {
            await _engine.ProcessSpeechAsync(prompt);
            return new OkResult();
        }
    }
}
