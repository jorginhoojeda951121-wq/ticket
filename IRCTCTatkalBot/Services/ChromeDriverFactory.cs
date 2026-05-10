using System;
using System.IO;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using IRCTCTatkalBot;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Central place to construct <see cref="ChromeDriver"/> with retries, logging, and Windows-friendly pacing.
    /// </summary>
    public static class ChromeDriverFactory
    {
        private static readonly object SyncRoot = new();

        /// <summary>
        /// Creates a <see cref="ChromeDriver"/> with settings-driven timeouts and retries.
        /// </summary>
        public static ChromeDriver Create(ChromeOptions options)
        {
            var settings = AppSettings.Instance;
            int retries = Math.Max(1, settings.ChromeDriverStartupRetries);
            int delayMs = Math.Max(200, settings.ChromeDriverRetryDelayMs);
            var initTimeout = TimeSpan.FromSeconds(Math.Max(30, settings.ChromeDriverInitTimeoutSeconds));
            var cmdTimeout = TimeSpan.FromSeconds(Math.Max(60, settings.ChromeDriverCommandTimeoutSeconds));
            int cooldown = Math.Max(0, settings.PostDriverStartCooldownMs);

            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "chromedriver_last.log");

            WebDriverException? last = null;

            lock (SyncRoot)
            {
                for (int attempt = 1; attempt <= retries; attempt++)
                {
                    try
                    {
                        if (attempt > 1)
                        {
                            Logger.Warning($"ChromeDriverFactory: retry {attempt}/{retries} after {delayMs}ms delay.");
                            Thread.Sleep(delayMs);
                        }

                        // Prefer Selenium Manager auto-resolution so ChromeDriver always matches
                        // the installed Chrome version (instead of relying on a stale local EXE).
                        try
                        {
                            var managedDriver = new ChromeDriver(options);
                            if (cooldown > 0)
                                Thread.Sleep(cooldown);
                            return managedDriver;
                        }
                        catch (WebDriverException ex) when (IsVersionMismatch(ex))
                        {
                            Logger.Warning($"ChromeDriverFactory: Selenium Manager attempt reported mismatch; trying local service fallback. {ex.Message}");
                        }

                        var service = ChromeDriverService.CreateDefaultService();
                        service.InitializationTimeout = initTimeout;
                        service.LogPath = logPath;
                        service.EnableVerboseLogging = true;

                        var driver = new ChromeDriver(service, options, cmdTimeout);
                        if (cooldown > 0)
                            Thread.Sleep(cooldown);
                        return driver;
                    }
                    catch (WebDriverException ex)
                    {
                        last = ex;
                        Logger.Warning($"ChromeDriverFactory: attempt {attempt}/{retries} — {ex.Message}");
                    }
                }
            }

            string tail = DriverDiagnostics.ReadChromedriverLogTail();
            string msg =
                (last?.Message ?? "ChromeDriver failed to start.") +
                Environment.NewLine + Environment.NewLine +
                DriverDiagnostics.BuildChecklist() +
                Environment.NewLine + Environment.NewLine +
                "---- chromedriver_last.log (tail) ----" +
                Environment.NewLine +
                tail;

            throw new WebDriverException(msg, last);
        }

        private static bool IsVersionMismatch(WebDriverException ex)
        {
            string message = ex.Message ?? string.Empty;
            return message.Contains("only supports Chrome version", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("session not created", StringComparison.OrdinalIgnoreCase);
        }
    }
}
