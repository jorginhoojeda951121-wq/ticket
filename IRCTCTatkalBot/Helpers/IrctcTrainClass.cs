using System;
using System.Collections.Generic;
using System.Linq;

namespace IRCTCTatkalBot.Helpers
{
    /// <summary>
    /// Canonical IRCTC booking classes shown in the app UI (1AC, 2AC, 3AC, SL, CC, 2S).
    /// Maps to site labels (often 1A/2A/3A on IRCTC) for search dropdowns and Tatkal window rules.
    /// </summary>
    public static class IrctcTrainClass
    {
        public static readonly string[] AllowedUiValues = { "1AC", "2AC", "3AC", "SL", "CC", "2S" };

        /// <summary>Legacy codes still accepted from old configs.</summary>
        private static readonly HashSet<string> LegacyAllowed =
            new(StringComparer.OrdinalIgnoreCase) { "1A", "2A", "3A" };

        public static bool IsAllowed(string? trainClass)
        {
            string u = (trainClass ?? "").Trim();
            if (u.Length == 0)
                return false;
            return AllowedUiValues.Any(x => x.Equals(u, StringComparison.OrdinalIgnoreCase))
                   || LegacyAllowed.Contains(u);
        }

        /// <summary>1A/2A/3A → 1AC/2AC/3AC for internal logic.</summary>
        public static string NormalizeToCanonical(string? trainClass)
        {
            string u = (trainClass ?? "").Trim().ToUpperInvariant();
            return u switch
            {
                "1A" => "1AC",
                "2A" => "2AC",
                "3A" => "3AC",
                _ => u
            };
        }

        /// <summary>Strings to try when matching journey-class options on train-search (IRCTC wording varies).</summary>
        public static string[] JourneySearchAliases(string? trainClass)
        {
            string c = NormalizeToCanonical(trainClass);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c };
            switch (c)
            {
                case "1AC":
                    set.Add("1A");
                    set.Add("FIRST");
                    set.Add("1 AC");
                    break;
                case "2AC":
                    set.Add("2A");
                    set.Add("2 AC");
                    break;
                case "3AC":
                    set.Add("3A");
                    set.Add("3 AC");
                    set.Add("THIRD");
                    break;
                case "SL":
                    set.Add("SLEEPER");
                    break;
                case "CC":
                    set.Add("CHAIR");
                    break;
                case "2S":
                    set.Add("SECOND");
                    break;
            }

            return set.ToArray();
        }

        /// <summary>Tatkal window: SL and 2S open at 11:00 IST; AC family at 10:00 IST.</summary>
        public static TimeSpan TatkalWindowOpen(string? trainClass)
        {
            string c = NormalizeToCanonical(trainClass);
            return c is "SL" or "2S"
                ? new TimeSpan(11, 0, 0)
                : new TimeSpan(10, 0, 0);
        }
    }
}
