using System;

namespace IP_Phone.Models
{
    /// <summary>
    /// Represents a single call history entry with timestamps, direction, status, and duration.
    /// </summary>
    public class CallHistoryEntry
    {
        /// <summary>Unique identifier for this call history record (auto-generated GUID).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The remote party identifier (extension, URI, or phone number).</summary>
        public string RemoteParty { get; set; }

        /// <summary>Call direction: "inbound" or "outbound".</summary>
        public string Direction { get; set; }

        /// <summary>Final call status: "completed", "missed", "rejected", "failed", etc.</summary>
        public string Status { get; set; }

        /// <summary>UTC timestamp when the call started (initiated or answered).</summary>
        public DateTime StartTime { get; set; }

        /// <summary>UTC timestamp when the call ended, or null if still in progress.</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>Total call duration in seconds, or null if not yet completed.</summary>
        public double? DurationSeconds { get; set; }
    }
}
