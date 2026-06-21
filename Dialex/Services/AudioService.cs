using NAudio.Codecs;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System;

namespace IP_Phone.Services
{
    /// <summary>
    /// Provides 2-way audio for a single call line using NAudio.
    /// Captures microphone audio (PCM 16-bit) → encodes to PCMU → sends via RTP.
    /// Receives PCMU from RTP → decodes to PCM 16-bit → plays through speakers.
    /// </summary>
    public class AudioService : IDisposable
    {
        private WaveInEvent _waveIn;
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;
        private RTPSession _rtpSession;
        private bool _disposed;

        /// <summary>Starts microphone capture and speaker playback for the given RTP session.</summary>
        public void Start(RTPSession rtpSession)
        {
            _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(8000, 16, 1)
            };
            _waveIn.DataAvailable += OnMicData;
            _waveIn.StartRecording();

            _waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };
            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100
            };
            _waveOut.Init(_waveProvider);
            _waveOut.Play();

            _rtpSession.OnAudioFrameReceived += OnAudioReceived;
        }

        private void OnMicData(object sender, WaveInEventArgs e)
        {
            if (_disposed || _rtpSession == null) return;

            var muLaw = new byte[e.BytesRecorded / 2];
            for (int i = 0; i < muLaw.Length; i++)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                muLaw[i] = MuLawEncoder.LinearToMuLawSample(sample);
            }

            try
            {
                _rtpSession.SendAudio((uint)muLaw.Length, muLaw);
            }
            catch { }
        }

        private void OnAudioReceived(EncodedAudioFrame frame)
        {
            if (_disposed || _waveProvider == null || frame?.EncodedAudio == null) return;

            if (frame.AudioFormat.FormatName == "PCMU")
            {
                var pcm = new byte[frame.EncodedAudio.Length * 2];
                for (int i = 0; i < frame.EncodedAudio.Length; i++)
                {
                    short sample = MuLawDecoder.MuLawToLinearSample(frame.EncodedAudio[i]);
                    pcm[i * 2] = (byte)(sample & 0xFF);
                    pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
                _waveProvider.AddSamples(pcm, 0, pcm.Length);
            }
        }

        /// <summary>Stops capture and playback, releases all resources.</summary>
        public void Stop()
        {
            if (_disposed) return;

            if (_rtpSession != null)
                _rtpSession.OnAudioFrameReceived -= OnAudioReceived;

            if (_waveIn != null)
            {
                try { _waveIn.StopRecording(); } catch { }
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_waveOut != null)
            {
                try { _waveOut.Stop(); } catch { }
                _waveOut.Dispose();
                _waveOut = null;
            }

            _waveProvider = null;
            _rtpSession = null;
        }

        public void Dispose() { Stop(); _disposed = true; }
    }
}
