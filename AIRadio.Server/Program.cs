using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.AI;
using AIRadio.Server.Services.Audio;
using AIRadio.Server.Services.Location;
using AIRadio.Server.Services.Mpv;
using AIRadio.Server.Services.News;
using AIRadio.Server.Services.Radio;
using AIRadio.Server.Services.Sounds;
using AIRadio.Server.Services.Sounds.Radio.Audio;
using AIRadio.Server.Services.Tools;
using AIRadio.Server.Services.Tts;
using AIRadio.Server.Services.Weather;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.

builder.Services.AddControllers();

builder.Services.AddHttpClient("Llama");

builder.Services.AddHttpClient(
    "RadioBrowser",
    client =>
    {
        client.Timeout =
            TimeSpan.FromSeconds(10);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "RadioAppliance/1.0");
    });

builder.Services.AddHttpClient<INewsProvider, RssNewsProvider>(
    client =>
    {
        client.Timeout =
            TimeSpan.FromSeconds(10);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Radio/1.0");
    });

builder.Services.AddSingleton<INewsService, NewsService>();

builder.Services.AddSingleton<
    IRadioSearchClient,
    RadioSearchClient>();

builder.Services.AddSingleton<
    ITool,
    RadioTool>();
builder.Services.AddHostedService<RadioEngineService>();

builder.Services.AddSingleton<ISoundEffectManager, SoundEffectManager>(); 
builder.Services.AddSingleton<IConversationLlamaClient, ConversationLlamaClient>();
builder.Services.AddSingleton<IRadioMetadataTranslator, RadioMetadataTranslator>();
builder.Services.AddSingleton<IMpvTransport, MpvTransport>();
builder.Services.AddSingleton<IRadioMetadataTranslator, RadioMetadataTranslator>();
builder.Services.AddSingleton<IMpvClient, MpvClient>();
builder.Services.AddSingleton<IAudioManager, AudioManager>();
builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
builder.Services.AddSingleton<ILocationStore, JsonLocationStore>();
builder.Services.AddSingleton<IMpvState, MpvState>();
builder.Services.AddHttpClient<IWeatherLocationResolver, OpenMeteoLocationResolver>();
builder.Services.AddHttpClient<IWeatherService, WeatherService>();

builder.Services.AddSingleton<ITool, RadioTool>();
builder.Services.AddSingleton<ITool, LocationTool>();
builder.Services.AddSingleton<ITool, WeatherTool>();

builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 50;
});

builder.Services.AddHttpClient<IPiperClient, PiperClient>(); 
builder.Services.AddScoped<IRadioService, RadioService>();

var app = builder.Build();

var configuration = app.Services.GetRequiredService<IConfiguration>();

var directories = new[] { 
    configuration["Application:DataDirectory"], 
    configuration["Application:SoundsDirectory"], 
    configuration["Application:PromptsDirectory"], 
    configuration["Application:ConfigDirectory"] 
}; 

foreach (var directory in directories) 
{
    if (string.IsNullOrWhiteSpace(directory)) 
    { 
        continue; 
    } 
    Directory.CreateDirectory(directory); 
}

app.UseDefaultFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.

//app.UseHttpsRedirection();

//app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
