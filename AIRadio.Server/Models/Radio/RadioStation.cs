using AIRadio.Server.Services;
using Microsoft.Extensions.FileSystemGlobbing;
using System.Net.NetworkInformation;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace AIRadio.Server.Models.Radio
{
    public sealed class RadioStation
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string StreamUrl { get; set; } = string.Empty;

        public int? FavoriteNumber { get; set; }

        public string? Description { get; set; }

        public string? Homepage { get; set; }

        public string? Favicon { get; set; }

        public string[] Tags { get; set; } = [];

        public string? Country { get; set; }

        public string? CountryCode { get; set; }

        public string? State { get; set; }

        public string[] Languages { get; set; } = [];

        public string? Codec { get; set; }

        public int? Bitrate { get; set; }

        public string? Source { get; set; }

        public string? SourceId { get; set; }

        public bool IsHttps { get; set; }

        public bool IsOnline { get; set; }

        public int Votes { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        public DateTimeOffset? LastPlayed { get; set; }

        public int PlayCount { get; set; }

        public RadioStation Clone()
        {
            return new RadioStation
            {
                Id = Id,
                Name = Name,
                StreamUrl = StreamUrl,
                FavoriteNumber = FavoriteNumber,
                Description = Description,
                Homepage = Homepage,
                Favicon = Favicon,
                Tags = [.. Tags],
                Country = Country,
                CountryCode = CountryCode,
                State = State,
                Languages = [.. Languages],
                Codec = Codec,
                Bitrate = Bitrate,
                Source = Source,
                SourceId = SourceId,
                IsHttps = IsHttps,
                IsOnline = IsOnline,
                Votes = Votes,
                Latitude = Latitude,
                Longitude = Longitude,
                LastPlayed = LastPlayed,
                PlayCount = PlayCount
            };
        }
    }

    public sealed class StationRegistry
    {
        public List<RadioStation> Stations { get; set; } = [];
    }
}
