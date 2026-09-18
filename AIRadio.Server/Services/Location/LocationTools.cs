using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Location;

public sealed class LocationGetTool : ITool
{
    private readonly ILocationService _service;
    public LocationGetTool(ILocationService service) => _service = service;
    public string Name => "locationGet";
    public string GetLlmInstructions() => """
LOCATION GET
Read the radio's persistent location.
Parameters: none.
Example: "Where am I?" -> {tool:locationGet}
""";
    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToolResult.Successful(Name, "Current location retrieved.", _service.GetCurrentLocation()));
    }
}

public sealed class LocationSetTool : ITool
{
    private readonly ILocationService _service;
    public LocationSetTool(ILocationService service) => _service = service;
    public string Name => "locationSet";
    public string GetLlmInstructions() => """
LOCATION SET
Change the radio's persistent location.
Parameters:
- location: required city, state, ZIP/postal code, or recognizable place.
Do not use this tool merely to query another tool's location.
Example: "Set my location to Miami" -> {tool:locationSet,location=Miami}
""";
    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = request.GetString("location");
        if (string.IsNullOrWhiteSpace(location))
            return ToolResult.Failed(Name, "A location is required.");
        var updated = await _service.UpdateCurrentLocationAsync(location.Trim(), cancellationToken);
        return updated is null
            ? ToolResult.Failed(Name, $"Unable to determine location '{location}'.")
            : ToolResult.Successful(Name, "Current location updated.", updated);
    }
}
