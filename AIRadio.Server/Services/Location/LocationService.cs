using AIRadio.Server.Models.Location;

namespace AIRadio.Server.Services.Location
{
    public interface ILocationService
    {
        RadioLocation? GetCurrentLocation();

        Task<RadioLocation?> ResolveLocationAsync(
            string location,
            CancellationToken cancellationToken = default);

        Task<RadioLocation?> UpdateCurrentLocationAsync(
            string location,
            CancellationToken cancellationToken = default);

        RadioLocation BuildLocation(
            string raw,
            string? address = null,
            string? city = null,
            string? state = null,
            string? postalCode = null,
            string? country = null);

        Task InitializeAsync(
            CancellationToken cancellationToken = default);
    }

    public sealed class LocationService : ILocationService
    {
        private readonly ILocationStore _store;
        private readonly ILogger<LocationService> _logger;

        private readonly object _sync = new();

        private RadioLocation? _currentLocation;

        public LocationService(
            ILocationStore store,
            ILogger<LocationService> logger)
        {
            _store = store;
            _logger = logger;
        }

        // ============================================================
        // CURRENT LOCATION
        // ============================================================

        public RadioLocation? GetCurrentLocation()
        {
            lock (_sync)
            {
                return _currentLocation;
            }
        }

        // ============================================================
        // RESOLVE LOCATION
        // ============================================================

        public async Task<RadioLocation?> ResolveLocationAsync(
            string location,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                location);

            cancellationToken.ThrowIfCancellationRequested();

            /*
             * Location resolution is intentionally kept separate from
             * updating the current location.
             *
             * A request such as:
             *
             *     "What's the weather in Boca Raton?"
             *
             * resolves a RadioLocation but does not change the radio's
             * persisted current location.
             *
             * A geocoder can be added here later to populate City,
             * State, Country, etc.
             */

            return BuildLocation(
                location.Trim());
        }

        // ============================================================
        // UPDATE CURRENT LOCATION
        // ============================================================

        public async Task<RadioLocation?> UpdateCurrentLocationAsync(
            string location,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                location);

            var resolved =
                await ResolveLocationAsync(
                    location,
                    cancellationToken);

            if (resolved is null)
            {
                return null;
            }

            await _store.SaveAsync(
                resolved,
                cancellationToken);

            lock (_sync)
            {
                _currentLocation = resolved;
            }

            _logger.LogInformation(
                "Current radio location updated to {Location}.",
                GetDisplayName(resolved));

            return resolved;
        }

        // ============================================================
        // BUILD LOCATION
        // ============================================================

        public RadioLocation BuildLocation(
            string raw,
            string? address = null,
            string? city = null,
            string? state = null,
            string? postalCode = null,
            string? country = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                raw);

            return new RadioLocation
            {
                Raw = raw.Trim(),

                Address =
                    Normalize(address),

                City =
                    Normalize(city),

                State =
                    Normalize(state),

                PostalCode =
                    Normalize(postalCode),

                Country =
                    Normalize(country),

                UpdatedAt =
                    DateTimeOffset.Now
            };
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        public async Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            var location =
                await _store.GetAsync(
                    cancellationToken);

            lock (_sync)
            {
                _currentLocation = location;
            }

            if (location is null)
            {
                _logger.LogInformation(
                    "No saved radio location was found.");
            }
            else
            {
                _logger.LogInformation(
                    "Radio location initialized to {Location}.",
                    GetDisplayName(location));
            }
        }

        // ============================================================
        // HELPERS
        // ============================================================

        private static string? Normalize(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static string GetDisplayName(
            RadioLocation location)
        {
            if (!string.IsNullOrWhiteSpace(location.City) &&
                !string.IsNullOrWhiteSpace(location.State))
            {
                return $"{location.City}, {location.State}";
            }

            if (!string.IsNullOrWhiteSpace(location.City))
            {
                return location.City;
            }

            if (!string.IsNullOrWhiteSpace(location.State))
            {
                return location.State;
            }

            if (!string.IsNullOrWhiteSpace(location.PostalCode))
            {
                return location.PostalCode;
            }

            if (!string.IsNullOrWhiteSpace(location.Country))
            {
                return location.Country;
            }

            return location.Raw;
        }
    }
}
