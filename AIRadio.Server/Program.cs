using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Alarms;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.Network;
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

builder.Services.AddHttpClient<ILlamaHttpClient, LlamaHttpClient>();
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
builder.Services.AddSingleton<IPipeWireNativeClient, PipeWireNativeClient>();
builder.Services.AddSingleton<IPipeWireAudioClient, PipeWireAudioClient>();
builder.Services.AddSingleton<IMpvTransport, MpvTransport>();
builder.Services.AddSingleton<IRadioMetadataTranslator, RadioMetadataTranslator>();
builder.Services.AddSingleton<IMpvClient, MpvClient>();
builder.Services.AddSingleton<IAudioManager, AudioManager>();
builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
builder.Services.AddSingleton<ILocationStore, JsonLocationStore>();
builder.Services.AddSingleton<IMpvState, MpvState>();
builder.Services.AddSingleton<IWifiManager, WifiManager>();

builder.Services.AddHttpClient<IWeatherLocationResolver, OpenMeteoLocationResolver>();
builder.Services.AddHttpClient<IWeatherService, WeatherService>();
builder.Services.AddHttpClient<IPiperClient, PiperClient>();

builder.Services.AddSingleton<ITool, RadioTool>();
builder.Services.AddSingleton<ITool, LocationTool>();
builder.Services.AddSingleton<ITool, WeatherTool>();
builder.Services.AddSingleton<ITool, WifiTool>();

builder.Services.AddSingleton<AlarmManagerService>();
builder.Services.AddSingleton<IAlarmManagerService>(sp =>
    sp.GetRequiredService<AlarmManagerService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<AlarmManagerService>());

builder.Services.AddMemoryCache(options => options.SizeLimit = 50);
builder.Services.AddScoped<IRadioService, RadioService>();

var contentRoot = builder.Environment.ContentRootPath;

var configuredSoundsDirectory =
    builder.Configuration["Application:SoundsDirectory"]
    ?? throw new InvalidOperationException(
        "Application:SoundsDirectory is not configured.");

var configuredPromptsDirectory =
    builder.Configuration["Application:PromptsDirectory"]
    ?? throw new InvalidOperationException(
        "Application:PromptsDirectory is not configured.");

builder.Configuration["Application:SoundsDirectory"] =
    Path.GetFullPath(
        Path.Combine(
            contentRoot,
            configuredSoundsDirectory));

builder.Configuration["Application:PromptsDirectory"] =
    Path.GetFullPath(
        Path.Combine(
            contentRoot,
            configuredPromptsDirectory));

var app = builder.Build();
var configuration = app.Services.GetRequiredService<IConfiguration>();

foreach (var directory in new[]
{
    configuration["Application:DataDirectory"],
    configuration["Application:ConfigDirectory"]
})
{
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
}

var soundsDirectory =
    configuration["Application:SoundsDirectory"]
    ?? throw new InvalidOperationException(
        "Application:SoundsDirectory is not configured.");

var promptsDirectory =
    configuration["Application:PromptsDirectory"]
    ?? throw new InvalidOperationException(
        "Application:PromptsDirectory is not configured.");

if (!Directory.Exists(soundsDirectory))
{
    throw new DirectoryNotFoundException(
        $"Sound effects directory was not found: {soundsDirectory}");
}

if (!Directory.Exists(promptsDirectory))
{
    throw new DirectoryNotFoundException(
        $"Prompt directory was not found: {promptsDirectory}");
}

await app.Services.GetRequiredService<ISoundEffectManager>().InitializeAsync(
    soundsDirectory);

await app.Services.GetRequiredService<IPipeWireAudioClient>().InitializeAsync();
await app.Services.GetRequiredService<ILlamaIntentClient>().InitializeAsync();
await app.Services.GetRequiredService<IConversationLlamaClient>().InitializeAsync();

app.UseDefaultFiles();
app.MapStaticAssets();
app.MapControllers();
app.MapFallbackToFile("/index.html");

await app.RunAsync();
