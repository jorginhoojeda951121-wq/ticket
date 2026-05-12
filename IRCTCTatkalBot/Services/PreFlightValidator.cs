using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.ViewModels;

namespace IRCTCTatkalBot.Services
{
    public static class PreFlightValidator
    {
        private static readonly string[] ChromePaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
        };

        public static IReadOnlyList<string> Validate(MainViewModel vm, AccountManager accounts)
        {
            var errors = new List<string>();

            if (ChromePaths.All(p => !File.Exists(p)))
                errors.Add("Google Chrome not found in standard install paths.");

            var active = accounts.GetActive();
            if (active.Count == 0)
                errors.Add("Add at least one active IRCTC account.");

            var settings = AppSettings.Instance;
            var provider = (settings.CaptchaProvider ?? "2captcha").Trim().ToLowerInvariant();
            if (provider is "manual" or "none" or "off")
            {
                if (!settings.ShowBrowserWindows)
                    errors.Add("Manual captcha needs a visible browser — turn on \"Show Chrome windows\".");
            }
            else if (provider is "anticaptcha" or "anti-captcha")
            {
                string key = string.IsNullOrWhiteSpace(settings.AntiCaptchaApiKey)
                    ? settings.TwoCaptchaApiKey
                    : settings.AntiCaptchaApiKey;
                if (string.IsNullOrWhiteSpace(key))
                    errors.Add("Anti-Captcha client key is missing (use Anti-Captcha key field or main API key).");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(settings.TwoCaptchaApiKey))
                    errors.Add("2Captcha API key is missing (or choose Captcha provider: Manual).");
            }

            if (string.IsNullOrWhiteSpace(vm.FromStation))
                errors.Add("From station is required.");
            if (string.IsNullOrWhiteSpace(vm.ToStation))
                errors.Add("To station is required.");

            // IRCTC booking calendar follows IST; comparing to local PC date can wrongly reject/accept a journey date.
            DateTime indiaToday = GetIndiaDateToday();
            if (vm.JourneyDate.Date < indiaToday)
                errors.Add($"Journey date cannot be in the past (India date today is {indiaToday:yyyy-MM-dd}).");

            if (string.IsNullOrWhiteSpace(vm.TrainClass))
                errors.Add("Train class is required.");
            else if (!IrctcTrainClass.IsAllowed(vm.TrainClass))
                errors.Add($"Class must be one of: {string.Join(", ", IrctcTrainClass.AllowedUiValues)} (legacy 1A / 2A / 3A is still accepted).");

            string trainNo = (vm.TrainNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trainNo))
                errors.Add("Train number is required. Many trains share the same route — enter the numeric train number (e.g. 12215) so the correct train is selected.");
            else if (!IsValidIrctcTrainNumber(trainNo))
                errors.Add($"Train number must be 4–6 digits only (got \"{trainNo}\"). Example: 12215.");

            var pax = vm.GetPassengersSnapshot();
            if (pax.Count == 0)
                errors.Add("Add at least one passenger.");
            else
            {
                int missingNames = pax.Count(p => string.IsNullOrWhiteSpace(p.Name));
                if (missingNames == 1)
                    errors.Add("One passenger has no name — fill the Name column before starting.");
                else if (missingNames > 1)
                    errors.Add($"{missingNames} passengers have no name — fill the Name column for each row (or remove extra rows).");

                foreach (var p in pax)
                {
                    if (p.Age <= 0 || p.Age > 120)
                    {
                        string label = string.IsNullOrWhiteSpace(p.Name) ? "(unnamed passenger)" : $"'{p.Name.Trim()}'";
                        errors.Add($"Invalid age for {label}: use a value between 1 and 120.");
                    }
                }
            }

            return errors;
        }

        /// <summary>IRCTC passenger train numbers are typically 4–6 digits.</summary>
        public static bool IsValidIrctcTrainNumber(string? trainNumber)
        {
            string t = (trainNumber ?? "").Trim();
            return t.Length > 0 && Regex.IsMatch(t, @"^\d{4,6}$");
        }

        private static DateTime GetIndiaDateToday()
        {
            try
            {
                var india = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, india).Date;
            }
            catch
            {
                return DateTime.Today;
            }
        }
    }
}
