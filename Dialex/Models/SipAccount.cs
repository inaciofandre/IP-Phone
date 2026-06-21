using System.Text.Json.Serialization;

namespace IP_Phone.Models
{
    /// <summary>
    /// Represents a complete SIP account configuration including server, proxy, domain, port,
    /// extension, display name, and authentication credentials.
    /// </summary>
    public class SipAccount
    {
        /// <summary>SIP server / registrar address (hostname or IP).</summary>
        public string Server { get; set; } = "";

        /// <summary>Optional SIP outbound proxy address.</summary>
        public string Proxy { get; set; } = "";

        /// <summary>SIP domain (often same as the server).</summary>
        public string Domain { get; set; } = "";

        /// <summary>SIP server port (default 5060).</summary>
        public int Port { get; set; } = 5060;

        /// <summary>SIP extension / subscriber number.</summary>
        public string Extension { get; set; } = "";

        /// <summary>Display name sent in SIP messages and shown to the remote party.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>SIP authentication username.</summary>
        public string Username { get; set; } = "";

        /// <summary>SIP authentication password.</summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Returns a human-readable label combining DisplayName, Extension, and Server.
        /// Falls back to "Extension@Server" if DisplayName is empty.
        /// </summary>
        [JsonIgnore]
        public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
            ? $"{Extension}@{Server}"
            : $"{DisplayName} <{Extension}@{Server}>";
    }
}
