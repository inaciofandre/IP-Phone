using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using System;
using System.IO;

namespace IP_Phone.Models
{
    /// <summary>
    /// Represents a single phone line, each with its own SIPUserAgent and RTP session.
    /// Supports multiple concurrent lines for call handling, hold, transfer, and recording.
    /// </summary>
    public class CallLine
    {
        /// <summary>Zero-based index identifying this line among all registered lines.</summary>
        public int Id { get; set; }

        /// <summary>SIP user agent handling registration and call signaling for this line.</summary>
        public SIPUserAgent UserAgent { get; set; }

        /// <summary>RTP media session for sending and receiving audio.</summary>
        public RTPSession RtpSession { get; set; }

        /// <summary>Server-side user agent for an incoming (pending) call that hasn't been answered yet.</summary>
        public SIPServerUserAgent PendingCallUAS { get; set; }

        /// <summary>The original SIP request for a pending incoming call.</summary>
        public SIPRequest PendingCallRequest { get; set; }

        /// <summary>Identifier linking this call to its entry in the call history log.</summary>
        public string CallHistoryId { get; set; }

        /// <summary>The original SIP dialogue saved when a transfer is initiated (attended or blind).</summary>
        public SIPDialogue TransferOriginalDialogue { get; set; }

        /// <summary>The RTP session associated with the transfer target's media.</summary>
        public RTPSession TransferMediaSession { get; set; }

        /// <summary>Whether an active call is currently established on this line.</summary>
        public bool IsCallActive => UserAgent != null && UserAgent.IsCallActive;

        /// <summary>Whether there is an incoming call waiting to be answered.</summary>
        public bool HasPendingCall => PendingCallUAS != null;

        /// <summary>Whether the local microphone audio is muted for this call.</summary>
        public bool IsMuted { get; set; }

        /// <summary>Whether the current call is being recorded to a file.</summary>
        public bool IsRecording { get; set; }

        /// <summary>File stream used to write recorded RTP audio data.</summary>
        public FileStream RecordingStream { get; set; }

        /// <summary>UTC timestamp when the current call was answered/started.</summary>
        public DateTime CallStartTime { get; set; }

        /// <summary>Returns elapsed call time as MM:SS, or empty string if no active call.</summary>
        public string CallDuration
        {
            get
            {
                if (!IsCallActive || CallStartTime == default) return "";
                var elapsed = DateTime.Now - CallStartTime;
                return $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
            }
        }

        /// <summary>Returns a human-readable status string for this line.</summary>
        public string StatusString
        {
            get
            {
                if (TransferOriginalDialogue != null) return "Transfer";
                if (HasPendingCall) return "Ringing";
                if (IsCallActive) return "Active";
                return "Idle";
            }
        }
    }
}
