using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IRCTCTatkalBot;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Orchestrates concurrent Tatkal booking across multiple accounts.
    ///
    /// Workflow:
    ///   1. For each config, create a SessionManager + BookingEngine pair
    ///   2. Wait for the pre-login time (T-90s), log all sessions in
    ///   3. At T=0 (Tatkal window open), fire booking tasks (search begins without a second Login when pre-login succeeded)
    ///   4. Collect and report results
    /// </summary>
    public class BookingOrchestrator
    {
        private readonly AccountManager _accountManager;
        private readonly ICaptchaSolver _captchaSolver;
        private CancellationTokenSource _cts = new();

        public event Action<BookingResult>? OnBookingResult;
        public event Action<string>? OnStatusMessage;

        public BookingOrchestrator(AccountManager accountManager, ICaptchaSolver captchaSolver)
        {
            _accountManager = accountManager;
            _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));
        }

        // ── Main Entry ────────────────────────────────────────────────

        /// <summary>
        /// Runs bookings for all supplied configs concurrently.
        /// If <paramref name="useScheduler"/> is true, waits for the Tatkal window first.
        /// </summary>
        public async Task RunAllAsync(List<BookingConfig> configs,
                                      bool useScheduler = true,
                                      CancellationToken externalCt = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct = _cts.Token;

            if (configs.Count == 0)
            {
                Emit("No booking configs supplied.");
                return;
            }

            // Use the first config's class to determine the window time
            var windowTime = Scheduler.GetWindowForClass(configs[0].TrainClass);
            var scheduler = new Scheduler(windowTime);

            if (useScheduler)
            {
                DateTime windowOpen = scheduler.GetNextWindowDateTime();
                DateTime preLoginTime = scheduler.GetPreLoginTime(windowOpen);

                Emit($"Tatkal window opens at {windowOpen:HH:mm:ss} IST");
                Emit($"Pre-login starts at {preLoginTime:HH:mm:ss} IST ({(preLoginTime - DateTime.Now).TotalMinutes:F0} min)");

                // ── Phase 1: Wait until pre-login time ────────────────
                var sessions =
                    new List<(SessionManager Session, BookingConfig Config, bool PreLoginSucceeded)>();

                // Start countdown
                scheduler.OnCountdown += ts => Emit($"T-{ts:mm\\:ss}");

                // Wait to T-90s
                await WaitUntilAsync(preLoginTime, ct);
                Emit("▶ Pre-login phase started — opening browser sessions...");

                // ── Phase 2: Start drivers & log in (staggered; one driver failure does not abort others) ──
                var loginTasks = configs
                    .Select((cfg, idx) => StartLoginSessionAsync(cfg, idx, ct))
                    .ToList();

                var loginResults = await Task.WhenAll(loginTasks);
                sessions = loginResults
                    .Where(r => r.Session != null)
                    .Select(r => (r.Session!, r.Config, r.PreLoginSucceeded))
                    .ToList();

                int okCount = loginResults.Count(r => r.PreLoginSucceeded);
                Emit($"{okCount}/{configs.Count} pre-logins succeeded; {sessions.Count} browser session(s) open.");

                // ── Phase 3: Wait for window open ─────────────────────
                await scheduler.WaitForWindowAsync(windowOpen, ct);
                Emit("🚀 FIRING ALL BOOKING TASKS NOW!");

                // ── Phase 4: booking (first attempt skips login only if pre-login succeeded) ─────
                var bookingTasks = sessions.Select(s =>
                    RunSingleAsync(s.Session, s.Config, ct, s.PreLoginSucceeded));
                var results = await Task.WhenAll(bookingTasks);

                // Dispose all sessions
                foreach (var (session, _, _) in sessions)
                    try { session.Dispose(); } catch { }

                Emit($"All bookings complete. " +
                     $"Success: {results.Count(r => r.Status == BookingStatus.Completed)} / " +
                     $"Failed: {results.Count(r => r.Status == BookingStatus.Failed)}");
            }
            else
            {
                // Immediate mode — skip scheduler, run right now
                Emit("Immediate mode: Starting bookings now...");

                var immediateTasks = configs
                    .Select((cfg, idx) => RunImmediateAccountAsync(cfg, idx, ct))
                    .ToList();

                await Task.WhenAll(immediateTasks);
            }
        }

        public void Cancel() => _cts.Cancel();

        // ── Internal ──────────────────────────────────────────────────

        private async Task<BookingResult> RunSingleAsync(
            SessionManager session,
            BookingConfig config,
            CancellationToken ct,
            bool preLoginSucceededScheduled = false)
        {
            var engine = new BookingEngine(session, config);
            engine.OnStatusChanged += r => OnBookingResult?.Invoke(r);

            for (int attempt = 1; attempt <= config.MaxRetries; attempt++)
            {
                Logger.Info($"Orchestrator: Attempt {attempt}/{config.MaxRetries} for account {config.AccountId}");
                var result = await engine.RunAsync(ct);

                if (result.Status == BookingStatus.Completed)
                {
                    Logger.Info($"Orchestrator: ✓ Booking completed successfully on attempt {attempt}");
                    return result;
                }

                if (result.Status == BookingStatus.Cancelled)
                {
                    Logger.Info($"Orchestrator: Booking cancelled by user on attempt {attempt}");
                    return result;
                }

                // Log detailed failure reason for debugging
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    Logger.Warning($"Orchestrator: Attempt {attempt} failed with error: {result.ErrorMessage}");
                }

                if (attempt < config.MaxRetries)
                {
                    Logger.Warning($"Orchestrator: Retrying in {config.RetryDelayMs}ms...");
                    await Task.Delay(config.RetryDelayMs, ct);
                }
            }

            return new BookingResult
            {
                AccountId = config.AccountId,
                Status = BookingStatus.Failed,
                ErrorMessage = "All retry attempts exhausted.",
                CompletedAt = DateTime.Now
            };
        }

        private static async Task WaitUntilAsync(DateTime target, CancellationToken ct)
        {
            while (DateTime.Now < target)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan wait = target - DateTime.Now;
                int ms = wait.TotalSeconds > 10 ? 5000 : 500;
                await Task.Delay(ms, ct);
            }
        }

        private void Emit(string msg)
        {
            Logger.Info($"Orchestrator: {msg}");
            OnStatusMessage?.Invoke(msg);
        }

        private async Task<(SessionManager? Session, BookingConfig Config, bool PreLoginSucceeded)>
            StartLoginSessionAsync(BookingConfig cfg, int staggerIndex, CancellationToken ct)
        {
            int staggerMs = Math.Max(0, AppSettings.Instance.StaggerMsBetweenDriverStarts);
            if (staggerIndex > 0 && staggerMs > 0)
                await Task.Delay(staggerIndex * staggerMs, ct);

            var account = _accountManager.GetById(cfg.AccountId);
            if (account == null)
            {
                Logger.Warning($"Orchestrator: Account {cfg.AccountId} not found, skipping.");
                return (null, cfg, false);
            }

            SessionManager? session = null;
            try
            {
                session = new SessionManager(account, _accountManager, _captchaSolver);
                session.StartDriver();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Orchestrator: ChromeDriver failed for {account.Username}");
                try { session?.Dispose(); } catch { /* ignore */ }
                NotifyDriverFailure(cfg.AccountId, ex);
                return (null, cfg, false);
            }

            bool ok = await session!.LoginAsync(ct);
            if (!ok) Logger.Warning($"Orchestrator: Pre-login failed for {account.Username}");
            return (session, cfg, ok);
        }

        private async Task<BookingResult> RunImmediateAccountAsync(BookingConfig cfg, int staggerIndex, CancellationToken ct)
        {
            int staggerMs = Math.Max(0, AppSettings.Instance.StaggerMsBetweenDriverStarts);
            if (staggerIndex > 0 && staggerMs > 0)
                await Task.Delay(staggerIndex * staggerMs, ct);

            var account = _accountManager.GetById(cfg.AccountId);
            if (account == null)
            {
                Logger.Warning($"Orchestrator: Account {cfg.AccountId} not found.");
                return new BookingResult
                {
                    AccountId = cfg.AccountId,
                    Status = BookingStatus.Failed,
                    ErrorMessage = "Account not found",
                    CompletedAt = DateTime.Now
                };
            }

            var session = new SessionManager(account, _accountManager, _captchaSolver);
            try
            {
                session.StartDriver();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Orchestrator: ChromeDriver failed for {account.Username}");
                try { session.Dispose(); } catch { /* ignore */ }
                var fail = new BookingResult
                {
                    AccountId = cfg.AccountId,
                    Status = BookingStatus.Failed,
                    ErrorMessage = "Driver: " + DriverDiagnostics.FormatShort(ex),
                    CompletedAt = DateTime.Now
                };
                OnBookingResult?.Invoke(fail);
                return fail;
            }

            try
            {
                return await RunSingleAsync(session, cfg, ct);
            }
            finally
            {
                try { session.Dispose(); } catch { /* ignore */ }
            }
        }

        private void NotifyDriverFailure(Guid accountId, Exception ex)
        {
            OnBookingResult?.Invoke(new BookingResult
            {
                AccountId = accountId,
                Status = BookingStatus.Failed,
                ErrorMessage = "Driver: " + DriverDiagnostics.FormatShort(ex),
                CompletedAt = DateTime.Now
            });
        }
    }
}
