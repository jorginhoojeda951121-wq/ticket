using System;
using System.IO;
using System.Text;

namespace IRCTCTatkalBot.Helpers
{
    /// <summary>
    /// User-facing text when Chrome / ChromeDriver fails to start (localhost, AV, VPN, etc.).
    /// </summary>
    public static class DriverDiagnostics
    {
        public static string ChromedriverLogPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "chromedriver_last.log");

        /// <summary>Short line for results grid or log lines.</summary>
        public static string FormatShort(Exception ex)
        {
            if (ex == null) return "Driver start failed.";
            string m = ex.Message;
            if (m.Length > 280) m = m.Substring(0, 277) + "...";
            return m;
        }

        public static string ReadChromedriverLogTail(int maxChars = 3500)
        {
            try
            {
                string path = ChromedriverLogPath;
                if (!File.Exists(path)) return "(chromedriver_last.log not found or not written yet.)";

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long len = fs.Length;
                int read = (int)Math.Min(len, maxChars);
                fs.Seek(-read, SeekOrigin.End);
                var buf = new byte[read];
                _ = fs.Read(buf, 0, read);
                return Encoding.UTF8.GetString(buf);
            }
            catch (Exception ex)
            {
                return "(Could not read chromedriver_last.log: " + ex.Message + ")";
            }
        }

        public static string BuildChecklist()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "Things to try:",
                "• Turn ON \"Show Chrome windows\" (disable headless) and test again.",
                "• Run with one active account first.",
                "• Exclude this app folder, Chrome, and chromedriver (often under %USERPROFILE%\\.cache\\selenium) from real-time antivirus.",
                "• Temporarily disable VPN / corporate proxy and retry.",
                "• Open Logs\\chromedriver_last.log for ChromeDriver details.",
            });
        }

        /// <summary>Full text for a MessageBox after a failed booking run.</summary>
        public static string BuildUserFacingMessage(Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ex.Message);
            sb.AppendLine();
            sb.AppendLine(BuildChecklist());

            if (IsLikelyDriverStartupFailure(ex))
            {
                sb.AppendLine();
                sb.AppendLine("---- chromedriver_last.log (tail) ----");
                sb.AppendLine(ReadChromedriverLogTail());
            }

            return sb.ToString();
        }

        public static bool IsLikelyDriverStartupFailure(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                string t = e.GetType().FullName ?? "";
                if (t.Contains("WebDriver", StringComparison.OrdinalIgnoreCase)) return true;
                if (e.Message.Contains("driver service", StringComparison.OrdinalIgnoreCase)) return true;
                if (e.Message.Contains("ChromeDriver", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
