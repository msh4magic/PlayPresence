using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayPresence.Settings
{
    /// <summary>
    /// Narrow surface the settings view-model uses to drive the plugin (apply settings, reconnect,
    /// run a live credentials test, clear the cache). Declared as an interface so the view-model
    /// stays decoupled from the concrete plugin class and remains easy to reason about.
    /// </summary>
    public interface IPlayPresenceController
    {
        /// <summary>Playnite API, used by the view-model for dialogs and notifications.</summary>
        IPlayniteAPI PlayniteApi { get; }

        /// <summary>Re-applies saved settings to the live Discord/IGDB services.</summary>
        void OnSettingsSaved();

        /// <summary>Forces a clean Discord reconnect.</summary>
        void ReconnectDiscord();

        /// <summary>Clears the on-disk and in-memory metadata cache.</summary>
        void ClearMetadataCache();

        /// <summary>
        /// Validates the candidate Discord/IGDB credentials live and returns a short,
        /// human-readable result message (already localized by the caller's expectations).
        /// </summary>
        Task<string> TestConnectionAsync(PluginSettings candidate);
    }
}
