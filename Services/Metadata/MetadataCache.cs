using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PlayPresence.Models;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayPresence.Services.Metadata
{
    /// <summary>
    /// Two-tier cache (in-memory + JSON-on-disk) for resolved metadata, so repeated launches of
    /// the same game never re-query IGDB. Thread-safe and resilient to corrupt cache files.
    /// </summary>
    public sealed class MetadataCache
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly ConcurrentDictionary<string, ResolvedMetadata> memory =
            new ConcurrentDictionary<string, ResolvedMetadata>(StringComparer.OrdinalIgnoreCase);

        private readonly string cacheDirectory;

        public MetadataCache(string cacheDirectory)
        {
            this.cacheDirectory = cacheDirectory;
            try
            {
                Directory.CreateDirectory(cacheDirectory);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: could not create metadata cache directory.");
            }
        }

        public bool TryGet(string key, int maxAgeDays, out ResolvedMetadata metadata)
        {
            metadata = null;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (!memory.TryGetValue(key, out metadata))
            {
                metadata = ReadFromDisk(key);
                if (metadata != null)
                {
                    memory[key] = metadata;
                }
            }

            if (metadata == null)
            {
                return false;
            }

            if (maxAgeDays > 0 && metadata.ResolvedAtUtc.AddDays(maxAgeDays) < DateTime.UtcNow)
            {
                return false; // expired; caller will refresh
            }
            return true;
        }

        public void Set(string key, ResolvedMetadata metadata)
        {
            if (string.IsNullOrEmpty(key) || metadata == null)
            {
                return;
            }

            metadata.ResolvedAtUtc = DateTime.UtcNow;
            memory[key] = metadata;
            WriteToDisk(key, metadata);
        }

        /// <summary>Removes a single entry from memory and disk (used by manual refresh).</summary>
        public void Remove(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            ResolvedMetadata ignored;
            memory.TryRemove(key, out ignored);
            try
            {
                var path = PathForKey(key);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to remove cache entry.");
            }
        }

        public void Clear()
        {
            memory.Clear();
            try
            {
                if (Directory.Exists(cacheDirectory))
                {
                    foreach (var file in Directory.GetFiles(cacheDirectory, "*.json"))
                    {
                        try { File.Delete(file); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to clear metadata cache.");
            }
        }

        private ResolvedMetadata ReadFromDisk(string key)
        {
            try
            {
                var path = PathForKey(key);
                if (!File.Exists(path))
                {
                    return null;
                }
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<ResolvedMetadata>(json);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to read cache entry; ignoring.");
                return null;
            }
        }

        private void WriteToDisk(string key, ResolvedMetadata metadata)
        {
            try
            {
                var json = JsonConvert.SerializeObject(metadata, Formatting.Indented);
                File.WriteAllText(PathForKey(key), json);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to write cache entry.");
            }
        }

        private string PathForKey(string key)
        {
            return Path.Combine(cacheDirectory, Hash(key) + ".json");
        }

        private static string Hash(string value)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
