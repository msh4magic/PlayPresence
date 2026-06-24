using System.Collections.Generic;
using PlayPresence.Common;
using PlayPresence.Models;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayPresence.Settings
{
    /// <summary>
    /// The complete, persisted configuration for PlayPresence. Implemented as an
    /// <see cref="ObservableObject"/> so the settings view binds two-way with live updates.
    /// Defaults are chosen to give a great out-of-the-box experience once credentials are entered.
    /// </summary>
    public sealed class PluginSettings : ObservableObject
    {
        // ----- General -----------------------------------------------------
        private bool enableRichPresence = true;
        public bool EnableRichPresence { get => enableRichPresence; set => SetValue(ref enableRichPresence, value); }

        private bool showInDesktopMode = true;
        public bool ShowInDesktopMode { get => showInDesktopMode; set => SetValue(ref showInDesktopMode, value); }

        private bool showInFullscreenMode = true;
        public bool ShowInFullscreenMode { get => showInFullscreenMode; set => SetValue(ref showInFullscreenMode, value); }

        private bool enableIdlePresence = true;
        public bool EnableIdlePresence { get => enableIdlePresence; set => SetValue(ref enableIdlePresence, value); }

        // Optional: show launcher/emulator (e.g. "on Steam", "PlayStation 2 · PCSX2") instead of the
        // plain platform on the second line. Default false -> the platform is shown.
        private bool showSourceContext = false;
        public bool ShowSourceContext { get => showSourceContext; set => SetValue(ref showSourceContext, value); }

        private string idlePresenceText = "Browsing the library";
        public string IdlePresenceText { get => idlePresenceText; set => SetValue(ref idlePresenceText, value); }

        // ----- Discord -----------------------------------------------------
        private string discordApplicationId = string.Empty;
        public string DiscordApplicationId { get => discordApplicationId; set => SetValue(ref discordApplicationId, value); }

        private bool showElapsedTimer = true;
        public bool ShowElapsedTimer { get => showElapsedTimer; set => SetValue(ref showElapsedTimer, value); }

        private bool enableIgdbButton = false;
        public bool EnableIgdbButton { get => enableIgdbButton; set => SetValue(ref enableIgdbButton, value); }

        private bool enableCustomButton = false;
        public bool EnableCustomButton { get => enableCustomButton; set => SetValue(ref enableCustomButton, value); }

        private string customButtonLabel = string.Empty;
        public string CustomButtonLabel { get => customButtonLabel; set => SetValue(ref customButtonLabel, value); }

        private string customButtonUrl = string.Empty;
        public string CustomButtonUrl { get => customButtonUrl; set => SetValue(ref customButtonUrl, value); }

        // ----- Appearance (templates + field toggles) ----------------------
        private string detailsTemplate = "{game}";
        public string DetailsTemplate { get => detailsTemplate; set => SetValue(ref detailsTemplate, value); }

        private string stateTemplate = "{platform}";
        public string StateTemplate { get => stateTemplate; set => SetValue(ref stateTemplate, value); }

        private string largeImageTextTemplate = "{game}";
        public string LargeImageTextTemplate { get => largeImageTextTemplate; set => SetValue(ref largeImageTextTemplate, value); }

        private bool showPlatform = true;
        public bool ShowPlatform { get => showPlatform; set => SetValue(ref showPlatform, value); }

        private bool showDeveloper = true;
        public bool ShowDeveloper { get => showDeveloper; set => SetValue(ref showDeveloper, value); }

        private bool showGenre = true;
        public bool ShowGenre { get => showGenre; set => SetValue(ref showGenre, value); }

        private bool showYear = true;
        public bool ShowYear { get => showYear; set => SetValue(ref showYear, value); }

        private bool showSource = false;
        public bool ShowSource { get => showSource; set => SetValue(ref showSource, value); }

        private bool showEmulator = true;
        public bool ShowEmulator { get => showEmulator; set => SetValue(ref showEmulator, value); }

        private bool showDisc = true;
        public bool ShowDisc { get => showDisc; set => SetValue(ref showDisc, value); }

        private bool showFavorite = false;
        public bool ShowFavorite { get => showFavorite; set => SetValue(ref showFavorite, value); }

        private bool showCompletionStatus = false;
        public bool ShowCompletionStatus { get => showCompletionStatus; set => SetValue(ref showCompletionStatus, value); }

        // ----- Artwork -----------------------------------------------------
        private LargeImageSource largeImageSource = LargeImageSource.Icon;
        public LargeImageSource LargeImageSource { get => largeImageSource; set => SetValue(ref largeImageSource, value); }

        private ImageQuality imageQuality = ImageQuality.High;
        public ImageQuality ImageQuality { get => imageQuality; set => SetValue(ref imageQuality, value); }

        private bool smallImageEnabled = true;
        public bool SmallImageEnabled { get => smallImageEnabled; set => SetValue(ref smallImageEnabled, value); }

        // ----- IGDB --------------------------------------------------------
        private bool enableIgdb = true;
        public bool EnableIgdb { get => enableIgdb; set => SetValue(ref enableIgdb, value); }

        private string igdbClientId = string.Empty;
        public string IgdbClientId { get => igdbClientId; set => SetValue(ref igdbClientId, value); }

        /// <summary>
        /// Runtime (plaintext) IGDB secret. NEVER serialized directly; only the DPAPI-encrypted
        /// <see cref="IgdbClientSecretEncrypted"/> is written to disk.
        /// </summary>
        private string igdbClientSecret = string.Empty;
        [DontSerialize]
        public string IgdbClientSecret
        {
            get => igdbClientSecret;
            set
            {
                // SetValue returns void in the Playnite SDK, so update the encrypted mirror
                // unconditionally; persistence then always has the latest value.
                SetValue(ref igdbClientSecret, value);
                igdbClientSecretEncrypted = SecretProtector.Protect(value);
            }
        }

        /// <summary>DPAPI-encrypted IGDB secret as actually stored on disk.</summary>
        private string igdbClientSecretEncrypted = string.Empty;
        public string IgdbClientSecretEncrypted
        {
            get => igdbClientSecretEncrypted;
            set => SetValue(ref igdbClientSecretEncrypted, value);
        }

        private bool preferRomFilename = true;
        public bool PreferRomFilename { get => preferRomFilename; set => SetValue(ref preferRomFilename, value); }

        // ----- SteamGridDB (optional art source) ---------------------------
        private bool enableSteamGridDb = true;
        public bool EnableSteamGridDb { get => enableSteamGridDb; set => SetValue(ref enableSteamGridDb, value); }

        private bool preferSteamGridDb = false;
        public bool PreferSteamGridDb { get => preferSteamGridDb; set => SetValue(ref preferSteamGridDb, value); }

        /// <summary>Runtime (plaintext) SteamGridDB key; only the encrypted mirror is persisted.</summary>
        private string steamGridDbApiKey = string.Empty;
        [DontSerialize]
        public string SteamGridDbApiKey
        {
            get => steamGridDbApiKey;
            set
            {
                SetValue(ref steamGridDbApiKey, value);
                steamGridDbApiKeyEncrypted = SecretProtector.Protect(value);
            }
        }

        private string steamGridDbApiKeyEncrypted = string.Empty;
        public string SteamGridDbApiKeyEncrypted
        {
            get => steamGridDbApiKeyEncrypted;
            set => SetValue(ref steamGridDbApiKeyEncrypted, value);
        }

        // ----- Privacy -----------------------------------------------------
        private bool privateMode = false;
        /// <summary>When on, no presence is shown regardless of other settings (quick privacy toggle).</summary>
        public bool PrivateMode { get => privateMode; set => SetValue(ref privateMode, value); }

        // ----- Google image fallback (optional last resort) ----------------
        private bool enableGoogleImageFallback = false;
        public bool EnableGoogleImageFallback { get => enableGoogleImageFallback; set => SetValue(ref enableGoogleImageFallback, value); }

        /// <summary>Programmable Search Engine id (CX). Semi-public, stored as plain text.</summary>
        private string googleSearchEngineId = string.Empty;
        public string GoogleSearchEngineId { get => googleSearchEngineId; set => SetValue(ref googleSearchEngineId, value); }

        /// <summary>Runtime (plaintext) Google API key; only the encrypted mirror is persisted.</summary>
        private string googleApiKey = string.Empty;
        [DontSerialize]
        public string GoogleApiKey
        {
            get => googleApiKey;
            set
            {
                SetValue(ref googleApiKey, value);
                googleApiKeyEncrypted = SecretProtector.Protect(value);
            }
        }

        private string googleApiKeyEncrypted = string.Empty;
        public string GoogleApiKeyEncrypted
        {
            get => googleApiKeyEncrypted;
            set => SetValue(ref googleApiKeyEncrypted, value);
        }

        private double matchConfidenceThreshold = 0.55;
        public double MatchConfidenceThreshold { get => matchConfidenceThreshold; set => SetValue(ref matchConfidenceThreshold, value); }

        // ----- Performance -------------------------------------------------
        private int cacheDurationDays = 30;
        public int CacheDurationDays { get => cacheDurationDays; set => SetValue(ref cacheDurationDays, value); }

        private int maxConcurrentRequests = 3;
        public int MaxConcurrentRequests { get => maxConcurrentRequests; set => SetValue(ref maxConcurrentRequests, value); }

        // ----- Diagnostics -------------------------------------------------
        private bool enableDebugLogging = false;
        public bool EnableDebugLogging { get => enableDebugLogging; set => SetValue(ref enableDebugLogging, value); }

        private LogVerbosity logVerbosity = LogVerbosity.Information;
        public LogVerbosity LogVerbosity { get => logVerbosity; set => SetValue(ref logVerbosity, value); }

        /// <summary>
        /// Called right after Playnite deserializes the object to rehydrate the runtime secret from
        /// its encrypted form.
        /// </summary>
        public void AfterLoad()
        {
            igdbClientSecret = SecretProtector.Unprotect(igdbClientSecretEncrypted);
            steamGridDbApiKey = SecretProtector.Unprotect(steamGridDbApiKeyEncrypted);
            googleApiKey = SecretProtector.Unprotect(googleApiKeyEncrypted);
        }
    }
}
