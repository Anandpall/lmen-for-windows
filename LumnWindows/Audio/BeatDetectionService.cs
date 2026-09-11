using System;
using System.Diagnostics;
using NAudio.Dsp;

namespace LumnWindows.Audio;

public class BeatDetectionService
{
    private const int FftSize = 1024;
    private readonly Complex[] _fftBuffer = new Complex[FftSize];

    // Circular buffer for flux history
    private const int HistoryLength = 32;
    private readonly float[] _fluxHistory = new float[HistoryLength];
    private int _historyIndex = 0;
    private int _historyFilled = 0;

    private float _prevBassEnergy = 0f;
    private long _lastBeatTimestamp = 0;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public float BeatIntensity { get; private set; }
    public float OverallVolume { get; private set; }
    public double Sensitivity { get; set; } = 1.25;

    public event Action<float>? BeatTriggered;

    public void ProcessSamples(float[] samples)
    {
        if (samples == null || samples.Length == 0) return;

        // Calculate RMS volume and apply Hann window
        float sumSquares = 0f;
        int count = Math.Min(samples.Length, FftSize);
        for (int i = 0; i < count; i++)
        {
            sumSquares += samples[i] * samples[i];
            float window = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            _fftBuffer[i] = new Complex { X = samples[i] * window, Y = 0 };
        }
        for (int i = count; i < FftSize; i++)
        {
            _fftBuffer[i] = new Complex { X = 0, Y = 0 };
        }

        OverallVolume = (float)Math.Sqrt(sumSquares / count);

        // Compute FFT
        FastFourierTransform.FFT(true, (int)Math.Log(FftSize, 2), _fftBuffer);

        // Focus strictly on Kick Drum & Sub-Bass band (~35 Hz to ~140 Hz, bins 1 to 4)
        float currentBassEnergy = 0f;
        for (int i = 1; i <= 4; i++)
        {
            currentBassEnergy += (float)(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);
        }

        // Spectral Flux = positive rate of energy increase (transient onset)
        float flux = Math.Max(0f, currentBassEnergy - _prevBassEnergy);
        _prevBassEnergy = currentBassEnergy;

        // Store in circular buffer
        _fluxHistory[_historyIndex] = flux;
        _historyIndex = (_historyIndex + 1) % HistoryLength;
        if (_historyFilled < HistoryLength) _historyFilled++;

        // Calculate Mean and Standard Deviation of recent flux
        float mean = 0f;
        for (int i = 0; i < _historyFilled; i++) mean += _fluxHistory[i];
        mean /= _historyFilled;

        float sumVariance = 0f;
        for (int i = 0; i < _historyFilled; i++)
        {
            float diff = _fluxHistory[i] - mean;
            sumVariance += diff * diff;
        }
        float stdDev = (float)Math.Sqrt(sumVariance / _historyFilled);

        // Dynamic threshold based on user Sensitivity
        // Higher sensitivity = lower threshold multiplier
        float sensitivityFactor = (float)Math.Clamp(2.4 - (Sensitivity * 0.8), 0.6, 2.2);
        float threshold = mean + stdDev * sensitivityFactor;

        long now = _stopwatch.ElapsedMilliseconds;
        long timeSinceLastBeat = now - _lastBeatTimestamp;

        // Lockout of 115ms (approx 520 BPM max) prevents double-triggering on single kick tail
        if (flux > threshold && flux > 0.003f && timeSinceLastBeat > 115)
        {
            float hitRatio = Math.Clamp((flux - threshold) / (threshold * 0.8f + 0.001f), 0.4f, 1.0f);
            BeatIntensity = Math.Max(BeatIntensity, hitRatio);
            _lastBeatTimestamp = now;

            BeatTriggered?.Invoke(BeatIntensity);
        }

        // Smooth decay per audio buffer update (~15-20ms)
        BeatIntensity *= 0.85f;
        if (BeatIntensity < 0.01f) BeatIntensity = 0f;
    }

    public void DecayFrame()
    {
        BeatIntensity *= 0.88f;
        if (BeatIntensity < 0.005f) BeatIntensity = 0f;
    }
}
