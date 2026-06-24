using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using PlayPresence.Common;
using PlayPresence.Models;
using PlayPresence.Services.Discord;
using PlayPresence.Services.Igdb;
using PlayPresence.Services.Images;
using PlayPresence.Services.Matching;
using PlayPresence.Services.Metadata;
using PlayPresence.Services.Overrides;
using PlayPresence.Services.Platforms;
using PlayPresence.Services.Presence;
using PlayPresence.Settings;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayPresence.Plugin
{
    /// <summary>
    /// PlayPresence — a generic Playnite plugin delivering a premium Discord Rich Presence
    /// experience enriched with IGDB (and optional SteamGridDB) artwork plus intelligent ROM-name
    /// matching. This class is the composition root and translates Playnite lifecycle events into
    /// presence updates.
    /// </summary>
    public sealed class PlayPresencePlugin : GenericPlugin, IPlayPresenceController
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string MenuSection = "PlayPresence";

        private readonly HttpClient httpClient;
        private readonly PluginSettingsViewModel settingsViewModel;
        private readonly DiscordPresenceService discord;
        private readonly GameOverrideStore overrides;

        // Metadata stack (rebuilt when settings that affect it change).
        private readonly object stackLock = new object();
        private IgdbAuthClient igdbAuth;
        private IgdbMetadataClient igdbClient;
        private SteamGridDbClient steamGridDb;
        private GoogleImageClient googleImages;
        private MetadataCache metadataCache;
        private IMetadataResolver metadataResolver;

        // Session tracking.
        private readonly ConcurrentDictionary<Guid, DateTime> sessionStarts =
            new ConcurrentDictionary<Guid, DateTime>();
        private readonly object ctsLock = new object();
        private CancellationTokenSource activeUpdateCts;
        private DateTime appStartUtc = DateTime.UtcNow;

        public override Guid Id { get; } = Guid.Parse("c2f4e6a8-1b3d-4c5e-9f70-2a8b6d1e5c40");

        // GenericPlugin.PlayniteApi is a public field, which cannot implicitly satisfy an interface
        // property, so expose it explicitly to the controller interface used by the view-model.
        IPlayniteAPI IPlayPresenceController.PlayniteApi => PlayniteApi;

        public PlayPresencePlugin(IPlayniteAPI api) : base(api)
        {
            LocalizationLoader.EnsureLoaded();

            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            discord = new DiscordPresenceService();
            steamGridDb = new SteamGridDbClient(httpClient);
            googleImages = new GoogleImageClient(httpClient);
            overrides = new GameOverrideStore(GetPluginUserDataPath());

            var loaded = LoadPluginSettings<PluginSettings>() ?? new PluginSettings();
            loaded.AfterLoad();
            var isFirstRun = string.IsNullOrWhiteSpace(loaded.DiscordApplicationId) && !BundledCredentials.HasDiscordAppId;
            settingsViewModel = new PluginSettingsViewModel(this, loaded, isFirstRun);

            metadataCache = new MetadataCache(System.IO.Path.Combine(GetPluginUserDataPath(), "cache"));
            BuildMetadataStack(loaded);

            Properties = new GenericPluginProperties { HasSettings = true };
        }

        // ----- Settings plumbing -------------------------------------------

        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;

        public override UserControl GetSettingsView(bool firstRunView) => new SettingsView();

        private PluginSettings Settings => settingsViewModel.Settings;

        public void OnSettingsSaved()
        {
            var s = Settings;
            try
            {
                SavePluginSettings(s);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPresence: failed to persist settings.");
            }

            BuildMetadataStack(s);
            ReinitDiscordFromSettings();
            RefreshPresenceForCurrentState();
        }

        private void BuildMetadataStack(PluginSettings s)
        {
            lock (stackLock)
            {
                try { igdbClient?.Dispose(); } catch { /* ignore */ }
                try { igdbAuth?.Dispose(); } catch { /* ignore */ }

                igdbAuth = new IgdbAuthClient(httpClient);
                string igdbId, igdbSecret;
                ResolveIgdbCredentials(s, out igdbId, out igdbSecret);
                igdbAuth.Configure(igdbId, igdbSecret);

                var maxConcurrent = Math.Max(1, Math.Min(4, s.MaxConcurrentRequests));
                igdbClient = new IgdbMetadataClient(httpClient, igdbAuth, maxConcurrent);

                // The user's own key takes priority; otherwise fall back to the developer-bundled
                // key so SteamGridDB works in the background with zero setup (no user is forced to
                // supply their own — they simply may, to use their own rate limit).
                var steamKey = !string.IsNullOrWhiteSpace(s.SteamGridDbApiKey)
                    ? s.SteamGridDbApiKey
                    : BundledCredentials.SteamGridDbApiKey;
                steamGridDb.Configure(steamKey);
                googleImages.Configure(s.GoogleApiKey, s.GoogleSearchEngineId);

                metadataResolver = new MetadataResolver(igdbClient, metadataCache, BuildResolverOptions, steamGridDb, googleImages);
            }
        }

        // Resolves effective IGDB credentials: the user's own pair takes priority; otherwise the
        // developer-bundled pair is used so IGDB works with zero setup.
        private static void ResolveIgdbCredentials(PluginSettings s, out string clientId, out string clientSecret)
        {
            if (!string.IsNullOrWhiteSpace(s.IgdbClientId) && !string.IsNullOrWhiteSpace(s.IgdbClientSecret))
            {
                clientId = s.IgdbClientId;
                clientSecret = s.IgdbClientSecret;
            }
            else
            {
                clientId = BundledCredentials.IgdbClientId;
                clientSecret = BundledCredentials.IgdbClientSecret;
            }
        }

        // Effective Discord Application ID: the user's own value takes priority; otherwise the
        // developer-bundled id (if any) so Rich Presence works with zero setup.
        private string EffectiveDiscordAppId()
        {
            return !string.IsNullOrWhiteSpace(Settings.DiscordApplicationId)
                ? Settings.DiscordApplicationId
                : BundledCredentials.DiscordApplicationId;
        }

        private MetadataResolverOptions BuildResolverOptions()
        {
            var s = Settings;
            return new MetadataResolverOptions
            {
                IgdbEnabled = s.EnableIgdb,
                SmallImageEnabled = s.SmallImageEnabled,
                Quality = s.ImageQuality,
                LargeImageSource = s.LargeImageSource,
                CacheDays = Math.Max(0, s.CacheDurationDays),
                MatchThreshold = s.MatchConfidenceThreshold,
                SteamGridDbEnabled = s.EnableSteamGridDb,
                PreferSteamGridDb = s.PreferSteamGridDb,
                GoogleFallbackEnabled = s.EnableGoogleImageFallback
            };
        }

        private PresenceOptions BuildPresenceOptions()
        {
            var s = Settings;
            return new PresenceOptions
            {
                DetailsTemplate = s.DetailsTemplate,
                StateTemplate = s.StateTemplate,
                LargeImageTextTemplate = s.LargeImageTextTemplate,
                ShowElapsedTimer = s.ShowElapsedTimer,
                ShowSourceContext = s.ShowSourceContext,
                LargeImageSource = s.LargeImageSource,
                SmallImageEnabled = s.SmallImageEnabled,
                ShowPlatform = s.ShowPlatform,
                ShowDeveloper = s.ShowDeveloper,
                ShowGenre = s.ShowGenre,
                ShowYear = s.ShowYear,
                ShowSource = s.ShowSource,
                ShowEmulator = s.ShowEmulator,
                ShowDisc = s.ShowDisc,
                ShowFavorite = s.ShowFavorite,
                ShowCompletionStatus = s.ShowCompletionStatus,
                EnableCustomButton = s.EnableCustomButton,
                CustomButtonLabel = s.CustomButtonLabel,
                CustomButtonUrl = s.CustomButtonUrl,
                // The "View on IGDB" button was removed from the product; never add it, even if an
                // older saved settings file still has it enabled.
                EnableIgdbButton = false
            };
        }

        // ----- Playnite lifecycle ------------------------------------------

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            appStartUtc = DateTime.UtcNow;
            try
            {
                if (Settings.EnableDebugLogging)
                {
                    Logger.Info("PlayPresence: OnApplicationStarted fired. EnableRichPresence=" + Settings.EnableRichPresence
                        + ", PrivateMode=" + Settings.PrivateMode
                        + ", bundledDiscordId=" + BundledCredentials.HasDiscordAppId
                        + ", userDiscordId=" + (!string.IsNullOrWhiteSpace(Settings.DiscordApplicationId)) + ".");
                }
                ReinitDiscordFromSettings();
                ShowIdlePresence();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPresence: OnApplicationStarted failed.");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            CancelActiveUpdate();
            try { discord.Shutdown(); } catch (Exception ex) { Logger.Warn(ex, "PlayPresence: discord shutdown error."); }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            var game = args?.Game;
            if (game == null)
            {
                return;
            }

            var startUtc = DateTime.UtcNow;
            sessionStarts[game.Id] = startUtc;

            // Respect per-game hide and the global private/enable/mode gates.
            if (overrides.IsHidden(game.Id))
            {
                if (Settings.EnableDebugLogging)
                {
                    Logger.Info($"PlayPresence: '{game.Name}' is hidden; showing idle instead.");
                }
                ShowIdlePresence();
                return;
            }
            if (!ShouldShowPresence())
            {
                return;
            }

            var token = BeginNewUpdate();
            var context = BuildGameContext(game);
            Task.Run(() => UpdatePresenceForGameAsync(context, startUtc, token), token);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var game = args?.Game;
            if (game != null)
            {
                DateTime ignored;
                sessionStarts.TryRemove(game.Id, out ignored);
            }

            CancelActiveUpdate();
            ShowIdlePresence();
        }

        // ----- Menu items --------------------------------------------------

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var game = args?.Games != null && args.Games.Count > 0 ? args.Games[0] : null;
            if (game == null)
            {
                yield break;
            }

            yield return new GameMenuItem
            {
                MenuSection = MenuSection,
                Description = LocalizationLoader.Loc("LOCPlayPresence_MenuRefresh"),
                Action = _ => RefreshGameMetadata(game)
            };

            yield return new GameMenuItem
            {
                MenuSection = MenuSection,
                Description = LocalizationLoader.Loc("LOCPlayPresence_MenuSetId"),
                Action = _ => SetIgdbIdManually(game)
            };

            if (overrides.GetIgdbOverride(game.Id).HasValue)
            {
                yield return new GameMenuItem
                {
                    MenuSection = MenuSection,
                    Description = LocalizationLoader.Loc("LOCPlayPresence_MenuClearOverride"),
                    Action = _ => ClearIgdbOverride(game)
                };
            }

            var hidden = overrides.IsHidden(game.Id);
            yield return new GameMenuItem
            {
                MenuSection = MenuSection,
                Description = LocalizationLoader.Loc(hidden ? "LOCPlayPresence_MenuShow" : "LOCPlayPresence_MenuHide"),
                Action = _ => ToggleHidden(game)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var privateOn = Settings.PrivateMode;
            yield return new MainMenuItem
            {
                MenuSection = "@" + MenuSection,
                Description = LocalizationLoader.Loc(privateOn ? "LOCPlayPresence_MenuPrivateOff" : "LOCPlayPresence_MenuPrivateOn"),
                Action = _ => TogglePrivateMode()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@" + MenuSection,
                Description = LocalizationLoader.Loc("LOCPlayPresence_MenuMainReconnect"),
                Action = _ => ReconnectDiscord()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@" + MenuSection,
                Description = LocalizationLoader.Loc("LOCPlayPresence_MenuMainClearCache"),
                Action = _ => ClearMetadataCache()
            };
        }

        private void RefreshGameMetadata(Game game)
        {
            try
            {
                var context = BuildGameContext(game);
                lock (stackLock)
                {
                    metadataResolver?.Invalidate(context);
                }

                // If this game is the active session, re-resolve and push immediately.
                if (sessionStarts.TryGetValue(game.Id, out var start) && ShouldShowPresence() && !overrides.IsHidden(game.Id))
                {
                    var token = BeginNewUpdate();
                    Task.Run(() => UpdatePresenceForGameAsync(context, start, token), token);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPresence: refresh metadata failed.");
            }
        }

        private void SetIgdbIdManually(Game game)
        {
            try
            {
                var existing = overrides.GetIgdbOverride(game.Id);
                var result = PlayniteApi.Dialogs.SelectString(
                    LocalizationLoader.Loc("LOCPlayPresence_SetIdPrompt"),
                    MenuSection,
                    existing.HasValue ? existing.Value.ToString() : string.Empty);

                if (result == null || !result.Result)
                {
                    return;
                }

                long id;
                if (!long.TryParse((result.SelectedString ?? string.Empty).Trim(), out id) || id <= 0)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(LocalizationLoader.Loc("LOCPlayPresence_SetIdInvalid"), MenuSection);
                    return;
                }

                overrides.SetIgdbOverride(game.Id, id);
                var context = BuildGameContext(game);
                lock (stackLock)
                {
                    metadataResolver?.Invalidate(context);
                }
                PlayniteApi.Dialogs.ShowMessage(LocalizationLoader.Loc("LOCPlayPresence_OverrideApplied"), MenuSection);

                if (sessionStarts.TryGetValue(game.Id, out var start) && ShouldShowPresence() && !overrides.IsHidden(game.Id))
                {
                    var token = BeginNewUpdate();
                    Task.Run(() => UpdatePresenceForGameAsync(context, start, token), token);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPresence: set IGDB id failed.");
            }
        }

        private void ClearIgdbOverride(Game game)
        {
            overrides.ClearIgdbOverride(game.Id);
            var context = BuildGameContext(game);
            lock (stackLock)
            {
                metadataResolver?.Invalidate(context);
            }
            RefreshGameMetadata(game);
        }

        private void ToggleHidden(Game game)
        {
            var nowHidden = overrides.ToggleHidden(game.Id);
            // If the game is running, reflect the change immediately.
            if (sessionStarts.ContainsKey(game.Id))
            {
                if (nowHidden)
                {
                    CancelActiveUpdate();
                    ShowIdlePresence();
                }
                else
                {
                    RefreshPresenceForCurrentState();
                }
            }
        }

        private void TogglePrivateMode()
        {
            Settings.PrivateMode = !Settings.PrivateMode;
            try { SavePluginSettings(Settings); } catch (Exception ex) { Logger.Warn(ex, "PlayPresence: save private mode failed."); }
            RefreshPresenceForCurrentState();
        }

        // ----- Core presence flow ------------------------------------------

        private async Task UpdatePresenceForGameAsync(GameContext context, DateTime startUtc, CancellationToken token)
        {
            try
            {
                IMetadataResolver resolver;
                lock (stackLock)
                {
                    resolver = metadataResolver;
                }

                var metadata = await resolver.ResolveAsync(context, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var presence = PresenceBuilder.Build(BuildPresenceOptions(), context, metadata, startUtc);

                if (!token.IsCancellationRequested && ShouldShowPresence())
                {
                    discord.SetPresence(presence);
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded or stopped.
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPresence: failed to update presence; showing a minimal fallback.");
                TrySetMinimalPresence(context, startUtc);
            }
        }

        private void TrySetMinimalPresence(GameContext context, DateTime startUtc)
        {
            try
            {
                var fallback = new ResolvedMetadata { Title = context.GameName };
                var presence = PresenceBuilder.Build(BuildPresenceOptions(), context, fallback, startUtc);
                discord.SetPresence(presence);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: minimal fallback presence also failed.");
            }
        }

        private void ShowIdlePresence()
        {
            if (Settings.PrivateMode || !Settings.EnableRichPresence || !Settings.EnableIdlePresence || !ShouldShowPresence())
            {
                discord.ClearPresence();
                return;
            }

            try
            {
                var idle = PresenceBuilder.BuildIdle(Settings.IdlePresenceText, appStartUtc);
                discord.SetPresence(idle);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to set idle presence.");
            }
        }

        private void RefreshPresenceForCurrentState()
        {
            var running = sessionStarts.FirstOrDefault();
            if (!running.Equals(default(KeyValuePair<Guid, DateTime>)) && !overrides.IsHidden(running.Key))
            {
                var game = PlayniteApi.Database.Games.Get(running.Key);
                if (game != null && ShouldShowPresence())
                {
                    var token = BeginNewUpdate();
                    var context = BuildGameContext(game);
                    Task.Run(() => UpdatePresenceForGameAsync(context, running.Value, token), token);
                    return;
                }
            }

            ShowIdlePresence();
        }

        // ----- Context assembly --------------------------------------------

        private GameContext BuildGameContext(Game game)
        {
            var platform = game.Platforms != null && game.Platforms.Count > 0 ? game.Platforms[0] : null;
            var identity = PlatformResolver.Resolve(platform?.SpecificationId, platform?.Name);

            var context = new GameContext
            {
                GameName = game.Name,
                PlatformName = identity.FriendlyName,
                PlatformSpecificationId = platform?.SpecificationId,
                IgdbPlatformName = identity.IgdbPlatformName,
                Source = game.Source?.Name,
                EmulatorName = ResolveEmulatorName(game),
                ReleaseYear = game.ReleaseDate?.Year,
                IsFavorite = game.Favorite,
                CompletionStatus = game.CompletionStatus?.Name,
                LocalCoverPath = game.CoverImage,
                IgdbIdOverride = overrides.GetIgdbOverride(game.Id)
            };

            string discLabel;
            context.CandidateNames = BuildCandidateNames(game, out discLabel);
            context.DiscLabel = discLabel;
            return context;
        }

        private List<string> BuildCandidateNames(Game game, out string discLabel)
        {
            discLabel = null;
            var romNames = new List<string>();

            if (game.Roms != null)
            {
                foreach (var rom in game.Roms)
                {
                    var raw = !string.IsNullOrWhiteSpace(rom?.Name) ? rom.Name : SafeFileName(rom?.Path);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var normalized = RomNameNormalizer.Normalize(raw);
                    if (!string.IsNullOrWhiteSpace(normalized.CleanTitle))
                    {
                        romNames.Add(normalized.CleanTitle);
                    }
                    if (discLabel == null && normalized.DiscNumber.HasValue)
                    {
                        discLabel = "Disc " + normalized.DiscNumber.Value;
                    }
                }
            }

            var names = new List<string>();
            if (Settings.PreferRomFilename)
            {
                names.AddRange(romNames);
                if (!string.IsNullOrWhiteSpace(game.Name)) names.Add(game.Name);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(game.Name)) names.Add(game.Name);
                names.AddRange(romNames);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (var n in names)
            {
                if (seen.Add(n)) ordered.Add(n);
            }
            if (ordered.Count == 0 && !string.IsNullOrWhiteSpace(game.Name)) ordered.Add(game.Name);
            return ordered;
        }

        private string ResolveEmulatorName(Game game)
        {
            try
            {
                if (game.GameActions == null)
                {
                    return null;
                }
                foreach (var action in game.GameActions)
                {
                    if (action != null && action.Type == GameActionType.Emulator && action.EmulatorId != Guid.Empty)
                    {
                        var emulator = PlayniteApi.Database.Emulators?.Get(action.EmulatorId);
                        if (emulator != null)
                        {
                            return emulator.Name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: emulator name resolution failed.");
            }
            return null;
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            try { return System.IO.Path.GetFileName(path); }
            catch { return path; }
        }

        // ----- Mode / toggle helpers ---------------------------------------

        private bool ShouldShowPresence()
        {
            if (Settings.PrivateMode || !Settings.EnableRichPresence)
            {
                return false;
            }
            var fullscreen = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            return fullscreen ? Settings.ShowInFullscreenMode : Settings.ShowInDesktopMode;
        }

        private void ReinitDiscordFromSettings()
        {
            DiscordPresenceService.Verbose = Settings.EnableDebugLogging;
            if (Settings.PrivateMode || !Settings.EnableRichPresence)
            {
                if (Settings.EnableDebugLogging)
                {
                    Logger.Info("PlayPresence: Rich Presence disabled (PrivateMode or EnableRichPresence off); not connecting.");
                }
                discord.Shutdown();
                return;
            }
            var appId = EffectiveDiscordAppId();
            if (Settings.EnableDebugLogging)
            {
                Logger.Info("PlayPresence: connecting to Discord. App ID length=" + (appId ?? string.Empty).Trim().Length + ".");
            }
            discord.Initialize(appId);
        }

        private CancellationToken BeginNewUpdate()
        {
            lock (ctsLock)
            {
                activeUpdateCts?.Cancel();
                activeUpdateCts?.Dispose();
                activeUpdateCts = new CancellationTokenSource();
                return activeUpdateCts.Token;
            }
        }

        private void CancelActiveUpdate()
        {
            lock (ctsLock)
            {
                activeUpdateCts?.Cancel();
                activeUpdateCts?.Dispose();
                activeUpdateCts = null;
            }
        }

        // ----- IPlayPresenceController -------------------------------------------

        public void ReconnectDiscord()
        {
            ReinitDiscordFromSettings();
            discord.Reconnect();
            RefreshPresenceForCurrentState();
        }

        public void ClearMetadataCache()
        {
            lock (stackLock)
            {
                metadataCache?.Clear();
            }
        }

        public async Task<string> TestConnectionAsync(PluginSettings candidate)
        {
            var lines = new List<string>();

            var effectiveDiscordId = !string.IsNullOrWhiteSpace(candidate.DiscordApplicationId)
                ? candidate.DiscordApplicationId
                : BundledCredentials.DiscordApplicationId;

            if (string.IsNullOrWhiteSpace(effectiveDiscordId))
            {
                lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestDiscordEmpty"));
            }
            else if (effectiveDiscordId.Trim().Length < 17)
            {
                lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestDiscordShort"));
            }
            else
            {
                lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestDiscordOk"));
            }

            string testId, testSecret;
            ResolveIgdbCredentials(candidate, out testId, out testSecret);

            if (!candidate.EnableIgdb)
            {
                lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestIgdbDisabled"));
            }
            else if (string.IsNullOrWhiteSpace(testId) || string.IsNullOrWhiteSpace(testSecret))
            {
                lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestIgdbEmpty"));
            }
            else
            {
                using (var testAuth = new IgdbAuthClient(httpClient))
                {
                    testAuth.Configure(testId, testSecret);
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        {
                            var token = await testAuth.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                            lines.Add(LocalizationLoader.Loc(string.IsNullOrEmpty(token)
                                ? "LOCPlayPresence_TestIgdbFail" : "LOCPlayPresence_TestIgdbOk"));
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add(LocalizationLoader.Loc("LOCPlayPresence_TestIgdbFail") + " — " + ex.Message);
                    }
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        // ----- Disposal -----------------------------------------------------

        public override void Dispose()
        {
            CancelActiveUpdate();
            try { discord.Dispose(); } catch { /* ignore */ }

            lock (stackLock)
            {
                try { igdbClient?.Dispose(); } catch { /* ignore */ }
                try { igdbAuth?.Dispose(); } catch { /* ignore */ }
                try { steamGridDb?.Dispose(); } catch { /* ignore */ }
                try { googleImages?.Dispose(); } catch { /* ignore */ }
            }

            try { httpClient.Dispose(); } catch { /* ignore */ }
            base.Dispose();
        }
    }
}
