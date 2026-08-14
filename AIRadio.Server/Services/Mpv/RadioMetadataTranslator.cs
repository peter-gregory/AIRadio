using AIRadio.Server.Models.Mpv;
using System.Text.Json;

namespace AIRadio.Server.Services.Mpv
{
    public interface IRadioMetadataTranslator
    {
        RadioMetadata Translate(JsonElement metadata);
    }

    public sealed class RadioMetadataTranslator
        : IRadioMetadataTranslator
    {
        private readonly ILogger<RadioMetadataTranslator> _logger;

        public RadioMetadataTranslator(
            ILogger<RadioMetadataTranslator> logger)
        {
            _logger = logger;
        }


        public RadioMetadata Translate(JsonElement metadata)
        {
            var builder =
                new RadioMetadataBuilder();


            foreach (var property in metadata.EnumerateObject())
            {
                var recognized =
                    builder.AddProperty(
                        property.Name,
                        property.Value.ToString());


                if (!recognized)
                {
                    _logger.LogDebug(
                        "Unknown radio metadata property {PropertyName}={Value}",
                        property.Name,
                        property.Value.ToString());
                }
            }


            return builder.Build();
        }
    }

    internal sealed class RadioMetadataBuilder
    {
        private readonly Dictionary<string, string> _additionalProperties =
            new(StringComparer.OrdinalIgnoreCase);

        public string? StationName { get; private set; }

        public string? StationDescription { get; private set; }

        public string? ProgramName { get; private set; }

        public string? Title { get; private set; }

        public string? Artist { get; private set; }

        public string? Album { get; private set; }

        public string? Genre { get; private set; }

        public string? Composer { get; private set; }

        public string? Year { get; private set; }

        public string? Comment { get; private set; }

        public string? StreamUrl { get; private set; }

        public string? ArtworkUrl { get; private set; }

        public IReadOnlyDictionary<string, string> AdditionalProperties =>
            _additionalProperties;

        /// <summary>
        /// Adds a metadata property to the builder.
        /// </summary>
        /// <param name="name">The metadata property name.</param>
        /// <param name="value">The metadata property value.</param>
        /// <returns>
        /// True if the property name is recognized; otherwise false.
        /// Unknown properties are preserved in AdditionalProperties.
        /// </returns>
        public bool AddProperty(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            name = name.Trim();
            value = Normalize(value);

            switch (name.ToLowerInvariant())
            {
                //
                // Station Information (Icecast / SHOUTcast)
                //
                case "icy-name":
                case "station":
                case "station-name":
                case "radio-name":
                    StationName ??= value;
                    return true;

                case "icy-description":
                case "description":
                    StationDescription ??= value;
                    return true;

                case "program":
                case "show":
                    ProgramName ??= value;
                    return true;

                //
                // Track Information (ICY / ID3)
                //
                case "streamtitle":
                case "title":
                    SetTitle(value);
                    return true;

                case "artist":
                case "performer":
                case "creator":
                    Artist ??= value;
                    return true;

                case "album":
                    Album ??= value;
                    return true;

                case "genre":
                    Genre ??= value;
                    return true;

                case "composer":
                    Composer ??= value;
                    return true;

                case "date":
                case "year":
                    Year ??= value;
                    return true;

                case "comment":
                    Comment ??= value;
                    return true;

                //
                // Artwork
                //
                case "artwork":
                case "coverart":
                case "albumart":
                    ArtworkUrl ??= value;
                    return true;

                //
                // URLs
                //
                case "icy-url":
                case "url":
                    StreamUrl ??= value;
                    return true;

                //
                // Unknown metadata
                //
                default:
                    if (value != null)
                    {
                        _additionalProperties[name] = value;
                    }

                    return false;
            }
        }

        private void SetTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            //
            // Common ICY format:
            //     Artist - Title
            //
            if (Artist == null && Title == null)
            {
                int separator = value.IndexOf(" - ", StringComparison.Ordinal);

                if (separator > 0)
                {
                    Artist = Normalize(value[..separator]);
                    Title = Normalize(value[(separator + 3)..]);
                    return;
                }
            }

            Title ??= value;
        }

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value
                .Replace("\0", string.Empty)
                .Trim();

            return value.Length == 0
                ? null
                : value;
        }

        public RadioMetadata Build()
        {
            return new RadioMetadata
            {
                StationName = StationName,
                StationDescription = StationDescription,
                ProgramName = ProgramName,
                Title = Title,
                Artist = Artist,
                Album = Album,
                Genre = Genre,
                Composer = Composer,
                Year = Year,
                Comment = Comment,
                StreamUrl = StreamUrl,
                ArtworkUrl = ArtworkUrl,
                AdditionalProperties = new Dictionary<string, string>(
                    _additionalProperties,
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }

}
