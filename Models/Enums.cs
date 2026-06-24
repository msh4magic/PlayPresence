namespace PlayPresence.Models
{
    /// <summary>Preferred artwork resolution for the Discord large image.</summary>
    public enum ImageQuality
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>Which image to use as the large Rich Presence image.</summary>
    public enum LargeImageSource
    {
        /// <summary>Square game icon (best for the icon look; sourced from SteamGridDB icons).</summary>
        Icon = 0,
        /// <summary>Portrait game cover (IGDB / SteamGridDB).</summary>
        Cover = 1,
        /// <summary>Landscape key artwork (IGDB).</summary>
        Artwork = 2
    }

    /// <summary>Verbosity of the plugin's diagnostic log.</summary>
    public enum LogVerbosity
    {
        Errors = 0,
        Warnings = 1,
        Information = 2,
        Debug = 3
    }
}
