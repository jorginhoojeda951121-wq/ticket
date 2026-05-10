using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IRCTCTatkalBot.Helpers
{
    /// <summary>
    /// AES-256 encryption helper for storing credentials securely.
    /// The encryption key is derived from the machine's unique identifier
    /// so credentials are tied to the machine they were created on.
    /// </summary>
    public static class EncryptionHelper
    {
        // Derive a 256-bit key from the machine's unique ID
        private static readonly byte[] Key = DeriveKey();
        private static readonly byte[] Salt = Encoding.UTF8.GetBytes("IRCTC_TATKAL_SALT_v1");

        private static byte[] DeriveKey()
        {
            // Use machine-specific data so encrypted files can't simply be copied
            string machineId = Environment.MachineName
                + Environment.UserName
                + "IRCTC_BOT_SECRET_2024";

            using var deriveBytes = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(machineId),
                Encoding.UTF8.GetBytes("IRCTC_TATKAL_SALT_v1"),
                100_000,
                HashAlgorithmName.SHA256);

            return deriveBytes.GetBytes(32); // 256-bit key
        }

        /// <summary>Encrypts plaintext and returns a Base64-encoded ciphertext.</summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            using var aes = Aes.Create();
            aes.Key = Key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();

            // Prepend IV to the ciphertext
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
                sw.Write(plainText);

            return Convert.ToBase64String(ms.ToArray());
        }

        /// <summary>Decrypts a Base64-encoded ciphertext and returns plaintext.</summary>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            byte[] fullCipher = Convert.FromBase64String(cipherText);

            using var aes = Aes.Create();
            aes.Key = Key;

            // Extract IV from first 16 bytes
            byte[] iv = new byte[16];
            Array.Copy(fullCipher, 0, iv, 0, 16);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(fullCipher, 16, fullCipher.Length - 16);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            return sr.ReadToEnd();
        }
    }
}
