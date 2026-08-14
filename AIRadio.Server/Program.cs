using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Alarms;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.News;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Tools;
using AIRadio.Server.Services.Tts;
using AIRadio.Server.Services.Weather;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Host.UseSerilog();
builder.Services.AddControllers();

builder.Services.AddHttpClient("Llama");
builder.Services.AddHttpClient("RadioBrowser", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RadioAppliance/1.0");
});
builder.Services.AddHttpClient<INewsProvider, RssNewsProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Radio/1.0");
});

builder.Services.AddSingleton<INewsService, NewsService>();
builder.Services.AddSingleton<IRadioSearchClient, RadioSearchClient>();
builder.Services.AddSingleton<IConversationLlamaClient, ConversationLlamaClient>();
builder.Services.AddSingleton<ILlamaIntentClient, LlamaIntentClient>();
builder.Services.AddSingleton<IConversationService, ConversationService>();
builder.Services.AddSingleton<IIntentService, IntentService>();
builder.Services.AddSingleton<IRadioManagerService, RadioManagerService>();

builder.Services.AddSingleton<ISoundEffectManager, SoundEffectManager>();
builder.Services.AddSingleton<IMpvTransport, MpvTransport>();
builder.Services.AddSingleton<IRadioMetadataTranslator, RadioMetadataTranslator>();
builder.Services.AddSingleton<IMpvClient, MpvClient>();
builder.Services.AddSingleton<IAudioManager, AudioManager>();
builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
builder.Services.AddSingleton<ILocationStore, JsonLocationStore>();
builder.Services.AddSingleton<IMpvState, MpvState>();

builder.Services.AddHttpClient<IWeatherLocationResolver, OpenMeteoLocationResolver>();
builder.Services.AddHttpClient<IWeatherService, WeatherService>();
builder.Services.AddHttpClient<IPiperClient, PiperClient>();

builder.Services.AddSingleton<ITool, RadioTool>();
builder.Services.AddSingleton<ITool, LocationTool>();
builder.Services.AddSingleton<ITool, WeatherTool>();

builder.Services.AddHostedService<AlarmManagerService>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 50);
builder.Services.AddScoped<IRadioService, RadioService>();

var app = builder.Build();
var configuration = app.Services.GetRequiredService<IConfiguration>();

foreach (var directory in new[]
{
    configuration["Application:DataDirectory"],
    configuration["Application:SoundsDirectory"],
    configuration["Application:PromptsDirectory"],
    configuration["Application:ConfigDirectory"]
})
{
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
}

await app.Services.GetRequiredService<ISoundEffectManager>().InitializeAsync(
    configuration["Application:SoundsDirectory"]
        ?? throw new InvalidOperationException("Application:SoundsDirectory is not configured."));

await app.Services.GetRequiredService<ILlamaIntentClient>().InitializeAsync();
await app.Services.GetRequiredService<IConversationLlamaClient>().InitializeAsync();

app.UseDefaultFiles();
app.MapStaticAssets();
app.MapControllers();
app.MapFallbackToFile("/index.html");

await app.RunAsync();
