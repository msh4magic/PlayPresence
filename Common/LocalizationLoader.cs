using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using Playnite.SDK;

namespace PlayPresence.Common
{
    /// <summary>
    /// Loads the plugin's localization dictionaries and merges them into the application's resources
    /// so that <c>{DynamicResource LOCPlayPresence_*}</c> keys resolve in XAML and <see cref="Loc"/> resolves
    /// them in code. English is merged first as the guaranteed fallback, then the user's language is
    /// merged on top when available. This belt-and-braces approach means the UI is never left showing
    /// raw resource keys, regardless of how the host loads extension localization.
    /// </summary>
    public static class LocalizationLoader
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static bool loaded;

        public static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;

            try
            {
                var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(baseDir))
                {
                    return;
                }

                var locDir = Path.Combine(baseDir, "Localization");

                // 1) English is always merged first as the fallback.
                Merge(Path.Combine(locDir, "en_US.xaml"));

                // 2) Merge the current UI language on top, if we ship it.
                var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                if (string.Equals(lang, "ar", StringComparison.OrdinalIgnoreCase))
                {
                    Merge(Path.Combine(locDir, "ar_SA.xaml"));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: localization load failed; using built-in fallbacks.");
            }
        }

        private static void Merge(string path)
        {
            try
            {
                if (!File.Exists(path) || Application.Current == null)
                {
                    return;
                }

                using (var stream = File.OpenRead(path))
                {
                    var dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
                    Application.Current.Resources.MergedDictionaries.Add(dict);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"PlayPresence: failed to merge localization file '{path}'.");
            }
        }

        /// <summary>
        /// Resolves a localization key to its string, falling back to <paramref name="fallback"/>
        /// (or the key itself) if it isn't present. Safe to call from any thread.
        /// </summary>
        public static string Loc(string key, string fallback = null)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.Resources.Contains(key))
                {
                    var value = app.Resources[key] as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // fall through to fallback
            }
            return fallback ?? key;
        }
    }
}
