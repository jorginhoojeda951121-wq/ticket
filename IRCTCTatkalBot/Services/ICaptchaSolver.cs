using System.Threading;
using System.Threading.Tasks;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Pluggable captcha solving (2Captcha, Anti-Captcha, etc.).
    /// </summary>
    public interface ICaptchaSolver
    {
        Task<string?> SolveImageCaptchaAsync(string base64Image, CancellationToken ct = default);

        Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl,
            CancellationToken ct = default);
    }
}
