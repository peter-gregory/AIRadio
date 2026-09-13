using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Alarms;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Events;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.Network;
using AIRadio.Server.Services.News;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Report;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Time;
using AIRadio.Server.Services.Tools;
using AIRadio.Server.Services.Tts;
using AIRadio.Server.Services.Weather;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "Config", "IntentRegex.json"), optional: false, reloadOnChange: true);
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

Log.Logger.Information("Startup: add controllers");

builder.Services.AddControllers();

Log.Logger.Information("Startup: add LlamaHttpClient");

builder.Services.AddHttpClient<ILlamaHttpClient, LlamaHttpClient>();

Log.Logger.Information("Startup: add RadioBrowser ");

builder.Services.AddHttpClient("RadioBrowser", client => { client.Timeout = TimeSpan.FromSeconds(10); client.DefaultRequestHeaders.UserAgent.ParseAdd("RadioAppliance/1.0"); });

Log.Logger.Information("Startup: add RssNewsProvider ");

builder.Services.AddHttpClient<INewsProvider, RssNewsProvider>(client => { client.Timeout = TimeSpan.FromSeconds(10); client.DefaultRequestHeaders.UserAgent.ParseAdd("Radio/1.0"); });

Log.Logger.Information("Startup: add NewsService ");

builder.Services.AddSingleton<INewsService, NewsService>();

Log.Information("Startup: add RadioSearchClient ");

builder.Services.AddSingleton<IRadioSearchClient, RadioSearchClient>();

Log.Logger.Information("Startup: add ConversationLlamaClient ");

builder.Services.AddSingleton<IConversationLlamaClient, ConversationLlamaClient>();

Log.Logger.Information("Startup: add RegexIntentParser ");

builder.Services.AddSingleton<IRegexIntentParser, RegexIntentParser>();

Log.Logger.Information("Startup: add ConversationService ");

builder.Services.AddSingleton<IConversationService, ConversationService>();

Log.Logger.Information("Startup: add IntentService ");

builder.Services.AddSingleton<IIntentService, IntentService>();

Log.Logger.Information("Startup: add RadioManagerService ");

builder.Services.AddSingleton<IRadioManagerService, RadioManagerService>();

Log.Logger.Information("Startup: add SoundEffectManager ");

builder.Services.AddSingleton<ISoundEffectManager, SoundEffectManager>();

Log.Logger.Information("Startup: add PipeWireNativeClient ");

builder.Services.AddSingleton<IPipeWireNativeClient, PipeWireNativeClient>();

Log.Logger.Information("Startup: add PipeWireAudioClient ");

builder.Services.AddSingleton<IPipeWireAudioClient, PipeWireAudioClient>();

Log.Logger.Information("Startup: add MpvTransport ");

builder.Services.AddSingleton<IMpvTransport, MpvTransport>();

Log.Logger.Information("Startup: add RadioMetadataTranslator ");

builder.Services.AddSingleton<IRadioMetadataTranslator, RadioMetadataTranslator>();

Log.Logger.Information("Startup: add MpvClient ");

builder.Services.AddSingleton<IMpvClient, MpvClient>();

Log.Logger.Information("Startup: add MpvManager ");

builder.Services.AddSingleton<IMpvManager, MpvManager>();

Log.Information("Startup: add AudioManager ");

builder.Services.AddSingleton<IAudioManager, AudioManager>();

Log.Logger.Information("Startup: add ToolExecutor ");

builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();

Log.Logger.Information("Startup: add JsonLocationStore ");

builder.Services.AddSingleton<ILocationStore, JsonLocationStore>();

Log.Logger.Information("Startup: add MpvState ");

builder.Services.AddSingleton<IMpvState, MpvState>();

Log.Logger.Information("Startup: add WifiManager ");

builder.Services.AddSingleton<IWifiManager, WifiManager>();

Log.Logger.Information("Startup: add OpenMeteoLocationResolver ");

builder.Services.AddHttpClient<IWeatherLocationResolver, OpenMeteoLocationResolver>();

Log.Logger.Information("Startup: add WeatherService ");

builder.Services.AddHttpClient<IWeatherService, WeatherService>();

Log.Logger.Information("Startup: add PiperAudioCache ");

builder.Services.AddSingleton<PiperAudioCache>();

Log.Logger.Information("Startup: add PiperClient ");

builder.Services.AddHttpClient<IPiperClient, PiperClient>();

Log.Logger.Information("Startup: add RadioTool ");

builder.Services.AddSingleton<ITool, RadioTool>();

Log.Logger.Information("Startup: add LocationTool ");

builder.Services.AddSingleton<ITool, LocationTool>();

Log.Logger.Information("Startup: add WeatherTool ");

builder.Services.AddSingleton<ITool, WeatherTool>();

Log.Logger.Information("Startup: add NewsTool ");

builder.Services.AddSingleton<ITool, NewsTool>();

Log.Logger.Information("Startup: add TimeTool ");

builder.Services.AddSingleton<ITool, TimeTool>();

Log.Logger.Information("Startup: add EventTool ");

builder.Services.AddSingleton<ITool, EventTool>();

Log.Logger.Information("Startup: add ReportTool ");

builder.Services.AddSingleton<ITool, ReportTool>();

Log.Logger.Information("Startup: add WifiTool ");

builder.Services.AddSingleton<ITool, WifiTool>();

Log.Logger.Information("Startup: add LocationService ");

builder.Services.AddSingleton<ILocationService, LocationService>();

Log.Logger.Information("Startup: add AlarmManagerService ");

builder.Services.AddSingleton<AlarmManagerService>();
builder.Services.AddSingleton<IAlarmManagerService>(sp => sp.GetRequiredService<AlarmManagerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmManagerService>());

Log.Logger.Information("Startup: add RadioService ");

builder.Services.AddScoped<IRadioService, RadioService>();

Log.Logger.Information("Startup: configure web ");

var contentRoot = builder.Environment.ContentRootPath;
var configuredSoundsDirectory = builder.Configuration["Application:SoundsDirectory"] ?? throw new InvalidOperationException("Application:SoundsDirectory is not configured.");
var configuredPromptsDirectory = builder.Configuration["Application:PromptsDirectory"] ?? throw new InvalidOperationException("Application:PromptsDirectory is not configured.");
builder.Configuration["Application:SoundsDirectory"] = Path.GetFullPath(Path.Combine(contentRoot, configuredSoundsDirectory));
builder.Configuration["Application:PromptsDirectory"] = Path.GetFullPath(Path.Combine(contentRoot, configuredPromptsDirectory));

Log.Logger.Information("Startup: creating application host");

var app = builder.Build();
var configuration = app.Services.GetRequiredService<IConfiguration>();
foreach (var directory in new[] { configuration["Application:DataDirectory"], configuration["Application:ConfigDirectory"] })
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

var soundsDirectory = configuration["Application:SoundsDirectory"] ?? throw new InvalidOperationException("Application:SoundsDirectory is not configured.");
var promptsDirectory = configuration["Application:PromptsDirectory"] ?? throw new InvalidOperationException("Application:PromptsDirectory is not configured.");
if (!Directory.Exists(soundsDirectory)) throw new DirectoryNotFoundException($"Sound effects directory was not found: {soundsDirectory}");
if (!Directory.Exists(promptsDirectory)) throw new DirectoryNotFoundException($"Prompt directory was not found: {promptsDirectory}");

Log.Logger.Information("Startup: creating sound effect manager");

await app.Services.GetRequiredService<ISoundEffectManager>().InitializeAsync(soundsDirectory);

Log.Logger.Information("Startup: creating pipewire audio client");
await app.Services.GetRequiredService<IPipeWireAudioClient>().InitializeAsync();

Log.Logger.Information("Startup: creating conversation client");
await app.Services.GetRequiredService<IConversationLlamaClient>().InitializeAsync();

Log.Logger.Information("Startup: finish web config");

app.UseDefaultFiles();
app.MapStaticAssets();

Log.Logger.Information("Startup: map controllers");

app.MapControllers();

app.MapFallbackToFile("/index.html");

Log.Logger.Information("Startup: run the app");

await app.RunAsync();
