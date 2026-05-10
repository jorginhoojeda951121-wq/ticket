using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Manages IRCTC accounts: add, remove, persist and retrieve with encrypted passwords.
    /// All accounts are stored in accounts.json in the app data folder.
    /// </summary>
    public class AccountManager
    {
        private readonly string _storePath;
        private List<Account> _accounts = new();

        public IReadOnlyList<Account> Accounts => _accounts.AsReadOnly();

        public AccountManager()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IRCTCTatkalBot");
            Directory.CreateDirectory(appData);
            _storePath = Path.Combine(appData, "accounts.json");
            Load();
        }

        // ── CRUD ──────────────────────────────────────────────────────

        /// <summary>Adds a new account, encrypting the password before storage.</summary>
        public Account Add(string displayName, string username, string plainPassword,
                           string phone = "", string proxy = "",
                           string proxyUser = "", string proxyPass = "")
        {
            var account = new Account
            {
                DisplayName = displayName,
                Username = username,
                EncryptedPassword = EncryptionHelper.Encrypt(plainPassword),
                PhoneNumber = phone,
                ProxyAddress = proxy,
                ProxyUsername = proxyUser,
                ProxyPassword = EncryptionHelper.Encrypt(proxyPass)
            };

            _accounts.Add(account);
            Save();
            Logger.Info($"AccountManager: Added account '{username}'");
            return account;
        }

        public void Update(Account account) { Save(); }

        public bool Remove(Guid id)
        {
            int removed = _accounts.RemoveAll(a => a.Id == id);
            if (removed > 0) { Save(); Logger.Info($"AccountManager: Removed account {id}"); }
            return removed > 0;
        }

        public Account? GetById(Guid id) =>
            _accounts.FirstOrDefault(a => a.Id == id);

        public List<Account> GetActive() =>
            _accounts.Where(a => a.IsActive).ToList();

        /// <summary>Returns the decrypted plain-text password for an account.</summary>
        public string GetPassword(Account account) =>
            EncryptionHelper.Decrypt(account.EncryptedPassword);

        public string GetProxyPassword(Account account) =>
            EncryptionHelper.Decrypt(account.ProxyPassword);

        // ── Persistence ───────────────────────────────────────────────

        private void Save()
        {
            string json = JsonSerializer.Serialize(_accounts,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }

        private void Load()
        {
            if (!File.Exists(_storePath)) return;
            try
            {
                string json = File.ReadAllText(_storePath);
                _accounts = JsonSerializer.Deserialize<List<Account>>(json) ?? new();
                Logger.Info($"AccountManager: Loaded {_accounts.Count} account(s)");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AccountManager.Load");
                _accounts = new();
            }
        }
    }
}
