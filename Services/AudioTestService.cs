using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Generates and plays WAV test tones for left/right speaker channel verification.
    /// Uses raw WAV synthesis — no external files or Media Foundation needed.
    /// Safe to call repeatedly; previous playback is stopped and disposed before each new play.
    /// </summary>
    public class AudioTestService : IDisposable
    {
        private System.Media.SoundPlayer? _player;
        private readonly object _lock = new();
        private bool _disposed;

        // ── Public API ────────────────────────────────────────────────────────

        public enum Channel { Left, Right, Both }

        /// <summary>
        /// Plays a sine-wave tone on the specified <paramref name="channel"/>.
        /// Safe to call from any thread.  Returns immediately after starting playback.
        /// </summary>
        public void PlayTone(Channel channel, int frequencyHz = 1000, int durationMs = 1500)
        {
            if (_disposed) return;

            _ = Task.Run(() =>  // CS4014: fire-and-forget intentional — audio plays in background
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    StopInternal();

                    try
                    {
                        byte[] wav = BuildWav(channel, frequencyHz, durationMs);
                        var ms = new MemoryStream(wav);
                        _player = new System.Media.SoundPlayer(ms);
                        // Play synchronously on this background thread so we can detect completion.
                        // This avoids the race condition in SoundPlayer.PlaySync() when called
                        // from multiple rapid button-presses.
                        _player.Load();
                        _player.PlaySync();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException
                                                    or InvalidOperationException
                                                    or IOException)
                    {
                        // Swallow: can happen if Stop() is called concurrently.
                    }
                    catch { /* never crash the UI */ }
                    finally
                    {
                        // Always release resources after playback finishes or fails.
                        lock (_lock) { DisposePlayer(); }
                    }
                }
            });
        }

        /// <summary>Stops any currently playing tone immediately.</summary>
        public void Stop()
        {
            lock (_lock) { StopInternal(); }
        }

        // ── WAV synthesis ─────────────────────────────────────────────────────

        private static byte[] BuildWav(Channel channel, int freqHz, int durationMs)
        {
            const int sampleRate   = 44100;
            const int bitsPerSample = 16;
            const int channels      = 2;   // always stereo
            int numSamples = sampleRate * durationMs / 1000;
            int dataBytes  = numSamples * channels * (bitsPerSample / 8);

            using var ms  = new MemoryStream();
            using var bw  = new BinaryWriter(ms);

            // ── RIFF header ──────────────────────────────────────────────────
            bw.Write(new[] { 'R','I','F','F' });
            bw.Write(36 + dataBytes);               // ChunkSize
            bw.Write(new[] { 'W','A','V','E' });

            // ── fmt sub-chunk ────────────────────────────────────────────────
            bw.Write(new[] { 'f','m','t',' ' });
            bw.Write(16);                           // Subchunk1Size (PCM)
            bw.Write((short)1);                     // AudioFormat = PCM
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * bitsPerSample / 8);   // ByteRate
            bw.Write((short)(channels * bitsPerSample / 8));        // BlockAlign
            bw.Write((short)bitsPerSample);

            // ── data sub-chunk ───────────────────────────────────────────────
            bw.Write(new[] { 'd','a','t','a' });
            bw.Write(dataBytes);

            // Apply a short fade-in / fade-out envelope to avoid clicks
            for (int i = 0; i < numSamples; i++)
            {
                double t       = (double)i / sampleRate;
                double sine    = Math.Sin(2 * Math.PI * freqHz * t);

                // Envelope: 10 ms fade-in, 20 ms fade-out
                double env = 1.0;
                int fadeIn  = sampleRate * 10 / 1000;
                int fadeOut = sampleRate * 20 / 1000;
                if (i < fadeIn)
                    env = (double)i / fadeIn;
                else if (i > numSamples - fadeOut)
                    env = (double)(numSamples - i) / fadeOut;

                double amplitude = 0.70 * short.MaxValue * env;
                short sample     = (short)(sine * amplitude);

                // Left channel
                short left  = channel == Channel.Right ? (short)0 : sample;
                // Right channel
                short right = channel == Channel.Left  ? (short)0 : sample;

                bw.Write(left);
                bw.Write(right);
            }

            bw.Flush();
            return ms.ToArray();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void StopInternal()
        {
            try { _player?.Stop(); } catch { }
            DisposePlayer();
        }

        private void DisposePlayer()
        {
            try { _player?.Dispose(); } catch { }
            _player = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                StopInternal();
            }
        }
    }
}
