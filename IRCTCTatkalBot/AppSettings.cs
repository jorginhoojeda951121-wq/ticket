using System;
using System.IO;
using System.Text.Json;

namespace IRCTCTatkalBot
{
    /// <summary>
    /// Global application settings persisted to settings.json.
    /// </summary>
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IRCTCTatkalBot", "settings.json");

        private static AppSettings? _instance;
        public static AppSettings Instance => _instance ??= Load();

        // Captcha
        public string TwoCaptchaApiKey { get; set; } = string.Empty;
        /// <summary>Separate key for Anti-Captcha; if empty, <see cref="TwoCaptchaApiKey"/> is used when provider is anticaptcha.</summary>
        public string AntiCaptchaApiKey { get; set; } = string.Empty;
        /// <summary>2captcha | anticaptcha | manual (no API key; you type captcha in Chrome).</summary>
        public string CaptchaProvider { get; set; } = "2captcha";

        // Convenience static accessor
        public static string CaptchaApiKey => Instance.TwoCaptchaApiKey;

        // Booking defaults
        public int DefaultRetries { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 500;

        // UI
        public bool DarkMode { get; set; } = true;
        public bool ShowBrowserWindows { get; set; } = true;

        // Chrome / ChromeDriver (optional path; empty = auto-detect)
        public string ChromeBinaryPath { get; set; } = string.Empty;

        /// <summary>Delay before each account starts its driver (reduces parallel-start flakiness on Windows).</summary>
        public int StaggerMsBetweenDriverStarts { get; set; } = 700;

        public int ChromeDriverStartupRetries { get; set; } = 3;
        public int ChromeDriverRetryDelayMs { get; set; } = 2000;
        public int ChromeDriverInitTimeoutSeconds { get; set; } = 120;
        public int ChromeDriverCommandTimeoutSeconds { get; set; } = 180;

        /// <summary>Pause after a successful driver start before releasing the global lock.</summary>
        public int PostDriverStartCooldownMs { get; set; } = 450;

        // ── Persistence ───────────────────────────────────────────────

        public void Save()
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static AppSettings Load()
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }
}
