using System;

namespace IRCTCTatkalBot.Models
{
    /// <summary>
    /// Represents an IRCTC user account with encrypted credentials.
    /// </summary>
    public class Account
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string DisplayName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        // Stored as AES-encrypted Base64 string
        public string EncryptedPassword { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;  // For OTP
        public string ProxyAddress { get; set; } = string.Empty; // e.g. socks5://host:port
        public string ProxyUsername { get; set; } = string.Empty;
        public string ProxyPassword { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
        public DateTime LastUsed { get; set; } = DateTime.MinValue;
        public int SuccessfulBookings { get; set; } = 0;
    }
}
