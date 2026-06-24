using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayPresence.Services.Overrides
{
    /// <summary>Serializable on-disk shape for the override store.</summary>
    internal sealed class OverrideData
    {
        public Dictionary<string, long> IgdbOverrides { get; set; } = new Dictionary<string, long>();
        public List<string> HiddenGames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Per-game user overrides that should survive restarts: a manual IGDB id (to fix a wrong match)
    /// and a "hide from Discord" flag (for private games). Persisted as a single small JSON file in
    /// the plugin's data folder. All access is synchronized and failure-tolerant.
    /// </summary>
    public sealed class GameOverrideStore
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly string filePath;
        private readonly object sync = new object();

        private Dictionary<string, long> igdbOverrides = new Dictionary<string, long>();
        private HashSet<string> hiddenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GameOverrideStore(string dataDirectory)
        {
            filePath = Path.Combine(dataDirectory, "overrides.json");
            Load();
        }

        public long? GetIgdbOverride(Guid gameId)
        {
            lock (sync)
            {
                return igdbOverrides.TryGetValue(Key(gameId), out var id) ? id : (long?)null;
            }
        }

        public void SetIgdbOverride(Guid gameId, long igdbId)
        {
            lock (sync)
            {
                igdbOverrides[Key(gameId)] = igdbId;
                Save();
            }
        }

        public void ClearIgdbOverride(Guid gameId)
        {
            lock (sync)
            {
                if (igdbOverrides.Remove(Key(gameId)))
                {
                    Save();
                }
            }
        }

        public bool IsHidden(Guid gameId)
        {
            lock (sync)
            {
                return hiddenGames.Contains(Key(gameId));
            }
        }

        /// <summary>Toggles the hidden flag and returns the new state.</summary>
        public bool ToggleHidden(Guid gameId)
        {
            lock (sync)
            {
                var key = Key(gameId);
                bool nowHidden;
                if (hiddenGames.Contains(key))
                {
                    hiddenGames.Remove(key);
                    nowHidden = false;
                }
                else
                {
                    hiddenGames.Add(key);
                    nowHidden = true;
                }
                Save();
                return nowHidden;
            }
        }

        private static string Key(Guid gameId)
        {
            return gameId.ToString("N");
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }
                var json = File.ReadAllText(filePath);
                var data = Serialization.FromJson<OverrideData>(json);
                if (data != null)
                {
                    igdbOverrides = data.IgdbOverrides ?? new Dictionary<string, long>();
                    hiddenGames = new HashSet<string>(
                        data.HiddenGames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to load overrides; starting fresh.");
                igdbOverrides = new Dictionary<string, long>();
                hiddenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var data = new OverrideData
                {
                    IgdbOverrides = igdbOverrides,
                    HiddenGames = new List<string>(hiddenGames)
                };
                File.WriteAllText(filePath, Serialization.ToJson(data, true));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to save overrides.");
            }
        }
    }
}
