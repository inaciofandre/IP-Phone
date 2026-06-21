using IP_Phone.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IP_Phone.Services
{
    /// <summary>
    /// Manages persisted SIP account storage using DPAPI-encrypted JSON (<c>accounts.enc</c>).
    /// Holds a <see cref="List{SipAccount}"/> and a zero-based <c>_defaultIndex</c> that selects the active account.
    /// Includes a one-shot migration path from the legacy single-account <c>credentials.enc</c> format.
    /// Uses <see cref="ProtectedData"/> (Windows Data Protection API) so credentials are automatically
    /// encrypted/decrypted per Windows user account without a manual key.
    /// </summary>
    public class AccountManager
    {
        /// <summary>
        /// Full path to the encrypted account store file (default: <c>accounts.enc</c> in the base directory).
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// In-memory list of all configured SIP accounts. Loaded from disk by <see cref="Load"/>,
        /// written back by <see cref="Save"/>. <c>null</c> until the first <see cref="Load"/> call.
        /// </summary>
        private List<SipAccount> _accounts;

        /// <summary>
        /// Index into <c>_accounts</c> of the default / currently-selected account.
        /// Clamped to a valid range in <see cref="Default"/> and <see cref="Remove"/>.
        /// </summary>
        private int _defaultIndex;

        /// <summary>
        /// Initializes a new instance with an optional custom file path.
        /// </summary>
        /// <param name="filePath">
        /// Path to the encrypted store file. If <c>null</c>, defaults to
        /// <c>accounts.enc</c> in the application's base directory.
        /// </param>
        public AccountManager(string filePath = null)
        {
            _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.enc");
        }

        /// <summary>Returns <c>true</c> when the encrypted store file exists on disk.</summary>
        public bool HasAccounts => File.Exists(_filePath);

        /// <summary>Returns a read-only snapshot of all stored SIP accounts (empty list if none loaded).</summary>
        public IReadOnlyList<SipAccount> Accounts => (_accounts ?? new List<SipAccount>()).AsReadOnly();

        /// <summary>Gets the zero-based index of the default account.</summary>
        public int DefaultIndex => _defaultIndex;

        /// <summary>
        /// Gets the default <see cref="SipAccount"/>, or <c>null</c> if no accounts exist.
        /// The index is safely clamped to the last element if it exceeds the list boundary.
        /// </summary>
        public SipAccount Default => _accounts != null && _accounts.Count > 0
            ? _accounts[Math.Min(_defaultIndex, _accounts.Count - 1)]
            : null;

        /// <summary>
        /// Reads and decrypts the store file, then deserializes the JSON into <c>_accounts</c>
        /// and <c>_defaultIndex</c>. If the file does not exist, initialises empty state.
        /// </summary>
        public void Load()
        {
            if (!HasAccounts)
            {
                _accounts = new List<SipAccount>();
                _defaultIndex = 0;
                return;
            }

            var encrypted = File.ReadAllBytes(_filePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            var data = JsonSerializer.Deserialize<AccountStore>(json);
            _accounts = data?.Accounts ?? new List<SipAccount>();
            _defaultIndex = data?.DefaultIndex ?? 0;
        }

        /// <summary>
        /// Serialises the current account list and default index to JSON, encrypts the payload
        /// with DPAPI (<see cref="DataProtectionScope.CurrentUser"/>), and writes it to disk.
        /// </summary>
        public void Save()
        {
            var data = new AccountStore { Accounts = _accounts ?? new List<SipAccount>(), DefaultIndex = _defaultIndex };
            var json = JsonSerializer.Serialize(data);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, bytes);
        }

        /// <summary>
        /// Loads the current store, appends a new account, and persists the result.
        /// </summary>
        /// <param name="account">The SIP account to add.</param>
        public void Add(SipAccount account)
        {
            Load();
            _accounts.Add(account);
            Save();
        }

        /// <summary>
        /// Loads the current store, removes the account at the given index, and persists.
        /// If the removed account was the default, the default index is adjusted to stay
        /// within bounds.
        /// </summary>
        /// <param name="index">Zero-based index of the account to remove. Silently ignored if out of range.</param>
        public void Remove(int index)
        {
            Load();
            if (index < 0 || index >= _accounts.Count) return;
            _accounts.RemoveAt(index);
            if (_defaultIndex >= _accounts.Count)
                _defaultIndex = Math.Max(0, _accounts.Count - 1);
            Save();
        }

        /// <summary>
        /// Sets the default account index and persists the change.
        /// </summary>
        /// <param name="index">Zero-based index to mark as default. Ignored if out of range.</param>
        public void SetDefault(int index)
        {
            Load();
            if (index >= 0 && index < _accounts.Count)
            {
                _defaultIndex = index;
                Save();
            }
        }

        /// <summary>
        /// Deletes the encrypted store file from disk and resets the in-memory account list to <c>null</c>.
        /// </summary>
        public void Clear()
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
            _accounts = null;
        }

        /// <summary>
        /// Attempts a one-time migration from the legacy single-account <c>credentials.enc</c> format.
        /// Reads extension, username, password from <c>credentials.enc</c> and optional server/proxy/domain
        /// from <c>settings.json</c>, creates a single <see cref="SipAccount"/>, saves it to the new format,
        /// then deletes the old file.
        /// </summary>
        /// <param name="oldCredPath">
        /// Explicit path to the legacy <c>credentials.enc</c> file. If <c>null</c>, defaults to
        /// <c>credentials.enc</c> in the base directory.
        /// </param>
        /// <returns><c>true</c> if migration succeeded; <c>false</c> if the old file was missing or any error occurred.</returns>
        public static bool TryMigrateOldCredentials(string oldCredPath)
        {
            var oldFile = oldCredPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credentials.enc");
            if (!File.Exists(oldFile)) return false;

            try
            {
                var encrypted = File.ReadAllBytes(oldFile);
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(decrypted);
                string ext = "", user = "", pass = "";
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    ext = root.GetProperty("extension").GetString() ?? "";
                    user = root.GetProperty("username").GetString() ?? "";
                    pass = root.GetProperty("password").GetString() ?? "";
                }

                var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                string oldServer = "", oldProxy = "", oldDomain = "", oldDisplay = "";
                int oldPort = 5060;
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var sj = File.ReadAllText(settingsPath);
                        using (var sdoc = JsonDocument.Parse(sj))
                        {
                            var sroot = sdoc.RootElement;
                            if (sroot.TryGetProperty("Server", out var sv)) oldServer = sv.GetString() ?? "";
                            if (sroot.TryGetProperty("Proxy", out var px)) oldProxy = px.GetString() ?? "";
                            if (sroot.TryGetProperty("Domain", out var dm)) oldDomain = dm.GetString() ?? "";
                            if (sroot.TryGetProperty("DisplayName", out var dn)) oldDisplay = dn.GetString() ?? "";
                            if (sroot.TryGetProperty("Port", out var pt)) oldPort = pt.GetInt32();
                        }
                    }
                    catch { }
                }

                var mgr = new AccountManager();
                mgr.Load();
                var server = !string.IsNullOrWhiteSpace(oldServer) ? oldServer : ext;
                mgr._accounts.Add(new SipAccount
                {
                    Server = server,
                    Proxy = !string.IsNullOrWhiteSpace(oldProxy) ? oldProxy : server,
                    Domain = !string.IsNullOrWhiteSpace(oldDomain) ? oldDomain : server,
                    Port = oldPort,
                    Extension = ext,
                    DisplayName = oldDisplay,
                    Username = user,
                    Password = pass
                });
                mgr._defaultIndex = 0;
                mgr.Save();

                File.Delete(oldFile);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Internal DTO used to serialise/deserialise the encrypted store file.
        /// Contains the flat list of SIP accounts and the selected default index.
        /// </summary>
        private class AccountStore
        {
            /// <summary>The persisted list of SIP accounts.</summary>
            public List<SipAccount> Accounts { get; set; } = new List<SipAccount>();

            /// <summary>The zero-based index of the default account.</summary>
            public int DefaultIndex { get; set; } = 0;
        }
    }
}
