using IP_Phone.Models;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace IP_Phone.Services
{
    /// <summary>
    /// Core SIP phone service handling registration, multi-line call control,
    /// NAT detection, DTMF, hold, transfer, recording, and PBX keep-alive.
    /// Uses SIPSorcery for SIP stack and RTPSession for media (without external media endpoints).
    /// </summary>
    public class SipService
    {
        private readonly Settings _settings;
        private readonly bool _verbose;
        private SipAccount _currentAccount;
        private SIPTransport _sipTransport;
        private SIPRegistrationUserAgent _regUserAgent;
        private bool _dndEnabled;
        private string _forwardingTarget;
        private bool _isOnline;
        private Timer _reconnectTimer;
        private readonly CallHistoryService _callHistoryService = new CallHistoryService();
        private List<CallLine> _lines;
        private int _activeLineIndex;
        private readonly HashSet<string> _processedCallIds = new HashSet<string>();
        private readonly HashSet<string> _autoAnswerNumbers = new HashSet<string>();
        // Phonebook service for directory-based number resolution and contact lookup.
        private PhonebookService _phonebook;

        private SIPURI _aor;
        private SIPURI _contactUri;
        private bool _contactRewritten;
        private string _lastDialedNumber;
        // Speed-dial key-to-number mappings for quick number lookup.
        private readonly Dictionary<string, string> _speedDials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pre-computed mu-law to 16-bit PCM conversion table for call recording.
        private static readonly byte[] MuLawToPcm = new byte[256];

        static SipService()
        {
            for (int i = 0; i < 256; i++)
            {
                int muLaw = ~i;
                int sign = (muLaw & 0x80) != 0 ? -1 : 1;
                int exponent = (muLaw >> 4) & 0x07;
                int mantissa = muLaw & 0x0F;
                int sample = sign * ((mantissa << (exponent + 3)) + 0x84);
                MuLawToPcm[i] = (byte)(sample >> 8);
            }
        }

        /// <summary>Gets the call history service for logging and querying call records.</summary>
        public CallHistoryService CallHistory => _callHistoryService;
        /// <summary>Gets the list of phone lines managed by this service.</summary>
        public IReadOnlyList<CallLine> Lines => _lines;
        /// <summary>Gets the index of the currently active phone line.</summary>
        public int ActiveLineIndex => _activeLineIndex;
        /// <summary>Gets the currently active phone line.</summary>
        public CallLine ActiveLine => _lines?[_activeLineIndex];
        /// <summary>Gets whether DND (Do Not Disturb) is enabled.</summary>
        public bool DndEnabled => _dndEnabled;
        /// <summary>Gets the current call forwarding target, or null if disabled.</summary>
        public string ForwardingTarget => _forwardingTarget;
        /// <summary>Gets whether the active line has a pending (ringing) call.</summary>
        public bool HasPendingCall => ActiveLine?.HasPendingCall ?? false;
        /// <summary>Gets whether the SIP registration is active (online).</summary>
        public bool IsOnline => _isOnline;
        /// <summary>Gets whether the active line's microphone is muted.</summary>
        public bool IsMuted => ActiveLine?.IsMuted ?? false;
        /// <summary>Gets whether the active line is being recorded.</summary>
        public bool IsRecording => ActiveLine?.IsRecording ?? false;
        /// <summary>Gets the set of auto-answer whitelisted phone numbers.</summary>
        public IEnumerable<string> AutoAnswerNumbers => _autoAnswerNumbers;
        /// <summary>Gets the phonebook service for directory-based number resolution.</summary>
        public PhonebookService Phonebook => _phonebook;
        /// <summary>Gets the last dialed phone number.</summary>
        public string LastDialedNumber => _lastDialedNumber;
        /// <summary>Gets the speed-dial key-to-number mapping.</summary>
        public IReadOnlyDictionary<string, string> SpeedDials => _speedDials;
        /// <summary>Gets the currently configured SIP account.</summary>
        public SipAccount CurrentAccount => _currentAccount;

        public SipService(Settings settings, SipAccount account, bool verbose = false)
        {
            _settings = settings;
            _currentAccount = account;
            _verbose = verbose;
            _phonebook = new PhonebookService();
        }

        /// <summary>Switches to a new SIP account, shuts down current registration, and restarts the service.</summary>
        public void SwitchAccount(SipAccount account)
        {
            _currentAccount = account;
            Shutdown();
            Task.Run(async () => await StartAsync());
        }

        /// <summary>Shuts down SIP registration, hangs up active calls, disposes RTP sessions, and stops the transport.</summary>
        public void Shutdown()
        {
            CancelReconnect();

            if (_regUserAgent != null)
            {
                _regUserAgent.RegistrationSuccessful -= OnRegistrationSuccessful;
                _regUserAgent.RegistrationFailed -= OnRegistrationFailed;
                _regUserAgent.RegistrationTemporaryFailure -= OnRegistrationTemporaryFailure;
                _regUserAgent.Stop(true);
                _regUserAgent = null;
            }

            foreach (var line in _lines)
            {
                if (line.UserAgent.IsCallActive)
                    line.UserAgent.Hangup();
                line.RtpSession?.Dispose();
                line.RtpSession = null;
                line.TransferMediaSession?.Dispose();
                line.TransferMediaSession = null;
            }

            _sipTransport?.Shutdown();
            _isOnline = false;
        }

        /// <summary>Creates an RTP session with optional SRTP (DTLS or SDES) based on settings.</summary>
        private RTPSession CreateRtpSession()
        {
            var config = new RtpSessionConfig();
            // Configure SRTP security mode based on the SrtpMode setting (dtls, sdes, or disabled).
            switch (_settings.SrtpMode?.ToLower())
            {
                case "dtls":
                    config.RtpSecureMediaOption = RtpSecureMediaOptionEnum.DtlsSrtp;
                    break;
                case "sdes":
                    config.RtpSecureMediaOption = RtpSecureMediaOptionEnum.SdpCryptoNegotiation;
                    break;
            }
            return new RTPSession(config);
        }

        private SIPURI CreateSipUri(string user, string host, int port)
        {
            var hostPort = port > 0 ? $"{host}:{port}" : host;
            var scheme = _settings.UseTls ? SIPSchemesEnum.sips : SIPSchemesEnum.sip;
            return new SIPURI(user, hostPort, null, scheme);
        }

        /// <summary>Starts the SIP transport, configures channels (UDP/TLS), trace events, keep-alive handlers, creates phone lines, detects public IP, and begins PBX registration.</summary>
        public async Task StartAsync()
        {
            _sipTransport = new SIPTransport();

            var listenAddress = IPAddress.Any;
            var udpChannel = new SIPUDPChannel(listenAddress, _settings.Port);
            _sipTransport.AddSIPChannel(udpChannel);
            if (_verbose) CliHelper.Info($"UDP channel on port {_settings.Port}");

            // Set up TLS channel with optional client certificate for secure SIP signaling.
            if (_settings.UseTls)
            {
                try
                {
                    RemoteCertificateValidationCallback certValidator = (sender, cert, chain, errors) =>
                        !_settings.ValidateServerCert || errors == SslPolicyErrors.None;

                    X509Certificate2 serverCert = null;
                    if (!string.IsNullOrWhiteSpace(_settings.TlsCertPath) && File.Exists(_settings.TlsCertPath))
                    {
                        serverCert = new X509Certificate2(_settings.TlsCertPath, _settings.TlsCertPassword);
                        if (_verbose) CliHelper.Info($"Loaded TLS certificate: {serverCert.Subject}");
                    }

                    SIPTLSChannel tlsChannel;
                    if (serverCert != null)
                    {
                        tlsChannel = new SIPTLSChannel(serverCert, listenAddress, _settings.TlsPort);
                    }
                    else
                    {
                        var endpoint = new IPEndPoint(listenAddress, _settings.TlsPort);
                        tlsChannel = new SIPTLSChannel(endpoint, false, certValidator);
                    }
                    _sipTransport.AddSIPChannel(tlsChannel);
                    if (_verbose) CliHelper.Info($"TLS channel on port {_settings.TlsPort}");
                }
                catch (Exception ex)
                {
                    CliHelper.Error($"Failed to create TLS channel: {ex.Message}");
                }
            }

            if (_verbose)
            {
                _sipTransport.SIPRequestOutTraceEvent += (local, remote, req) =>
                {
                    CliHelper.Info($"[SIP OUT] {req.Method} {req.URI}");
                };
                _sipTransport.SIPResponseInTraceEvent += (local, remote, resp) =>
                {
                    CliHelper.Info($"[SIP IN] {(int)resp.Status} {resp.ReasonPhrase}");
                };
            }

            // Handle inbound NOTIFY and OPTIONS for PBX keep-alive.
            // Using SIPRequestInTraceEvent (pre-dialog-layer) ensures we catch in-dialog
            // NOTIFY messages that the dialog layer would otherwise intercept before
            // SIPTransportRequestReceived fires.
            _sipTransport.SIPRequestInTraceEvent += (local, remote, req) =>
            {
                if (req.Method == SIPMethodsEnum.NOTIFY)
                {
                    if (_verbose) CliHelper.Info($"[NOTIFY] from {remote}");
                    try
                    {
                        var response = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                        _sipTransport.SendResponseAsync(response);
                    }
                    catch (Exception ex)
                    {
                        if (_verbose) CliHelper.Error($"[NOTIFY] Error: {ex.Message}");
                    }
                }
                // Respond to OPTIONS (keep-alive ping) with 200 OK.
                else if (req.Method == SIPMethodsEnum.OPTIONS)
                {
                    if (_verbose) CliHelper.Info($"[OPTIONS] from {remote}");
                    try
                    {
                        var response = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                        _sipTransport.SendResponseAsync(response);
                    }
                    catch (Exception ex)
                    {
                        if (_verbose) CliHelper.Error($"[OPTIONS] Error: {ex.Message}");
                    }
                }
            };

            _lines = new List<CallLine>();
            for (int i = 0; i < _settings.LineCount; i++)
            {
                var line = new CallLine
                {
                    Id = i,
                    UserAgent = new SIPUserAgent(_sipTransport, null)
                };
                SetupLine(line);
                _lines.Add(line);
            }
            _activeLineIndex = 0;

            var domain = !string.IsNullOrWhiteSpace(_currentAccount.Domain) ? _currentAccount.Domain : _currentAccount.Server;
            _aor = CreateSipUri(_currentAccount.Extension, domain, 0);

            var localIP = SIPChannel.InternetDefaultAddress ?? IPAddress.Loopback;
            var contactPort = _settings.UseTls ? _settings.TlsPort : _settings.Port;

            if (!string.IsNullOrWhiteSpace(_settings.PublicIP))
            {
                _contactUri = CreateSipUri(_currentAccount.Extension, _settings.PublicIP, contactPort);
            }
            else
            {
                var publicIP = await DetectPublicIPAsync();
                if (publicIP != null && publicIP != localIP.ToString())
                {
                    _contactUri = CreateSipUri(_currentAccount.Extension, publicIP, contactPort);
                    if (_settings.UseTls)
                        _contactUri = CreateSipUri(_currentAccount.Extension, publicIP, contactPort);
                    else
                        _contactUri = CreateSipUri(_currentAccount.Extension, publicIP, contactPort);
                }
                else
                    _contactUri = CreateSipUri(_currentAccount.Extension, localIP.ToString(), contactPort);
            }
            _contactUri.Parameters.Set("ob", "");

            CreateRegistration();
        }

        /// <summary>Creates or recreates SIP registration with outbound proxy support and registers success/failure event handlers.</summary>
        private void CreateRegistration()
        {
            _regUserAgent?.Stop();

            SIPEndPoint outboundProxy = null;
            if (!string.IsNullOrWhiteSpace(_currentAccount.Proxy))
            {
                var proxyParts = _currentAccount.Proxy.Split(':');
                var proxyHost = proxyParts[0];
                var proxyProto = _settings.UseTls ? SIPProtocolsEnum.tls : SIPProtocolsEnum.udp;
                var proxyPort = proxyParts.Length > 1 && int.TryParse(proxyParts[1], out var pp) ? pp : (_settings.UseTls ? _settings.TlsPort : _currentAccount.Port);
                var proxyAddr = System.Net.Dns.GetHostAddresses(proxyHost).FirstOrDefault();
                if (proxyAddr != null)
                    outboundProxy = new SIPEndPoint(proxyProto, proxyAddr, proxyPort);
            }

            var regServer = _settings.UseTls ? $"{_currentAccount.Server}:{_settings.TlsPort}" : _currentAccount.Server;

            _regUserAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                outboundProxy,
                _aor,
                _currentAccount.Username,
                _currentAccount.Password,
                null,
                regServer,
                _contactUri,
                300,
                null,
                60,
                300,
                3,
                false);

            _regUserAgent.RegistrationSuccessful += OnRegistrationSuccessful;
            _regUserAgent.RegistrationFailed += OnRegistrationFailed;
            _regUserAgent.RegistrationTemporaryFailure -= OnRegistrationTemporaryFailure;
            _regUserAgent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;

            if (_verbose) CliHelper.Info("Starting registration...");
            _regUserAgent.Start();
            if (_verbose) CliHelper.Info("Registration started.");
        }

        /// <summary>
        /// Tries to detect the public IP address by querying multiple fallback web services.
        /// Used when PublicIP is not manually configured in settings.json.
        /// </summary>
        private static async Task<string> DetectPublicIPAsync()
        {
            var services = new[]
            {
                "https://api.ipify.org",
                "https://checkip.amazonaws.com",
                "https://icanhazip.com"
            };
            foreach (var url in services)
            {
                try
                {
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                    {
                        var ip = (await client.GetStringAsync(url)).Trim();
                        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                            return ip;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Called when SIP registration succeeds. Updates online state, cancels reconnection,
        /// checks for NAT (via Via headers) and re-registers with the detected public IP if needed.
        /// </summary>
        private void OnRegistrationSuccessful(SIPURI uri, SIPResponse response)
        {
            _isOnline = true;
            CancelReconnect();
            CliHelper.Success($"Registered: {_currentAccount.Extension} @ {_currentAccount.Server}");

            // Perform NAT detection by inspecting Via headers for received/rport values that differ from our local IP.
            if (!_contactRewritten && response != null)
            {
                string detectedIP = null;
                int detectedPort = 0;

                if (response.Header?.Vias != null && response.Header.Vias.Via != null)
                {
                    var localIP = SIPChannel.InternetDefaultAddress?.ToString();
                    foreach (var via in response.Header.Vias.Via)
                    {
                        if (!string.IsNullOrEmpty(via.ReceivedFromIPAddress) &&
                            via.ReceivedFromIPAddress != localIP)
                        {
                            detectedIP = via.ReceivedFromIPAddress;
                            detectedPort = via.ReceivedFromPort > 0 ? via.ReceivedFromPort : _settings.Port;
                            if (_verbose) CliHelper.Warning($"NAT detected via Via received: IP={detectedIP}, Port={detectedPort}");
                            break;
                        }
                    }
                }

                if (detectedIP != null)
                {
                    var newContact = new SIPURI(_currentAccount.Extension, $"{detectedIP}:{detectedPort}", null);
                    newContact.Parameters.Set("ob", "");

                    if (newContact.ToString() != _contactUri.ToString())
                    {
                        if (_verbose) CliHelper.Warning($"Re-registering with public address {detectedIP}:{detectedPort}...");
                        _contactUri = newContact;
                        _contactRewritten = true;
                        Task.Run(() => CreateRegistration());
                        return;
                    }
                }
            }
        }

        /// <summary>Handles registration failure: sets offline state, checks for unrecoverable errors (DNS, empty input), and schedules reconnection.</summary>
        private void OnRegistrationFailed(SIPURI uri, SIPResponse response, string error)
        {
            _isOnline = false;
            CliHelper.Error($"Registration failed: {error}");
            var noRetry = string.IsNullOrWhiteSpace(_currentAccount?.Server)
                || error?.IndexOf("DNS", StringComparison.OrdinalIgnoreCase) >= 0
                || error?.IndexOf("empty input", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!noRetry)
                ScheduleReconnect();
        }

        /// <summary>Logs temporary registration failures (e.g., network glitches) without triggering a full reconnection cycle.</summary>
        private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse response, string error)
        {
            if (_verbose)
                CliHelper.Info("Registration temporary failure: " + error);
        }

        /// <summary>Schedules periodic re-registration attempts every 30 seconds after a registration failure.</summary>
        private void ScheduleReconnect()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = new Timer(_ =>
            {
                CliHelper.Info("Attempting re-registration...");
                try
                {
                    CreateRegistration();
                }
                catch (Exception ex)
                {
                    CliHelper.Error($"Re-registration failed: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>Cancels any pending reconnection timer.</summary>
        public void CancelReconnect()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        /// <summary>
        /// Configures all event handlers for a single phone line: call state transitions
        /// (ringing, answered, failed, hungup), incoming call handling (DND, forwarding,
        /// auto-answer, manual answer), DTMF reception, and attended transfer completion.
        /// </summary>
        private void SetupLine(CallLine line)
        {
            line.UserAgent.ClientCallTrying += (uac, sipResponse) =>
            {
                if (_verbose) CliHelper.Info($"Line {line.Id}: Call TRYING");
            };

            line.UserAgent.ClientCallRinging += (uac, sipResponse) =>
            {
                CliHelper.Info($"Line {line.Id}: Ringing...");
            };

            line.UserAgent.ClientCallAnswered += async (uac, sipResponse) =>
            {
                if (line.TransferOriginalDialogue != null)
                {
                    CliHelper.Info($"Line {line.Id}: Consultation answered. Completing attended transfer...");
                    bool result = await line.UserAgent.AttendedTransfer(
                        line.TransferOriginalDialogue,
                        TimeSpan.FromSeconds(30),
                        System.Threading.CancellationToken.None,
                        null,
                        _currentAccount.Username,
                        _currentAccount.Password);
                    line.TransferOriginalDialogue = null;
                    line.RtpSession?.Dispose();
                    line.RtpSession = null;
                    line.TransferMediaSession?.Dispose();
                    line.TransferMediaSession = null;

                    if (result)
                        CliHelper.Success($"Line {line.Id}: Attended transfer completed");
                    else
                        CliHelper.Error($"Line {line.Id}: Attended transfer failed");

                    line.UserAgent.Hangup();
                }
                else
                {
                    line.CallStartTime = DateTime.Now;
                    CliHelper.Event($"Line {line.Id}: Call answered");
                    _callHistoryService.Update(line.CallHistoryId, e =>
                    {
                        e.Status = "Answered";
                        e.StartTime = DateTime.Now;
                    });
                }
            };

            line.UserAgent.ClientCallFailed += (uac, errorMessage, sipResponse) =>
            {
                CliHelper.Error($"Line {line.Id}: Call failed: {errorMessage}");
                _callHistoryService.Update(line.CallHistoryId, e =>
                {
                    e.Status = "Failed";
                    e.EndTime = DateTime.Now;
                });
            };

            // Handles incoming calls: deduplicates by Call-ID, then routes to DND,
            // forwarding, auto-answer, or prompts the user.
            line.UserAgent.OnIncomingCall += async (ua, req) =>
            {
                lock (_processedCallIds)
                {
                    if (!_processedCallIds.Add(req.Header.CallId))
                        return;
                }

                var uas = line.UserAgent.AcceptCall(req);

                var callerName = req.Header.From?.FromName;
                var callerUri = req.Header.From?.FromURI?.User ?? req.Header.From?.FromURI?.ToString() ?? "unknown";
                var caller = !string.IsNullOrEmpty(callerName) ? $"{callerName} <{callerUri}>" : callerUri;
                var callerNum = req.Header.From?.FromURI?.User ?? "";

                if (_dndEnabled)
                {
                    CliHelper.Warning($"\n=== DND: REJECTING CALL FROM {caller} ===");
                    _callHistoryService.Add(new CallHistoryEntry
                    {
                        RemoteParty = caller,
                        Direction = "Incoming",
                        Status = "Missed",
                        StartTime = DateTime.Now
                    });
                    await AutoReject(uas, line);
                }
                else if (_forwardingTarget != null)
                {
                    CliHelper.Warning($"\n=== FORWARDING CALL FROM {caller} TO {_forwardingTarget} ===");
                    _callHistoryService.Add(new CallHistoryEntry
                    {
                        RemoteParty = caller,
                        Direction = "Incoming",
                        Status = "Forwarded",
                        StartTime = DateTime.Now
                    });
                    var forwardUri = new SIPURI(_forwardingTarget, _currentAccount.Server, null);
                    uas.Redirect(SIPResponseStatusCodesEnum.MovedTemporarily, forwardUri);
                }
                else if (_autoAnswerNumbers.Count > 0 && _autoAnswerNumbers.Contains(callerNum))
                {
                    CliHelper.Success($"\n=== AUTO-ANSWERING CALL FROM {caller} ON LINE {line.Id} ===");
                    _callHistoryService.Add(new CallHistoryEntry
                    {
                        RemoteParty = caller,
                        Direction = "Incoming",
                        Status = "Answered",
                        StartTime = DateTime.Now
                    });
                    await AutoAnswerCall(uas, line);
                    CliHelper.Event($"Line {line.Id}: === CALL ANSWERED ===");
                }
                else
                {
                    CliHelper.Warning($"\n=== INCOMING CALL FROM: {caller} ON LINE {line.Id} ===");
                    CliHelper.Info("Type 'answer' or 'reject' to respond.");
                    line.PendingCallUAS = uas;
                    line.PendingCallRequest = req;
                    line.CallHistoryId = Guid.NewGuid().ToString("N");
                    _callHistoryService.Add(new CallHistoryEntry
                    {
                        Id = line.CallHistoryId,
                        RemoteParty = caller,
                        Direction = "Incoming",
                        Status = "Ringing",
                        StartTime = DateTime.Now
                    });
                }
            };

            line.UserAgent.OnCallHungup += async (dialogue) =>
            {
                if (line.TransferOriginalDialogue != null)
                {
                    CliHelper.Info($"Line {line.Id}: Consultation ended. Resuming original call...");
                    await SendReinviteForDialogue(line.TransferOriginalDialogue, MediaStreamStatusEnum.SendRecv);
                    line.TransferOriginalDialogue = null;
                    line.TransferMediaSession?.Dispose();
                    line.TransferMediaSession = null;
                    return;
                }

                CliHelper.Event($"Line {line.Id}: Call ended");
                StopRecording(line);
                _callHistoryService.Update(line.CallHistoryId, e =>
                {
                    e.EndTime = DateTime.Now;
                    if (e.StartTime != default)
                        e.DurationSeconds = (e.EndTime.Value - e.StartTime).TotalSeconds;
                    if (e.Status == "Ringing")
                        e.Status = "Missed";
                });
                line.RtpSession?.Dispose();
                line.RtpSession = null;
                line.PendingCallUAS = null;
                line.PendingCallRequest = null;
                line.IsMuted = false;
            };

            line.UserAgent.OnDtmfTone += (byte tone, int duration) =>
            {
                if (_verbose) CliHelper.Info($"Line {line.Id}: DTMF received: {tone}");
            };
        }

        /// <summary>
        /// Rejects an incoming call by answering (to cancel all forked branches)
        /// then immediately hanging up.
        /// </summary>
        private async Task AutoReject(SIPServerUserAgent uas, CallLine line)
        {
            var rtpSession = CreateRtpSession();
            rtpSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                MediaStreamStatusEnum.SendRecv,
                null,
                null
            ));
            await line.UserAgent.Answer(uas, rtpSession);
            await Task.Delay(500);
            line.UserAgent.Hangup();
        }

        /// <summary>Auto-answers an incoming call from a whitelisted number.</summary>
        private async Task AutoAnswerCall(SIPServerUserAgent uas, CallLine line)
        {
            await HoldOtherLines(line);
            var rtpSession = CreateRtpSession();
            rtpSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                MediaStreamStatusEnum.SendRecv,
                null,
                null
            ));
            line.RtpSession = rtpSession;
            line.CallStartTime = DateTime.Now;
            await line.UserAgent.Answer(uas, line.RtpSession);
            uas = null;
        }

        /// <summary>Switches the active phone line by index.</summary>
        public void SwitchLine(int index)
        {
            if (index >= 0 && index < _lines.Count)
            {
                _activeLineIndex = index;
                CliHelper.Info($"Switched to line {index}");
            }
        }

        /// <summary>
        /// Places an outgoing call: puts other lines on hold, creates an RTP session,
        /// adds a PCMU media track, and initiates via SIPUserAgent.Call.
        /// </summary>
        public async Task MakeCallAsync(string targetUri)
        {
            var line = ActiveLine;
            CliHelper.Info($"Line {line.Id}: Calling {targetUri}...");

            await HoldOtherLines(line);

            line.RtpSession = CreateRtpSession();
            line.RtpSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                MediaStreamStatusEnum.SendRecv,
                null,
                null
            ));

            line.CallHistoryId = Guid.NewGuid().ToString("N");
            _callHistoryService.Add(new CallHistoryEntry
            {
                Id = line.CallHistoryId,
                RemoteParty = targetUri,
                Direction = "Outgoing",
                Status = "Ringing",
                StartTime = DateTime.Now
            });
            _lastDialedNumber = targetUri;
            bool result = await line.UserAgent.Call(targetUri, _currentAccount.Username, _currentAccount.Password, line.RtpSession);
            if (!result)
            {
                _callHistoryService.Update(line.CallHistoryId, e => e.Status = "Failed");
            }
            if (result)
            {
                line.CallStartTime = DateTime.Now;
                CliHelper.Success($"=== LINE {line.Id}: CALL ACTIVE ===");
            }
            else
                CliHelper.Error($"=== LINE {line.Id}: CALL FAILED ===");
        }

        /// <summary>Toggles microphone mute on the active call via RTP stream status.</summary>
        public void ToggleMute()
        {
            var line = ActiveLine;
            if (!line.IsCallActive || line.RtpSession == null)
            {
                CliHelper.Warning($"Line {line.Id}: No active call to mute.");
                return;
            }

            line.IsMuted = !line.IsMuted;
            var status = line.IsMuted ? MediaStreamStatusEnum.SendOnly : MediaStreamStatusEnum.SendRecv;
            line.RtpSession.SetMediaStreamStatus(SDPMediaTypesEnum.audio, status);
            CliHelper.Info($"Line {line.Id}: Microphone {(line.IsMuted ? "MUTED" : "UNMUTED")}");
        }

        /// <summary>Performs a blind transfer by sending a REFER request.</summary>
        public async Task BlindTransferTo(string target)
        {
            var line = ActiveLine;
            if (!line.UserAgent.IsCallActive)
            {
                CliHelper.Warning($"Line {line.Id}: No active call to transfer.");
                return;
            }

            try
            {
                // Build target URI with the correct scheme (sips for TLS, sip otherwise) and perform blind transfer via REFER.
                var scheme = _settings.UseTls ? SIPSchemesEnum.sips : SIPSchemesEnum.sip;
                var uri = new SIPURI(target, _currentAccount.Server, null, scheme);
                CliHelper.Info($"Line {line.Id}: Transferring to {target}...");
                bool result = await line.UserAgent.BlindTransfer(
                    uri,
                    TimeSpan.FromSeconds(30),
                    System.Threading.CancellationToken.None,
                    null,
                    _currentAccount.Username,
                    _currentAccount.Password);
                if (result)
                    CliHelper.Success($"=== LINE {line.Id}: TRANSFER SUCCESSFUL ===");
                else
                    CliHelper.Error($"=== LINE {line.Id}: TRANSFER FAILED ===");
            }
            catch (Exception ex)
            {
                CliHelper.Info($"Line {line.Id}: Transfer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Performs an attended transfer: puts the original call on hold,
        /// places a consultation call to the target, and bridges on answer.
        /// </summary>
        public async Task AttendedTransferTo(string target)
        {
            var line = ActiveLine;
            if (!line.UserAgent.IsCallActive)
            {
                CliHelper.Info($"Line {line.Id}: No active call to transfer.");
                return;
            }

            // Store the original dialogue, put it on hold (Inactive), then create a new RTP session for the consultation call.
            line.TransferOriginalDialogue = line.UserAgent.Dialogue;
            CliHelper.Info($"Line {line.Id}: Putting original call on hold...");
            await SendReinviteForDialogue(line.TransferOriginalDialogue, MediaStreamStatusEnum.Inactive);

            var rtpSession = CreateRtpSession();
            rtpSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                MediaStreamStatusEnum.SendRecv,
                null,
                null
            ));
            line.TransferMediaSession = rtpSession;

            var scheme = _settings.UseTls ? "sips" : "sip";
            var dst = target.Contains("@") ? target : $"{scheme}:{target}@{_currentAccount.Server}";
            CliHelper.Info($"Line {line.Id}: Consultation call to {target}...");
            bool callResult = await line.UserAgent.Call(dst, _currentAccount.Username, _currentAccount.Password, line.TransferMediaSession, 30);
            if (!callResult)
            {
                CliHelper.Info($"Line {line.Id}: Failed to initiate consultation call to {target}.");
                line.TransferMediaSession?.Dispose();
                line.TransferMediaSession = null;
            }
        }

        /// <summary>Hangs up the active call on the current line.</summary>
        public void HangUp()
        {
            var line = ActiveLine;
            if (line.UserAgent.IsCallActive)
            {
                line.UserAgent.Hangup();
                CliHelper.Info($"Line {line.Id}: Hung up");
            }
        }

        /// <summary>Redials the last dialed number.</summary>
        public async Task Redial()
        {
            if (string.IsNullOrWhiteSpace(_lastDialedNumber))
            {
                CliHelper.Warning("No previous call to redial.");
                return;
            }
            CliHelper.Info($"Redialing {_lastDialedNumber}...");
            await MakeCallAsync(_lastDialedNumber);
        }

        /// <summary>Sets a speed dial key to a phone number.</summary>
        public void SetSpeedDial(string key, string number)
        {
            _speedDials[key] = number;
            CliHelper.Success($"Speed dial {key} -> {number}");
        }

        /// <summary>Removes a speed dial entry.</summary>
        public void RemoveSpeedDial(string key)
        {
            if (_speedDials.Remove(key))
                CliHelper.Info($"Speed dial {key} removed");
            else
                CliHelper.Warning($"Speed dial {key} not found");
        }

        /// <summary>Clears all speed dials.</summary>
        public void ClearSpeedDials()
        {
            _speedDials.Clear();
            CliHelper.Info("Speed dials cleared");
        }

        /// <summary>Resolves a speed dial key to its number, or returns null.</summary>
        public string ResolveSpeedDial(string key)
        {
            return _speedDials.TryGetValue(key, out var num) ? num : null;
        }

        /// <summary>Puts active calls on all lines except the specified one on hold.</summary>
        private async Task HoldOtherLines(CallLine activeLine)
        {
            foreach (var line in _lines)
            {
                if (line.Id == activeLine.Id) continue;
                if (line.UserAgent.IsCallActive && line.UserAgent.Dialogue != null)
                {
                    await SendReinviteForDialogue(line.UserAgent.Dialogue, MediaStreamStatusEnum.Inactive);
                }
            }
        }

        /// <summary>
        /// Sends a re-INVITE to update the media stream status on a specific dialogue.
        /// Used for hold/unhold and attended transfer.
        /// </summary>
        private async Task SendReinviteForDialogue(SIPDialogue dialogue, MediaStreamStatusEnum streamStatus)
        {
            var tempSession = CreateRtpSession();
            tempSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                streamStatus,
                null,
                null
            ));

            var sdp = tempSession.CreateOffer(System.Net.IPAddress.Loopback);
            var sdpBody = sdp.ToString();
            var reInvite = dialogue.GetInDialogRequest(SIPMethodsEnum.INVITE, null);
            reInvite.Body = sdpBody;
            reInvite.Header.ContentType = "application/sdp";
            reInvite.Header.ContentLength = System.Text.Encoding.UTF8.GetByteCount(sdpBody);

            await _sipTransport.SendRequestAsync(reInvite, false);
            tempSession.Dispose();
        }

        /// <summary>Sends an RFC 2833 DTMF tone on the active call.</summary>
        public async Task SendDtmf(byte digit)
        {
            // Send DTMF using RFC 2833 telephone-event payload (in-band via RTP stream).
            await ActiveLine.UserAgent.SendDtmf(digit);
            CliHelper.Info($"Line {ActiveLine.Id}: Sent DTMF (RFC 2833): {digit}");
        }

        /// <summary>Sends a SIP INFO DTMF tone with application/dtmf-relay body.</summary>
        public async Task SendDtmfInfo(byte digit)
        {
            var line = ActiveLine;
            if (line.UserAgent.Dialogue == null)
            {
                CliHelper.Warning($"Line {line.Id}: No active dialogue to send SIP INFO DTMF.");
                return;
            }

            try
            {
                // Build SIP INFO request with application/dtmf-relay body containing the signal digit and duration.
                var infoReq = line.UserAgent.Dialogue.GetInDialogRequest(SIPMethodsEnum.INFO, null);
                var body = $"Signal={digit}\r\nDuration=160";
                infoReq.Body = body;
                infoReq.Header.ContentType = "application/dtmf-relay";
                infoReq.Header.ContentLength = System.Text.Encoding.UTF8.GetByteCount(body);
                await _sipTransport.SendRequestAsync(infoReq, false);
                CliHelper.Success($"Line {line.Id}: Sent DTMF (INFO): {digit}");
            }
            catch (Exception ex)
            {
                CliHelper.Error($"Line {line.Id}: SIP INFO DTMF failed: {ex.Message}");
            }
        }

        /// <summary>Puts the active call on hold via SIPUserAgent.</summary>
        public void PutOnHold()
        {
            ActiveLine.UserAgent.PutOnHold();
            CliHelper.Info($"Line {ActiveLine.Id}: Call on hold");
        }

        /// <summary>Takes the active call off hold via SIPUserAgent.</summary>
        public void TakeOffHold()
        {
            ActiveLine.UserAgent.TakeOffHold();
            CliHelper.Info($"Line {ActiveLine.Id}: Call off hold");
        }

        /// <summary>Toggles Do Not Disturb mode.</summary>
        public void ToggleDnd()
        {
            _dndEnabled = !_dndEnabled;
            if (_dndEnabled)
                CliHelper.Warning("DND ENABLED");
            else
                CliHelper.Info("DND DISABLED");
        }

        /// <summary>Enables call forwarding to the specified target.</summary>
        public void SetForwarding(string target)
        {
            _forwardingTarget = target;
            CliHelper.Warning($"CALL FORWARDING ENABLED -> {target}");
        }

        /// <summary>Disables call forwarding.</summary>
        public void DisableForwarding()
        {
            _forwardingTarget = null;
            CliHelper.Info("CALL FORWARDING DISABLED");
        }

        /// <summary>Adds a number to the auto-answer whitelist.</summary>
        public void AddAutoAnswer(string number)
        {
            _autoAnswerNumbers.Add(number);
            CliHelper.Success($"Auto-answer enabled for {number}");
        }

        /// <summary>Removes a number from the auto-answer whitelist.</summary>
        public void RemoveAutoAnswer(string number)
        {
            _autoAnswerNumbers.Remove(number);
            CliHelper.Info($"Auto-answer disabled for {number}");
        }

        /// <summary>Clears the auto-answer whitelist.</summary>
        public void ClearAutoAnswer()
        {
            _autoAnswerNumbers.Clear();
            CliHelper.Info("Auto-answer list cleared");
        }

        /// <summary>
        /// Answers a pending incoming call: puts other lines on hold,
        /// creates an RTP session with PCMU track, and accepts the call.
        /// </summary>
        public async Task AnswerCall()
        {
            var line = ActiveLine;
            if (line.PendingCallUAS == null)
            {
                CliHelper.Info($"Line {line.Id}: No pending call to answer.");
                return;
            }

            await HoldOtherLines(line);

            var rtpSession = CreateRtpSession();
            rtpSession.addTrack(new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                },
                MediaStreamStatusEnum.SendRecv,
                null,
                null
            ));
            line.RtpSession = rtpSession;

            await line.UserAgent.Answer(line.PendingCallUAS, line.RtpSession);
            line.CallStartTime = DateTime.Now;
            CliHelper.Event($"Line {line.Id}: === CALL ANSWERED ===");
            _callHistoryService.Update(line.CallHistoryId, e =>
            {
                e.Status = "Answered";
                e.StartTime = DateTime.Now;
            });
            line.PendingCallUAS = null;
            line.PendingCallRequest = null;
        }

        /// <summary>
        /// Rejects a pending call by answering then immediately hanging up.
        /// This cancels all forked branches (other registered devices stop ringing).
        /// </summary>
        public async Task RejectCall()
        {
            var line = ActiveLine;
            if (line.PendingCallUAS == null)
            {
                CliHelper.Info($"Line {line.Id}: No pending call to reject.");
                return;
            }

            try
            {
                var rtpSession = CreateRtpSession();
                rtpSession.addTrack(new MediaStreamTrack(
                    SDPMediaTypesEnum.audio,
                    false,
                    new List<SDPAudioVideoMediaFormat>
                    {
                        new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 0, "PCMU", 8000, 1, null)
                    },
                    MediaStreamStatusEnum.SendRecv,
                    null,
                    null
                ));
                line.RtpSession = rtpSession;
                await line.UserAgent.Answer(line.PendingCallUAS, line.RtpSession);
                await Task.Delay(500);
                line.UserAgent.Hangup();
                line.RtpSession?.Dispose();
                line.RtpSession = null;
                CliHelper.Warning($"Line {line.Id}: === CALL REJECTED ===");
                _callHistoryService.Update(line.CallHistoryId, e =>
                {
                    e.Status = "Rejected";
                    e.EndTime = DateTime.Now;
                });
            }
            catch (Exception ex)
            {
                CliHelper.Error($"Line {line.Id}: Failed to reject call: {ex.Message}");
                line.RtpSession?.Dispose();
                line.RtpSession = null;
            }

            line.PendingCallUAS = null;
            line.PendingCallRequest = null;
        }

        /// <summary>
        /// Starts recording the active call as a WAV file.
        /// Converts mu-law audio to 16-bit PCM via pre-computed lookup table.
        /// </summary>
        public async Task StartRecording()
        {
            var line = ActiveLine;
            if (!line.IsCallActive || line.RtpSession == null)
            {
                CliHelper.Warning($"Line {line.Id}: No active call to record.");
                return;
            }

            if (line.IsRecording)
            {
                CliHelper.Warning($"Line {line.Id}: Already recording.");
                return;
            }

            try
            {
                var fileName = $"call_{line.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                var sampleRate = 8000;
                var bitsPerSample = 16;
                var channels = 1;
                var header = new byte[44];
                // Write WAV header: RIFF chunk descriptor with PCM format, 8 kHz sample rate, 16-bit mono.
                Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, header, 0, 4);
                Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, header, 8, 4);
                Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, header, 12, 4);
                header[16] = 16;
                header[17] = 0;
                header[18] = 1;
                header[19] = 0;
                header[20] = (byte)channels;
                header[21] = 0;
                header[22] = (byte)(sampleRate & 0xFF);
                header[23] = (byte)((sampleRate >> 8) & 0xFF);
                header[24] = (byte)((sampleRate * channels * bitsPerSample / 8) & 0xFF);
                header[25] = (byte)(((sampleRate * channels * bitsPerSample / 8) >> 8) & 0xFF);
                header[26] = (byte)((channels * bitsPerSample / 8) & 0xFF);
                header[27] = (byte)(((channels * bitsPerSample / 8) >> 8) & 0xFF);
                header[28] = (byte)(bitsPerSample & 0xFF);
                header[29] = (byte)((bitsPerSample >> 8) & 0xFF);
                Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("data"), 0, header, 36, 4);

                await stream.WriteAsync(header, 0, 44);
                line.RecordingStream = stream;

                line.RtpSession.OnAudioFrameReceived += OnAudioFrameReceived;
                line.IsRecording = true;
                CliHelper.Success($"Line {line.Id}: Recording started -> {fileName}");
            }
            catch (Exception ex)
            {
                CliHelper.Error($"Line {line.Id}: Failed to start recording: {ex.Message}");
            }
        }

        /// <summary>
        /// Callback for each received audio frame during recording.
        /// Converts PCMU to 16-bit PCM and writes to the WAV file.
        /// </summary>
        private void OnAudioFrameReceived(EncodedAudioFrame frame)
        {
            var line = ActiveLine;
            if (!line.IsRecording || line.RecordingStream == null) return;

            try
            {
                var data = frame.EncodedAudio;
                if (data == null) return;

                var formatName = frame.AudioFormat.FormatName;
                // Convert PCMU (mu-law) audio to 16-bit linear PCM using the pre-computed lookup table.
                if (formatName == "PCMU")
                {
                    var pcm = new byte[data.Length * 2];
                    for (int i = 0; i < data.Length; i++)
                    {
                        short sample = MuLawToPcm[data[i]];
                        pcm[i * 2] = (byte)(sample & 0xFF);
                        pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                    line.RecordingStream.Write(pcm, 0, pcm.Length);
                }
                else
                {
                    line.RecordingStream.Write(data, 0, data.Length);
                }
            }
            catch { }
        }

        /// <summary>
        /// Stops recording, finalises the WAV header (updates RIFF and data chunk sizes),
        /// and closes the file stream.
        /// </summary>
        public void StopRecording(CallLine line = null)
        {
            line = line ?? ActiveLine;
            if (!line.IsRecording) return;

            try
            {
                if (line.RtpSession != null)
                    line.RtpSession.OnAudioFrameReceived -= OnAudioFrameReceived;

                if (line.RecordingStream != null)
                {
                    var fileSize = (int)line.RecordingStream.Length;
                    line.RecordingStream.Seek(4, SeekOrigin.Begin);
                    var sizeBytes = BitConverter.GetBytes(fileSize - 8);
                    line.RecordingStream.Write(sizeBytes, 0, 4);
                    line.RecordingStream.Seek(40, SeekOrigin.Begin);
                    sizeBytes = BitConverter.GetBytes(fileSize - 44);
                    line.RecordingStream.Write(sizeBytes, 0, 4);
                    line.RecordingStream.Close();
                    line.RecordingStream = null;
                }

                line.IsRecording = false;
                CliHelper.Info($"Line {line.Id}: Recording stopped");
            }
            catch (Exception ex)
            {
                CliHelper.Error($"Line {line.Id}: Failed to stop recording: {ex.Message}");
                line.RecordingStream?.Close();
                line.RecordingStream = null;
                line.IsRecording = false;
            }
        }
    }
}
