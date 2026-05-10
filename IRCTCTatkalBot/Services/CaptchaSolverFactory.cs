using System;

namespace IRCTCTatkalBot.Services
{
    public static class CaptchaSolverFactory
    {
        public static ICaptchaSolver CreateFromSettings()
        {
            var s = AppSettings.Instance;
            var provider = (s.CaptchaProvider ?? "2captcha").Trim().ToLowerInvariant();

            return provider switch
            {
                "manual" or "none" or "off" =>
                    ManualCaptchaSolver.Instance,
                "anticaptcha" or "anti-captcha" =>
                    new AntiCaptchaSolver(string.IsNullOrWhiteSpace(s.AntiCaptchaApiKey)
                        ? s.TwoCaptchaApiKey
                        : s.AntiCaptchaApiKey),
                _ => new CaptchaSolver(s.TwoCaptchaApiKey)
            };
        }
    }
}
