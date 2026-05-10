using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Image captcha via Anti-Captcha.com JSON API (ImageToTextTask).
    /// </summary>
    public sealed class AntiCaptchaSolver : ICaptchaSolver
    {
        private const string CreateUrl = "https://api.anti-captcha.com/createTask";
        private const string ResultUrl = "https://api.anti-captcha.com/getTaskResult";
        private const int PollMs = 2_000;
        private const int MaxPolls = 35;

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
        public string ClientKey { get; }

        public AntiCaptchaSolver(string clientKey)
        {
            ClientKey = clientKey ?? string.Empty;
        }

        public async Task<string?> SolveImageCaptchaAsync(string base64Image, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ClientKey))
            {
                Logger.Error("AntiCaptchaSolver: Client key is empty.");
                return null;
            }

            try
            {
                var createPayload = new
                {
                    clientKey = ClientKey,
                    task = new
                    {
                        type = "ImageToTextTask",
                        body = base64Image
                    }
                };

                string json = JsonSerializer.Serialize(createPayload);
                using var resp = await _http.PostAsync(CreateUrl,
                    new StringContent(json, Encoding.UTF8, "application/json"), ct);
                string body = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                int errId = doc.RootElement.TryGetProperty("errorId", out var e) ? e.GetInt32() : -1;
                if (errId != 0)
                {
                    string? err = doc.RootElement.TryGetProperty("errorDescription", out var d)
                        ? d.GetString()
                        : body;
                    Logger.Error($"AntiCaptchaSolver: createTask failed: {err}");
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("taskId", out var taskIdEl))
                {
                    Logger.Error("AntiCaptchaSolver: Missing taskId in response.");
                    return null;
                }

                long taskId = taskIdEl.ValueKind == JsonValueKind.Number
                    ? taskIdEl.GetInt64()
                    : long.TryParse(taskIdEl.GetString(), out var parsed) ? parsed : 0;
                if (taskId == 0)
                {
                    Logger.Error("AntiCaptchaSolver: Invalid taskId.");
                    return null;
                }

                string pollJson = JsonSerializer.Serialize(new { clientKey = ClientKey, taskId });
                var pollStart = DateTime.UtcNow;

                for (int i = 0; i < MaxPolls; i++)
                {
                    await Task.Delay(PollMs, ct);
                    using var pollResp = await _http.PostAsync(ResultUrl,
                        new StringContent(pollJson, Encoding.UTF8, "application/json"), ct);
                    string pollBody = await pollResp.Content.ReadAsStringAsync(ct);
                    using var pdoc = JsonDocument.Parse(pollBody);
                    int pErr = pdoc.RootElement.TryGetProperty("errorId", out var pe) ? pe.GetInt32() : -1;
                    if (pErr != 0)
                    {
                        Logger.Error($"AntiCaptchaSolver: getTaskResult error: {pollBody}");
                        return null;
                    }

                    string status = pdoc.RootElement.GetProperty("status").GetString() ?? "";
                    if (status == "ready")
                    {
                        if (pdoc.RootElement.TryGetProperty("solution", out var sol) &&
                            sol.TryGetProperty("text", out var textEl))
                        {
                            string text = textEl.GetString() ?? "";
                            double secs = (DateTime.UtcNow - pollStart).TotalSeconds;
                            Logger.Info($"AntiCaptchaSolver: Solved in ~{secs:F1}s → '{text}'");
                            return text;
                        }
                    }
                }

                Logger.Error("AntiCaptchaSolver: Timed out waiting for solution.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AntiCaptchaSolver.SolveImageCaptchaAsync");
                return null;
            }
        }

        public Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl,
            CancellationToken ct = default)
        {
            Logger.Warning("AntiCaptchaSolver: Recaptcha v2 not wired — use 2Captcha provider or extend AntiCaptchaSolver.");
            return Task.FromResult<string?>(null);
        }
    }
}
