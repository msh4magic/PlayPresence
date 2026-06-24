using System.Collections.Generic;
using Newtonsoft.Json;

namespace PlayPresence.Services.Igdb
{
    /// <summary>Twitch OAuth2 token response.</summary>
    internal sealed class TwitchTokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }
    }

    public sealed class IgdbImage
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("image_id")]
        public string ImageId { get; set; }
    }

    public sealed class IgdbAlternativeName
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class IgdbGenre
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class IgdbCompany
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class IgdbInvolvedCompany
    {
        [JsonProperty("developer")]
        public bool Developer { get; set; }

        [JsonProperty("publisher")]
        public bool Publisher { get; set; }

        [JsonProperty("company")]
        public IgdbCompany Company { get; set; }
    }

    public sealed class IgdbGame
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("first_release_date")]
        public long? FirstReleaseDate { get; set; }

        [JsonProperty("total_rating_count")]
        public double? TotalRatingCount { get; set; }

        [JsonProperty("cover")]
        public IgdbImage Cover { get; set; }

        [JsonProperty("artworks")]
        public List<IgdbImage> Artworks { get; set; }

        [JsonProperty("alternative_names")]
        public List<IgdbAlternativeName> AlternativeNames { get; set; }

        [JsonProperty("genres")]
        public List<IgdbGenre> Genres { get; set; }

        [JsonProperty("involved_companies")]
        public List<IgdbInvolvedCompany> InvolvedCompanies { get; set; }
    }

    public sealed class IgdbPlatform
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("platform_logo")]
        public IgdbImage PlatformLogo { get; set; }
    }
}
