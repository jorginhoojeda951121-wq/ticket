using System;
using System.Threading;
using System.Threading.Tasks;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Precision scheduler that waits until the Tatkal booking window opens
    /// and then fires all booking sessions simultaneously.
    ///
    /// Tatkal windows (IST):
    ///   AC classes  (1A, 2A, 3A, CC, EC) → 10:00:00 AM
    ///   Non-AC      (SL, 2S)             → 11:00:00 AM
    ///
    /// The scheduler pre-logs all sessions in before the window opens
    /// so the only thing left at T+0 is the train search.
    /// </summary>
    public class Scheduler
    {
        // How many seconds before opening to start pre-login
        private const int PreLoginLeadSeconds = 90;

        private readonly TimeSpan _targetTime;

        public event Action? OnWindowOpen;
        public event Action<TimeSpan>? OnCountdown;

        public Scheduler(TimeSpan targetOpenTime)
        {
            _targetTime = targetOpenTime;
        }

        /// <summary>
        /// Returns the target DateTime (today or tomorrow) for the next Tatkal window.
        /// </summary>
        public DateTime GetNextWindowDateTime()
        {
            DateTime now = TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            DateTime target = now.Date + _targetTime;
            if (now >= target) target = target.AddDays(1);
            return target;
        }

        /// <summary>
        /// Blocks asynchronously until <paramref name="fireAt"/> then raises OnWindowOpen.
        /// Raises OnCountdown every second so the UI can show a live countdown.
        /// </summary>
        public async Task WaitForWindowAsync(DateTime fireAt, CancellationToken ct = default)
        {
            Logger.Info($"Scheduler: Window opens at {fireAt:dd-MMM-yyyy HH:mm:ss} IST");

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                DateTime nowIst = TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
                TimeSpan remaining = fireAt - nowIst;

                if (remaining.TotalMilliseconds <= 0) break;

                OnCountdown?.Invoke(remaining);
                Logger.Debug($"Scheduler: T-{remaining:mm\\:ss\\.fff}");

                // Sleep in 100ms ticks for high precision near the window
                int sleepMs = remaining.TotalSeconds > 5 ? 1000 : 100;
                await Task.Delay(sleepMs, ct);
            }

            Logger.Info("Scheduler: *** TATKAL WINDOW OPEN — FIRING NOW ***");
            OnWindowOpen?.Invoke();
        }

        /// <summary>
        /// Returns when pre-login should start (LeadSeconds before the window).
        /// </summary>
        public DateTime GetPreLoginTime(DateTime windowOpen) =>
            windowOpen.AddSeconds(-PreLoginLeadSeconds);

        /// <summary>
        /// Detects the correct Tatkal opening time based on train class.
        /// </summary>
        public static TimeSpan GetWindowForClass(string trainClass)
        {
            return trainClass.ToUpperInvariant() switch
            {
                "SL" or "2S" => new TimeSpan(11, 0, 0),
                _            => new TimeSpan(10, 0, 0) // AC classes
            };
        }
    }
}
