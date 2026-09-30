using AIRadio.Server.Models.Location;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Location;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Weather;

public sealed class WeatherTool : ITool
{
    private readonly IWeatherService _weatherService;
    private readonly ILocationService _locationService;
    private readonly IWeatherLocationResolver _locationResolver;
    private readonly ILogger<WeatherTool> _logger;

    public WeatherTool(
        IWeatherService weatherService,
        ILocationService locationService,
        IWeatherLocationResolver locationResolver,
        ILogger<WeatherTool> logger)
    {
        _weatherService = weatherService;
        _locationService = locationService;
        _locationResolver = locationResolver;
        _logger = logger;
    }

    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:weather-current,City=<optional>}";
    public string Name => "weather-current";
    public string Intent => "Get the current weather conditions.";

    public string GetLlmInstructions() => """
WEATHER-CURRENT
Get current weather conditions.
Parameters:
- City: optional city and state, ZIP/postal code, or recognizable place.
- If City is omitted, use the radio's persistent current location.
Use for what the weather is like now. Future weather belongs to forecast.
Examples:
"What's the weather?" -> {tool:weather-current}
"What's the weather in Miami?" -> {tool:weather-current,City="Miami, Florida"}
"Give me a weather report" -> {tool:weather-current}
""";

    public string GetLlmResponseInstructions() => string.Empty;

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Start execute tool weather-current");
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var cityName = request.GetString("City");
        RadioLocation? locationForWeather;

        if (string.IsNullOrWhiteSpace(cityName))
        {
            locationForWeather = _locationService.GetCurrentLocation();

            if (locationForWeather is null)
            {
                var pendingRequest = new ToolRequest
                {
                    Name = Name,
                    Arguments = new JObject { ["City"] = ToolRequest.RequiredValue },
                    State = ToolRequestState.AwaitingCurrentLocation
                };

                return ToolResult.MissingParameter(
                    Name,
                    "I need to know where we are. What is the name of the city and state where we are currently located?",
                    pendingRequest);
            }

            _logger.LogInformation("Using persistent radio location {Location} for weather.", locationForWeather.Raw);
        }
        else
        {
            var normalizedCityName = NormalizeCityState(cityName.Trim());

            _logger.LogInformation(
                "Normalized weather location '{Location}' to '{NormalizedLocation}'.",
                cityName,
                normalizedCityName);

            var requestedLocation =
                _locationService.BuildLocation(normalizedCityName);

            if (request.State == ToolRequestState.AwaitingCurrentLocation)
            {
                // Validate the user's answer with the weather geocoder before
                // allowing it to become the radio's persistent location.
                var lookup =
                    await _locationResolver.ResolveAsync(
                        requestedLocation,
                        cancellationToken);

                if (lookup is null)
                {
                    _logger.LogInformation(
                        "Current location response '{Location}' could not be validated by the location lookup.",
                        cityName);

                    return ToolResult.Failed(
                        Name,
                        $"Unable to determine the location '{cityName}'.",
                        $"{{sound:weather-static}} I'm sorry, I couldn't determine the weather location for {cityName}.",
                        complete: true);
                }

                locationForWeather =
                    await _locationService.UpdateCurrentLocationAsync(
                        requestedLocation,
                        cancellationToken);

                // Carry the normalized location into the continuation request.
                // The user's original answer can be an unqualified phrase such
                // as "PALM CITY FLORIDA", which is intentionally normalized for
                // validation. The next tool state must use that normalized value
                // rather than resolving the original raw phrase again.
                request = new ToolRequest
                {
                    Name = Name,
                    Arguments = new JObject { ["City"] = normalizedCityName },
                    State = ToolRequestState.AwaitingCurrentLocation
                };
            }
            else
            {
                locationForWeather =
                    await _locationService.ResolveLocationAsync(
                        cityName.Trim(),
                        cancellationToken);
            }

            if (locationForWeather is null)
                return ToolResult.Failed(
                    Name,
                    $"Unable to determine the location '{cityName}'.",
                    $"{{sound:weather-static}} I'm sorry, I couldn't determine the weather location for {cityName}.",
                    complete: true);
        }

        if (request.State is ToolRequestState.Initial or ToolRequestState.AwaitingCurrentLocation)
        {
            var displayLocation = GetDisplayLocation(locationForWeather);
            return ToolResult.Preamble(
                Name,
                $"Here's your current weather conditions for {displayLocation}. {{sound:weather-intro}} {{sound-loop:loop-thinking}}",
                request.WithState(ToolRequestState.PreambleComplete));
        }

        try
        {
            var weather = await _weatherService.GetWeatherAsync(locationForWeather, cancellationToken);
            if (weather.Current is null)
                return ToolResult.Failed(
                    Name,
                    "Current weather data was not returned.",
                    "{sound:weather-static} I'm sorry, I couldn't retrieve the current weather right now.",
                    complete: true);

            var report = WeatherReportFormatter.Format(weather);

            return ToolResult.Successful(
                Name,
                "Current weather retrieved successfully.",
                data: null,
                exactPrompt: report,
                complete: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Weather request failed for {Location}.",
                GetDisplayLocation(locationForWeather));

            return ToolResult.Failed(
                Name,
                "Unable to retrieve current weather.",
                "{sound:weather-static} I'm sorry, I couldn't retrieve the current weather right now.",
                complete: true);
        }
    }

    private static string NormalizeCityState(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Contains(','))
            return location;

        var states = new[]
        {
            "Alabama", "Alaska", "Arizona", "Arkansas", "California",
            "Colorado", "Connecticut", "Delaware", "Florida", "Georgia",
            "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa",
            "Kansas", "Kentucky", "Louisiana", "Maine", "Maryland",
            "Massachusetts", "Michigan", "Minnesota", "Mississippi", "Missouri",
            "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey",
            "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio",
            "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island",
            "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah",
            "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin",
            "Wyoming"
        };

        foreach (var state in states.OrderByDescending(s => s.Length))
        {
            if (!location.EndsWith(state, StringComparison.OrdinalIgnoreCase))
                continue;

            var city = location[..^state.Length].Trim();
            if (string.IsNullOrWhiteSpace(city))
                return location;

            return $"{city}, {state}";
        }

        return location;
    }

    private static string GetDisplayLocation(RadioLocation location)
    {
        if (!string.IsNullOrWhiteSpace(location.City) && !string.IsNullOrWhiteSpace(location.State))
            return $"{location.City}, {location.State}";
        if (!string.IsNullOrWhiteSpace(location.City))
            return location.City;
        if (!string.IsNullOrWhiteSpace(location.State))
            return location.State;
        if (!string.IsNullOrWhiteSpace(location.PostalCode))
            return location.PostalCode;
        return location.Raw;
    }

}