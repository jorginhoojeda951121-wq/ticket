using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Solves IRCTC captchas via the 2Captcha API.
    ///
    /// Workflow:
    ///   1. Submit the captcha image (Base64 or URL) → get task ID
    ///   2. Poll until the answer arrives (usually 5–20 s)
    ///   3. Return the text answer to the caller
    ///
    /// Set your 2Captcha API key in AppSettings before use.
    /// </summary>
    public class CaptchaSolver : ICaptchaSolver
    {
        private const string SubmitUrl = "https://2captcha.com/in.php";
        private const string ResultUrl = "https://2captcha.com/res.php";
        private const int PollIntervalMs = 2_000;
        private const int MaxPollAttempts = 30; // 60 s max

        private readonly HttpClient _http;
        public string ApiKey { get; set; } = string.Empty;

        public CaptchaSolver(string apiKey = "")
        {
            ApiKey = apiKey;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ── Image Captcha (login page) ─────────────────────────────────

        /// <summary>
        /// Solves an image captcha by submitting raw Base64 image data.
        /// </summary>
        /// <param name="base64Image">Base64-encoded PNG/JPG of the captcha.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Solved captcha text, or null on failure.</returns>
        public async Task<string?> SolveImageCaptchaAsync(string base64Image,
            CancellationToken ct = default)
        {
            Logger.Info("CaptchaSolver: Submitting image captcha to 2Captcha...");

            var content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("key", ApiKey),
                new System.Collections.Generic.KeyValuePair<string, string>("method", "base64"),
                new System.Collections.Generic.KeyValuePair<string, string>("body", base64Image),
                new System.Collections.Generic.KeyValuePair<string, string>("json", "0")
            });

            string taskId = await SubmitAsync(content, ct);
            if (string.IsNullOrEmpty(taskId)) return null;

            return await PollForResultAsync(taskId, ct);
        }

        /// <summary>
        /// Solves reCAPTCHA v2 (if IRCTC ever uses it).
        /// </summary>
        public async Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl,
            CancellationToken ct = default)
        {
            Logger.Info("CaptchaSolver: Submitting reCAPTCHA v2 to 2Captcha...");

            var content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("key", ApiKey),
                new System.Collections.Generic.KeyValuePair<string, string>("method", "userrecaptcha"),
                new System.Collections.Generic.KeyValuePair<string, string>("googlekey", siteKey),
                new System.Collections.Generic.KeyValuePair<string, string>("pageurl", pageUrl),
                new System.Collections.Generic.KeyValuePair<string, string>("json", "0")
            });

            string taskId = await SubmitAsync(content, ct);
            if (string.IsNullOrEmpty(taskId)) return null;

            return await PollForResultAsync(taskId, ct);
        }

        // ── Internal ──────────────────────────────────────────────────

        private async Task<string> SubmitAsync(FormUrlEncodedContent content, CancellationToken ct)
        {
            try
            {
                var response = await _http.PostAsync(SubmitUrl, content, ct);
                string body = await response.Content.ReadAsStringAsync(ct);

                // 2Captcha returns "OK|12345678" or "ERROR_..."
                if (body.StartsWith("OK|"))
                {
                    string taskId = body.Split('|')[1];
                    Logger.Info($"CaptchaSolver: Task submitted, ID={taskId}");
                    return taskId;
                }

                Logger.Error($"CaptchaSolver: Submit failed: {body}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CaptchaSolver.SubmitAsync");
                return string.Empty;
            }
        }

        private async Task<string?> PollForResultAsync(string taskId, CancellationToken ct)
        {
            string url = $"{ResultUrl}?key={ApiKey}&action=get&id={taskId}";

            for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                await Task.Delay(PollIntervalMs, ct);

                try
                {
                    string body = await _http.GetStringAsync(url, ct);

                    if (body.StartsWith("OK|"))
                    {
                        string answer = body.Split('|')[1];
                        Logger.Info($"CaptchaSolver: Solved in ~{attempt * PollIntervalMs / 1000}s → '{answer}'");
                        return answer;
                    }

                    if (body == "CAPCHA_NOT_READY")
                    {
                        Logger.Debug($"CaptchaSolver: Not ready yet (attempt {attempt + 1})");
                        continue;
                    }

                    Logger.Error($"CaptchaSolver: Poll error: {body}");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "CaptchaSolver.PollForResultAsync");
                    return null;
                }
            }

            Logger.Error("CaptchaSolver: Timed out waiting for captcha answer.");
            return null;
        }
    }
}
