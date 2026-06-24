using System;
using System.ComponentModel;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using PlayPresence.Common;
using PlayPresence.Models;
using PlayPresence.Services.Presence;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayPresence.Settings
{
    /// <summary>
    /// View-model for the settings page. Implements Playnite's <see cref="ISettings"/> edit
    /// lifecycle and exposes commands (test/reconnect/clear/reset/import/export, open portals),
    /// a first-run getting-started panel, and a live presence preview.
    /// </summary>
    public sealed class PluginSettingsViewModel : ObservableObject, ISettings
    {
        private const string DiscordPortalUrl = "https://discord.com/developers/applications";
        private const string IgdbPortalUrl = "https://dev.twitch.tv/console/apps";
        private const string KofiUrl = "https://ko-fi.com/mshfm";

        private readonly IPlayPresenceController controller;
        private readonly ILogger logger = LogManager.GetLogger();

        private PluginSettings editingClone;
        private PluginSettings settings;

        public PluginSettingsViewModel(IPlayPresenceController controller, PluginSettings loaded, bool isFirstRun)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            IsFirstRun = isFirstRun;
            Settings = loaded ?? new PluginSettings();
        }

        public bool IsFirstRun { get; }

        /// <summary>The live settings object bound to the view.</summary>
        public PluginSettings Settings
        {
            get => settings;
            set
            {
                if (settings != null)
                {
                    settings.PropertyChanged -= OnSettingChanged;
                }
                SetValue(ref settings, value);
                if (settings != null)
                {
                    settings.PropertyChanged += OnSettingChanged;
                }
                UpdateDerived();
            }
        }

        // ----- First-run / getting-started ---------------------------------

        private bool discordConfigured;
        public bool DiscordConfigured { get => discordConfigured; private set => SetValue(ref discordConfigured, value); }

        private bool igdbConfigured;
        public bool IgdbConfigured { get => igdbConfigured; private set => SetValue(ref igdbConfigured, value); }

        private bool showGettingStarted;
        /// <summary>Visible on first run, or whenever Discord isn't configured yet.</summary>
        public bool ShowGettingStarted { get => showGettingStarted; private set => SetValue(ref showGettingStarted, value); }

        private string stepDiscordText;
        public string StepDiscordText { get => stepDiscordText; private set => SetValue(ref stepDiscordText, value); }

        private string stepIgdbText;
        public string StepIgdbText { get => stepIgdbText; private set => SetValue(ref stepIgdbText, value); }

        // ----- Live preview ------------------------------------------------

        private string previewDetails;
        public string PreviewDetails { get => previewDetails; private set => SetValue(ref previewDetails, value); }

        private string previewState;
        public string PreviewState { get => previewState; private set => SetValue(ref previewState, value); }

        private string previewLargeText;
        public string PreviewLargeText { get => previewLargeText; private set => SetValue(ref previewLargeText, value); }

        private bool previewHasButton;
        public bool PreviewHasButton { get => previewHasButton; private set => SetValue(ref previewHasButton, value); }

        private string previewButtonLabel;
        public string PreviewButtonLabel { get => previewButtonLabel; private set => SetValue(ref previewButtonLabel, value); }

        private string statusText = string.Empty;
        public string StatusText { get => statusText; set => SetValue(ref statusText, value); }

        // Enum option lists for combo boxes.
        public IEnumerable<ImageQuality> ImageQualityOptions => (ImageQuality[])Enum.GetValues(typeof(ImageQuality));
        public IEnumerable<LargeImageSource> LargeImageSourceOptions => (LargeImageSource[])Enum.GetValues(typeof(LargeImageSource));
        public IEnumerable<LogVerbosity> LogVerbosityOptions => (LogVerbosity[])Enum.GetValues(typeof(LogVerbosity));

        // ----- Commands ----------------------------------------------------

        public RelayCommand<object> TestConnectionCommand => new RelayCommand<object>(_ => RunTestConnection());
        public RelayCommand<object> ReconnectCommand => new RelayCommand<object>(_ => RunReconnect());
        public RelayCommand<object> ClearCacheCommand => new RelayCommand<object>(_ => RunClearCache());
        public RelayCommand<object> ResetDefaultsCommand => new RelayCommand<object>(_ => RunResetDefaults());
        public RelayCommand<object> ExportCommand => new RelayCommand<object>(_ => RunExport());
        public RelayCommand<object> ImportCommand => new RelayCommand<object>(_ => RunImport());
        public RelayCommand<object> OpenDiscordPortalCommand => new RelayCommand<object>(_ => OpenUrl(DiscordPortalUrl));
        public RelayCommand<object> OpenIgdbPortalCommand => new RelayCommand<object>(_ => OpenUrl(IgdbPortalUrl));
        public RelayCommand<object> OpenKofiCommand => new RelayCommand<object>(_ => OpenUrl(KofiUrl));

        // ----- ISettings lifecycle -----------------------------------------

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
            editingClone.AfterLoad();
        }

        public void CancelEdit()
        {
            if (editingClone != null)
            {
                Settings = editingClone;
                editingClone = null;
            }
        }

        public void EndEdit()
        {
            editingClone = null;
            // Ensure encrypted mirrors are current before the plugin persists.
            Settings.IgdbClientSecret = Settings.IgdbClientSecret;
            Settings.SteamGridDbApiKey = Settings.SteamGridDbApiKey;
            Settings.GoogleApiKey = Settings.GoogleApiKey;
            controller.OnSettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (Settings.EnableRichPresence && string.IsNullOrWhiteSpace(Settings.DiscordApplicationId)
                && !BundledCredentials.HasDiscordAppId)
            {
                errors.Add(L("LOCPlayPresence_ValAppId"));
            }
            else if (!string.IsNullOrWhiteSpace(Settings.DiscordApplicationId) &&
                     !IsLikelyDiscordAppId(Settings.DiscordApplicationId))
            {
                errors.Add(L("LOCPlayPresence_ValAppIdFormat"));
            }

            if (Settings.EnableIgdb && !BundledCredentials.HasIgdbKeys &&
                (string.IsNullOrWhiteSpace(Settings.IgdbClientId) || string.IsNullOrWhiteSpace(Settings.IgdbClientSecret)))
            {
                errors.Add(L("LOCPlayPresence_ValIgdbCreds"));
            }

            if (Settings.MatchConfidenceThreshold < 0.1 || Settings.MatchConfidenceThreshold > 1.0)
            {
                errors.Add(L("LOCPlayPresence_ValThreshold"));
            }

            if (Settings.CacheDurationDays < 0 || Settings.CacheDurationDays > 365)
            {
                errors.Add(L("LOCPlayPresence_ValCache"));
            }

            if (Settings.MaxConcurrentRequests < 1 || Settings.MaxConcurrentRequests > 4)
            {
                errors.Add(L("LOCPlayPresence_ValConcurrent"));
            }

            if (Settings.EnableCustomButton)
            {
                if (string.IsNullOrWhiteSpace(Settings.CustomButtonLabel))
                {
                    errors.Add(L("LOCPlayPresence_ValButtonLabel"));
                }
                if (!IsValidHttpUrl(Settings.CustomButtonUrl))
                {
                    errors.Add(L("LOCPlayPresence_ValButtonUrl"));
                }
            }

            return errors.Count == 0;
        }

        // ----- Reactivity --------------------------------------------------

        private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateDerived();
        }

        private void UpdateDerived()
        {
            if (Settings == null)
            {
                return;
            }

            DiscordConfigured = !string.IsNullOrWhiteSpace(Settings.DiscordApplicationId) || BundledCredentials.HasDiscordAppId;
            IgdbConfigured = (!string.IsNullOrWhiteSpace(Settings.IgdbClientId) &&
                              !string.IsNullOrWhiteSpace(Settings.IgdbClientSecret)) || BundledCredentials.HasIgdbKeys;
            ShowGettingStarted = IsFirstRun || !DiscordConfigured;
            StepDiscordText = L(DiscordConfigured ? "LOCPlayPresence_StepDiscordDone" : "LOCPlayPresence_StepDiscordTodo");
            StepIgdbText = L(IgdbConfigured ? "LOCPlayPresence_StepIgdbDone" : "LOCPlayPresence_StepIgdbTodo");
            RecomputePreview();
        }

        private void RecomputePreview()
        {
            try
            {
                var options = new PresenceOptions
                {
                    DetailsTemplate = Settings.DetailsTemplate,
                    StateTemplate = Settings.StateTemplate,
                    LargeImageTextTemplate = Settings.LargeImageTextTemplate,
                    ShowElapsedTimer = Settings.ShowElapsedTimer,
                    SmallImageEnabled = Settings.SmallImageEnabled,
                    ShowPlatform = Settings.ShowPlatform,
                    ShowDeveloper = Settings.ShowDeveloper,
                    ShowGenre = Settings.ShowGenre,
                    ShowYear = Settings.ShowYear,
                    ShowSource = Settings.ShowSource,
                    ShowEmulator = Settings.ShowEmulator,
                    ShowDisc = Settings.ShowDisc,
                    EnableCustomButton = Settings.EnableCustomButton,
                    CustomButtonLabel = Settings.CustomButtonLabel,
                    CustomButtonUrl = Settings.CustomButtonUrl,
                    EnableIgdbButton = Settings.EnableIgdbButton
                };

                var sampleContext = new GameContext
                {
                    GameName = "The Legend of Zelda: Breath of the Wild",
                    PlatformName = "Nintendo Switch",
                    Source = "Emulation",
                    EmulatorName = "Ryujinx",
                    DiscLabel = null,
                    ReleaseYear = 2017
                };
                var sampleMetadata = new ResolvedMetadata
                {
                    Title = "The Legend of Zelda: Breath of the Wild",
                    Developer = "Nintendo",
                    Genre = "Adventure",
                    ReleaseYear = 2017,
                    FromIgdb = true,
                    IgdbUrl = "https://www.igdb.com/games/the-legend-of-zelda-breath-of-the-wild"
                };

                var model = PresenceBuilder.Build(options, sampleContext, sampleMetadata, DateTime.UtcNow.AddMinutes(-12));
                PreviewDetails = model.Details;
                PreviewState = model.State;
                PreviewLargeText = model.LargeImageText;
                PreviewHasButton = model.Buttons != null && model.Buttons.Count > 0;
                PreviewButtonLabel = PreviewHasButton ? model.Buttons[0].Label : null;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "PlayPresence: preview computation failed.");
            }
        }

        // ----- Command bodies ----------------------------------------------

        private void RunTestConnection()
        {
            StatusText = L("LOCPlayPresence_StatusTesting");
            var candidate = Serialization.GetClone(Settings);
            candidate.AfterLoad();

            System.Threading.Tasks.Task.Run(async () =>
            {
                string message;
                try
                {
                    message = await controller.TestConnectionAsync(candidate).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "PlayPresence: test connection threw.");
                    message = ex.Message;
                }
                OnUiThread(() => StatusText = message);
            });
        }

        private void RunReconnect()
        {
            try
            {
                controller.ReconnectDiscord();
                StatusText = L("LOCPlayPresence_StatusReconnected");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayPresence: reconnect failed.");
                StatusText = ex.Message;
            }
        }

        private void RunClearCache()
        {
            try
            {
                controller.ClearMetadataCache();
                StatusText = L("LOCPlayPresence_StatusCacheCleared");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayPresence: clear cache failed.");
                StatusText = ex.Message;
            }
        }

        private void RunResetDefaults()
        {
            var confirm = controller.PlayniteApi.Dialogs.ShowMessage(
                L("LOCPlayPresence_ResetConfirm"), "PlayPresence", MessageBoxButton.YesNo);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var defaults = new PluginSettings
            {
                DiscordApplicationId = Settings.DiscordApplicationId,
                IgdbClientId = Settings.IgdbClientId,
                IgdbClientSecret = Settings.IgdbClientSecret,
                SteamGridDbApiKey = Settings.SteamGridDbApiKey,
                GoogleApiKey = Settings.GoogleApiKey,
                GoogleSearchEngineId = Settings.GoogleSearchEngineId
            };
            Settings = defaults;
            StatusText = L("LOCPlayPresence_StatusReset");
        }

        private void RunExport()
        {
            try
            {
                var path = controller.PlayniteApi.Dialogs.SaveFile("JSON|*.json");
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                var exportable = Serialization.GetClone(Settings);
                exportable.IgdbClientSecret = string.Empty;
                exportable.IgdbClientSecretEncrypted = string.Empty;
                exportable.SteamGridDbApiKey = string.Empty;
                exportable.SteamGridDbApiKeyEncrypted = string.Empty;
                exportable.GoogleApiKey = string.Empty;
                exportable.GoogleApiKeyEncrypted = string.Empty;

                File.WriteAllText(path, Serialization.ToJson(exportable, true));
                StatusText = L("LOCPlayPresence_StatusExported");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayPresence: export failed.");
                StatusText = ex.Message;
            }
        }

        private void RunImport()
        {
            try
            {
                var path = controller.PlayniteApi.Dialogs.SelectFile("JSON|*.json");
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return;
                }

                var imported = Serialization.FromJson<PluginSettings>(File.ReadAllText(path));
                if (imported == null)
                {
                    StatusText = L("LOCPlayPresence_StatusImportInvalid");
                    return;
                }

                imported.DiscordApplicationId = Settings.DiscordApplicationId;
                imported.IgdbClientId = Settings.IgdbClientId;
                imported.IgdbClientSecret = Settings.IgdbClientSecret;
                imported.SteamGridDbApiKey = Settings.SteamGridDbApiKey;
                imported.GoogleApiKey = Settings.GoogleApiKey;
                imported.GoogleSearchEngineId = Settings.GoogleSearchEngineId;
                imported.AfterLoad();
                Settings = imported;
                StatusText = L("LOCPlayPresence_StatusImported");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayPresence: import failed.");
                StatusText = ex.Message;
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "PlayPresence: failed to open URL.");
            }
        }

        // ----- Helpers -----------------------------------------------------

        private static string L(string key)
        {
            return LocalizationLoader.Loc(key);
        }

        private static bool IsLikelyDiscordAppId(string id)
        {
            id = id?.Trim() ?? string.Empty;
            if (id.Length < 17 || id.Length > 20)
            {
                return false;
            }
            foreach (var c in id)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidHttpUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                   && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static void OnUiThread(Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
    }
}
