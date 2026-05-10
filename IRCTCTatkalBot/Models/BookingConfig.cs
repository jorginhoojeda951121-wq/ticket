using System;
using System.Collections.Generic;

namespace IRCTCTatkalBot.Models
{
    /// <summary>
    /// Complete configuration for one Tatkal booking attempt.
    /// </summary>
    public class BookingConfig
    {
        // ── Train Details ──────────────────────────────────────────────
        public string FromStation { get; set; } = string.Empty;   // e.g. NDLS
        public string ToStation { get; set; } = string.Empty;     // e.g. MMCT
        public DateTime JourneyDate { get; set; } = DateTime.Today.AddDays(1);
        public string TrainNumber { get; set; } = string.Empty;   // e.g. 12951
        public string TrainClass { get; set; } = "3A";            // SL / 3A / 2A / 1A / CC
        public string Quota { get; set; } = "TATKAL";             // TATKAL / PREMIUM TATKAL / GN

        // ── Payment ────────────────────────────────────────────────────
        public string PaymentMethod { get; set; } = "UPI";        // UPI / CARD / NETBANKING
        public string UpiId { get; set; } = string.Empty;         // Used if PaymentMethod = UPI

        // ── Passengers ────────────────────────────────────────────────
        public List<Passenger> Passengers { get; set; } = new();

        // ── Account Assignment ─────────────────────────────────────────
        public Guid AccountId { get; set; }                       // Which account to use

        // ── Scheduler ─────────────────────────────────────────────────
        // Tatkal opens at 10:00 AM for AC classes, 11:00 AM for non-AC
        public TimeSpan TargetOpenTime { get; set; } = new TimeSpan(10, 0, 0);
        public bool AutoSchedule { get; set; } = true;

        // ── Retry ─────────────────────────────────────────────────────
        public int MaxRetries { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 500;
    }
}
