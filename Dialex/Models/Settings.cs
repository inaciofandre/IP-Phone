namespace IP_Phone.Models
{
    /// <summary>
    /// Global application settings for the IP phone, including SIP ports, TLS, SRTP, and certificate configuration.
    /// </summary>
    public class Settings
    {
        /// <summary>UDP port used for SIP signaling (default 5060).</summary>
        public int Port { get; set; } = 5060;

        /// <summary>Number of concurrent call lines supported by the phone.</summary>
        public int LineCount { get; set; } = 2;

        /// <summary>Public IP address advertised in SIP messages for NAT traversal.</summary>
        public string PublicIP { get; set; } = "";

        /// <summary>Whether to use TLS for SIP signaling instead of UDP/TCP.</summary>
        public bool UseTls { get; set; } = false;

        /// <summary>Port used for TLS-secured SIP signaling (default 5061).</summary>
        public int TlsPort { get; set; } = 5061;

        /// <summary>Whether to validate the server's TLS certificate.</summary>
        public bool ValidateServerCert { get; set; } = false;

        /// <summary>SRTP encryption mode: "none", "optional", or "mandatory".</summary>
        public string SrtpMode { get; set; } = "none";

        /// <summary>File system path to the TLS client certificate (PFX or PEM).</summary>
        public string TlsCertPath { get; set; } = "";

        /// <summary>Password for the TLS certificate file, if applicable.</summary>
        public string TlsCertPassword { get; set; } = "";
    }
}
