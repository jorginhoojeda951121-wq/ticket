using System;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// No external API: user types the captcha in the browser; automation waits for the input field.
    /// </summary>
    public sealed class ManualCaptchaSolver : ICaptchaSolver
    {
        public static readonly ManualCaptchaSolver Instance = new ManualCaptchaSolver();

        private ManualCaptchaSolver() { }

        public Task<string?> SolveImageCaptchaAsync(string base64Image, CancellationToken ct = default)
        {
            Logger.Warning("ManualCaptchaSolver: SolveImageCaptchaAsync was called — use WaitForUserEntryAsync in login/booking flow.");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl, CancellationToken ct = default)
        {
            Logger.Warning("ManualCaptchaSolver: reCAPTCHA v2 is not supported in manual mode — complete it in the browser if shown.");
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Polls captcha input resolved by <paramref name="resolveCaptchaInput"/> each cycle (survives Angular re-renders / stale elements).
        /// </summary>
        public static async Task<string?> WaitForUserEntryAsync(
            Func<IWebElement?> resolveCaptchaInput,
            CancellationToken ct,
            TimeSpan? maxWait = null)
        {
            maxWait ??= TimeSpan.FromMinutes(15);
            DateTime deadline = DateTime.UtcNow + maxWait.Value;
            string? pending = null;
            DateTime pendingSince = DateTime.MinValue;

            Logger.Info("Manual captcha: type the characters in the captcha box in Chrome; waiting for your input…");

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                string v;
                try
                {
                    var captchaInput = resolveCaptchaInput();
                    if (captchaInput == null)
                    {
                        v = "";
                    }
                    else
                    {
                        v = captchaInput.GetDomProperty("value")?.Trim()
                            ?? captchaInput.GetAttribute("value")?.Trim()
                            ?? "";
                    }
                }
                catch (StaleElementReferenceException)
                {
                    await Task.Delay(400, ct);
                    continue;
                }

                if (v.Length >= 4)
                {
                    if (v != pending)
                    {
                        pending = v;
                        pendingSince = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - pendingSince).TotalMilliseconds >= 600)
                        return v;
                }
                else
                    pending = null;

                try
                {
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            Logger.Warning("Manual captcha: timed out waiting for input.");
            return null;
        }
    }
}
