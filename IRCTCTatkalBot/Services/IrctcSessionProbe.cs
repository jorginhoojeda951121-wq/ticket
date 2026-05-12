using System;
using OpenQA.Selenium;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Single place for "does this tab look authenticated?" so login, IsLoggedIn, and booking stay aligned.
    /// </summary>
    public static class IrctcSessionProbe
    {
        /// <summary>
        /// True when the UI/cookies strongly suggest an authenticated IRCTC session.
        /// Positive signals are evaluated before treating a visible LOGIN chip as logged-out.
        /// </summary>
        public static bool AppearsLoggedIn(IWebDriver driver)
        {
            try
            {
                if (LooksLikeActiveBookingFlow(driver))
                    return true;

                if (HasPositiveAuthMarkers(driver))
                    return true;

                if (HasLikelySessionCookie(driver))
                    return true;

                // Visible LOGIN alone — only after positives/cookies failed (order matters).
                if (AnyVisibleLoginChip(driver))
                    return false;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Same markers as <see cref="AppearsLoggedIn"/> but lightweight for quick checks after navigation.</summary>
        public static bool SessionLikelyAlive(IWebDriver driver)
        {
            try
            {
                if (LooksLikeActiveBookingFlow(driver))
                    return true;
                if (HasPositiveAuthMarkers(driver))
                    return true;
                if (HasLikelySessionCookie(driver))
                    return true;
                string src = driver.PageSource ?? "";
                if (src.Contains("Logout", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("MY ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("My Account", StringComparison.OrdinalIgnoreCase) ||
                    (src.Contains("Welcome", StringComparison.OrdinalIgnoreCase) && src.Contains("(", StringComparison.Ordinal)))
                    return true;
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool LooksLikeActiveBookingFlow(IWebDriver driver)
        {
            try
            {
                string url = driver.Url ?? "";
                if (!url.Contains("train-list", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("/nget/booking/", StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var by in new[]
                         {
                             By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'modify search')]"),
                             By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'results for')]"),
                             By.CssSelector("app-train-list"),
                         })
                {
                    foreach (var el in driver.FindElements(by))
                    {
                        try
                        {
                            if (el.Displayed)
                                return true;
                        }
                        catch { /* stale */ }
                    }
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool HasPositiveAuthMarkers(IWebDriver driver)
        {
            try
            {
                if (driver.FindElements(By.XPath("//a[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'logout') or contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'log out')]")).Count > 0)
                    return true;
                if (driver.FindElements(By.CssSelector("a[href*='logout']")).Count > 0)
                    return true;
                if (driver.FindElements(By.XPath("//*[contains(normalize-space(.),'Welcome') and contains(.,'(')]")).Count > 0)
                    return true;
                // Header menu variants
                if (driver.FindElements(By.XPath("//*[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'my account')]")).Count > 0)
                    return true;

                string src = driver.PageSource ?? "";
                if (src.Contains("Logout", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (src.Contains("MY ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("My Account", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool HasLikelySessionCookie(IWebDriver driver)
        {
            try
            {
                foreach (var c in driver.Manage().Cookies.AllCookies)
                {
                    string name = c.Name ?? "";
                    if (name.Length == 0 || string.IsNullOrEmpty(c.Value))
                        continue;
                    // IRCTC / gateway cookies — heuristic names change over time; keep broad.
                    if (name.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("JWT", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("SESSION", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("IRCTC", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("USER", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        private static bool AnyVisibleLoginChip(IWebDriver driver)
        {
            foreach (var el in driver.FindElements(By.XPath("//a[contains(normalize-space(.),'LOGIN') or contains(normalize-space(.),'Login')] | //button[contains(normalize-space(.),'LOGIN') or contains(normalize-space(.),'Login')]")))
            {
                try
                {
                    if (el.Displayed)
                        return true;
                }
                catch { /* stale */ }
            }

            return false;
        }
    }
}
