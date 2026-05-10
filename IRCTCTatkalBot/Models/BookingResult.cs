using System;

namespace IRCTCTatkalBot.Models
{
    public enum BookingStatus
    {
        Pending,
        LoggingIn,
        Searching,
        SelectingTrain,
        FillingPassengers,
        ProcessingPayment,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Result of a single booking attempt.
    /// </summary>
    public class BookingResult
    {
        public Guid BookingId { get; set; } = Guid.NewGuid();
        public Guid AccountId { get; set; }
        public BookingStatus Status { get; set; } = BookingStatus.Pending;
        public string? PnrNumber { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public double ElapsedSeconds => CompletedAt.HasValue
            ? (CompletedAt.Value - StartedAt).TotalSeconds : 0;
        public string? ScreenshotPath { get; set; }

        /// <summary>Wall-clock seconds spent in login (includes captcha).</summary>
        public double LoginPhaseSeconds { get; set; }

        public double SearchPhaseSeconds { get; set; }
        public double SelectTrainPhaseSeconds { get; set; }
        public double FillPassengersPhaseSeconds { get; set; }
        public double PaymentPhaseSeconds { get; set; }

        /// <summary>Last captcha solve duration observed in this attempt (seconds), if any.</summary>
        public double? LastCaptchaSolveSeconds { get; set; }

        /// <summary>Human-readable timing line for results grid / logs.</summary>
        public string PhaseTimingSummary =>
            $"L:{LoginPhaseSeconds:F1}s S:{SearchPhaseSeconds:F1}s T:{SelectTrainPhaseSeconds:F1}s " +
            $"P:{FillPassengersPhaseSeconds:F1}s Pay:{PaymentPhaseSeconds:F1}s" +
            (LastCaptchaSolveSeconds is { } cap ? $" Cap:{cap:F1}s" : "");
    }
}
