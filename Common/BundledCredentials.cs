namespace PlayPresence.Common
{
    /// <summary>
    /// Developer-bundled credentials so features work out of the box, with no per-user setup.
    /// Paste each value between the quotes below before building. Leave any as "" to omit it.
    /// </summary>
    /// <remarks>
    /// Note: bundled values are readable by anyone who inspects the plugin file and are shared across
    /// all users (shared rate limits). The Discord Application ID is public by design; treat the IGDB
    /// (Twitch) Client Secret as the only sensitive one. If you publish the source publicly, prefer
    /// leaving these empty in the committed file and filling them only in your local release build.
    ///
    ///   Discord Application ID : Discord Developer Portal -> your app -> Application ID
    ///   IGDB Client ID/Secret  : https://dev.twitch.tv/console/apps
    ///   SteamGridDB API key    : https://www.steamgriddb.com  (Preferences -> API)
    /// </remarks>
    public static class BundledCredentials
    {
        public const string DiscordApplicationId = "";   // <-- your Discord Application ID
        public const string IgdbClientId         = "";   // <-- your IGDB (Twitch) Client ID
        public const string IgdbClientSecret     = "";   // <-- your IGDB (Twitch) Client Secret
        public const string SteamGridDbApiKey    = "";   // <-- your SteamGridDB API key

        public static bool HasDiscordAppId  => !string.IsNullOrWhiteSpace(DiscordApplicationId);
        public static bool HasIgdbKeys      => !string.IsNullOrWhiteSpace(IgdbClientId)
                                            && !string.IsNullOrWhiteSpace(IgdbClientSecret);
        public static bool HasSteamGridDbKey => !string.IsNullOrWhiteSpace(SteamGridDbApiKey);
    }
}
