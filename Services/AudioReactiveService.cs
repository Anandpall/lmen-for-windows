using System;
using System.Threading;
using NAudio.Wave;

namespace LmenV2.Services
{
    public class AudioReactiveService : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private readonly object _lock = new object();

        // WasapiLoopbackCapture only raises DataAvailable while the render endpoint is
        // actually mixing audio. When playback stops the callback simply stops firing, so
        // the decay code inside it never runs and the glow used to freeze stuck at its last
        // (bright) value. This watchdog decays the envelope back to rest while silent.
        // It also covers the case where the capture dies because the default output device
        // changed (e.g. Bluetooth headphones connected) - we restart on the new device.
        private System.Threading.Timer? _watchdog;
        private long _lastDataTicks;
        private volatile bool _stopping;
        private volatile bool _disposed;

        // Low-pass filter state for bass/kick isolation (cutoff ~150Hz)
        private float _lastBassSample = 0f;
        private float _lpAlpha = 0.05f; // Calculated on format discovery

        // Energy envelope & dynamic AGC
        private float _smoothedEnergy = 0f;
        private float _peakEnergy = 0.05f; // Dynamic AGC ceiling

        public bool IsRunning { get; private set; } = false;
        public event Action<float>? BeatPulse;

        public float CurrentEnergy
        {
            get
            {
                lock (_lock)
                {
                    return _smoothedEnergy;
                }
            }
        }

        public void Start()
        {
            if (IsRunning || _disposed) return;

            WasapiLoopbackCapture capture;
            try
            {
                capture = new WasapiLoopbackCapture();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioReactiveService] Start failed: {ex.Message}");
                return;
            }

            try
            {
                // Compute low-pass filter alpha for ~150Hz cutoff: alpha = dt / (rc + dt)
                float sampleRate = capture.WaveFormat.SampleRate > 0 ? capture.WaveFormat.SampleRate : 48000f;
                const float fc = 150f;
                float frameSeconds = 1f / sampleRate;
                float rc = 1f / (2f * (float)Math.PI * fc);
                float alpha = frameSeconds / (rc + frameSeconds);

                lock (_lock)
                {
                    if (IsRunning || _disposed)
                    {
                        capture.Dispose();
                        return;
                    }
                    _lpAlpha = alpha;
                    _capture = capture;
                    _stopping = false;
                }

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;

                Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
                capture.StartRecording();

                IsRunning = true;

                // 10 Hz watchdog: pulls the glow back to rest while no buffers arrive.
                _watchdog ??= new System.Threading.Timer(_ => DecayWhenSilent(), null, 100, 100);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioReactiveService] Start failed: {ex.Message}");
                try { capture.Dispose(); } catch { }
                lock (_lock)
                {
                    _capture = null;
                }
                IsRunning = false;
            }
        }

        public void Stop()
        {
            // Permanent stop: never auto-restart after this.
            _stopping = true;

            WasapiLoopbackCapture? capture;
            lock (_lock)
            {
                capture = _capture;
                if (capture == null) return;
                _capture = null;
            }

            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.StopRecording();
            }
            catch { }
            finally
            {
                try { capture.Dispose(); } catch { }
                IsRunning = false;
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            IsRunning = false;

            // Stop() owns disposal for a deliberate shutdown.
            if (_stopping) return;

            // NAudio also raises this when the default render endpoint changes (plugging in
            // headphones, switching output, sleep/resume). Without a restart the reactive
            // glow stays dead for the rest of the session.
            //
            // Disposal is deferred to another thread on purpose: depending on the
            // synchronization context NAudio captured when the device was created, this
            // handler can run on the capture thread itself, where Dispose() would Join the
            // very thread it is executing on and hang the app.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(200);

                WasapiLoopbackCapture? stale;
                lock (_lock)
                {
                    stale = _capture;
                    _capture = null;
                }

                if (stale != null)
                {
                    try
                    {
                        stale.DataAvailable -= OnDataAvailable;
                        stale.RecordingStopped -= OnRecordingStopped;
                        stale.Dispose();
                    }
                    catch { }
                }

                if (_stopping) return;

                // Give the new endpoint a moment to become the default, then re-target it.
                Thread.Sleep(1000);
                try { Start(); } catch { }
            });
        }

        private void DecayWhenSilent()
        {
            if (!IsRunning) return;

            long last = Interlocked.Read(ref _lastDataTicks);
            if (Environment.TickCount64 - last < 150) return;

            lock (_lock)
            {
                _smoothedEnergy *= 0.72f;
                if (_smoothedEnergy < 0.0005f) _smoothedEnergy = 0f;
                _peakEnergy = Math.Max(_peakEnergy * 0.98f, 0.015f);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture == null || e.BytesRecorded == 0) return;

            try
            {
                int channels = Math.Max(1, _capture.WaveFormat.Channels);
                int bitsPerSample = _capture.WaveFormat.BitsPerSample;
                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample <= 0) return;

                int frameSize = bytesPerSample * channels;
                int maxFrames = e.BytesRecorded / frameSize;
                if (maxFrames <= 0) return;

                double bassEnergySum = 0;
                double totalRmsSum = 0;

                for (int f = 0; f < maxFrames; f++)
                {
                    int offset = f * frameSize;
                    if (offset + bytesPerSample > e.Buffer.Length) break;

                    float sample = 0f;

                    if (bitsPerSample == 32)
                    {
                        sample = BitConverter.ToSingle(e.Buffer, offset);
                    }
                    else if (bitsPerSample == 16)
                    {
                        short s = BitConverter.ToInt16(e.Buffer, offset);
                        sample = s / 32768.0f;
                    }
                    else if (bitsPerSample == 24)
                    {
                        int val = (e.Buffer[offset + 2] << 16) | (e.Buffer[offset + 1] << 8) | e.Buffer[offset];
                        if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                        sample = val / 8388608.0f;
                    }

                    // Low-Pass filter to isolate bass frequencies (kick drum, 808s, bass guitar)
                    _lastBassSample = _lastBassSample + _lpAlpha * (sample - _lastBassSample);

                    bassEnergySum += _lastBassSample * _lastBassSample;
                    totalRmsSum += sample * sample;
                }

                // Root-mean-square for bass band
                float bassRms = (float)Math.Sqrt(bassEnergySum / maxFrames);
                float totalRms = (float)Math.Sqrt(totalRmsSum / maxFrames);

                // Blend bass (80%) and overall RMS (20%) for full musical response
                float instantEnergy = (bassRms * 0.8f) + (totalRms * 0.2f);

                float energy;
                lock (_lock)
                {
                    // Automatic Gain Control (AGC) - adapts to quiet music and loud music
                    if (instantEnergy > _peakEnergy)
                    {
                        _peakEnergy = instantEnergy;
                    }
                    else
                    {
                        // Slowly decay peak over time
                        _peakEnergy = Math.Max(_peakEnergy * 0.995f, 0.015f);
                    }

                    // Normalized energy factor: 0.0 to 1.0
                    float range = Math.Max(0.005f, _peakEnergy);
                    float normalized = Math.Clamp(instantEnergy / range, 0f, 1f);

                    // Nonlinear expansion: square the value so quiet background sounds stay quiet,
                    // and bass kicks produce a sharp, satisfying surge!
                    float punchEnergy = normalized * normalized;

                    // Fast attack (instant jump on beat), smooth exponential decay
                    if (punchEnergy > _smoothedEnergy)
                    {
                        _smoothedEnergy = (_smoothedEnergy * 0.2f) + (punchEnergy * 0.8f);
                    }
                    else
                    {
                        _smoothedEnergy = (_smoothedEnergy * 0.84f) + (punchEnergy * 0.16f);
                    }

                    energy = _smoothedEnergy;
                }

                Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);

                // Publish the value captured under the lock instead of re-reading the
                // shared field from the audio callback thread.
                BeatPulse?.Invoke(energy);
            }
            catch
            {
                // Never crash audio processing
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();

            System.Threading.Timer? watchdog = _watchdog;
            _watchdog = null;
            if (watchdog != null)
            {
                try { watchdog.Dispose(); } catch { }
            }
        }
    }
}
