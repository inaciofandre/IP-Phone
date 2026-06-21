using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IP_Phone.Services
{
    /// <summary>
    /// Securely stores and retrieves SIP credentials (extension, username, password)
    /// using Windows DPAPI encryption (ProtectedData). The encrypted file is bound
    /// to the current Windows user account and can only be decrypted by the same user
    /// on the same machine. Credentials are never stored in plaintext settings.json.
    /// </summary>
    public class CredentialManager
    {
        private readonly string _filePath;

        /// <summary>
        /// Initialises the manager with an optional custom file path.
        /// Defaults to "credentials.enc" in the application base directory.
        /// </summary>
        public CredentialManager(string filePath = null)
        {
            _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credentials.enc");
        }

        /// <summary>Returns true if an encrypted credentials file exists on disk.</summary>
        public bool HasCredentials => File.Exists(_filePath);

        /// <summary>Encrypts and saves credentials to disk using DPAPI with CurrentUser scope.</summary>
        public void Save(string extension, string username, string password)
        {
            var data = new { extension, username, password };
            var json = JsonSerializer.Serialize(data);
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, encrypted);
        }

        /// <summary>Loads and decrypts credentials from disk. Throws if file is missing or corrupt.</summary>
        public (string extension, string username, string password) Load()
        {
            if (!HasCredentials)
                throw new FileNotFoundException("Credentials file not found.", _filePath);

            var encrypted = File.ReadAllBytes(_filePath);
            var decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                return (
                    root.GetProperty("extension").GetString(),
                    root.GetProperty("username").GetString(),
                    root.GetProperty("password").GetString()
                );
            }
        }

        /// <summary>Deletes the encrypted credentials file from disk.</summary>
        public void Clear()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }
}
