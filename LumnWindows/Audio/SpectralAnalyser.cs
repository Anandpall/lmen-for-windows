using System;
using System.Linq;
using NAudio.Dsp;

namespace LumnWindows.Audio;

public struct SpectralFrame
{
    // 6 Frequency bands (0.0 - 1.0)
    public float SubBass;      // 20–60 Hz    — kick drum body, deep rumble
    public float Bass;         // 60–250 Hz   — bass guitar, kick attack, low synths
    public float LowMid;       // 250–500 Hz  — upper bass, guitar body, male vocals
    public float Mid;          // 500–2000 Hz — most instruments live here, snare
    public float UpperMid;     // 2–6 kHz     — vocal presence, guitars, hi-hats
    public float Air;          // 6–20 kHz    — cymbals, sibilance, sparkle

    // Derived values
    public float BeatOnset;    // Spectral flux — sudden transient energy increase
    public float Warmth;       // (SubBass + Bass) / 2
    public float Brightness;   // (UpperMid + Air) / 2
    public float Fullness;     // Average of all 6
}

public class SpectralAnalyser
{
    private const int FftSize = 2048;
    private const int HopSize = 512;
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _window;
    private readonly float[] _inputQueue = new float[FftSize * 2];
    private int _queuePos = 0;

    private readonly float[] _prevBandEnergy = new float[6];
    private readonly float[] _smoothed = new float[6];

    private const float AttackCoeff = 0.82f;
    public float ReleaseCoeff { get; set; } = 0.15f;
    public double Sensitivity { get; set; } = 1.0;

    public SpectralFrame CurrentFrame { get; private set; }
    public event Action<SpectralFrame>? FrameReady;

    public SpectralAnalyser()
    {
        _window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
        }
    }

    public void Feed(float[] samples)
    {
        if (samples == null || samples.Length == 0) return;

        for (int sIdx = 0; sIdx < samples.Length; sIdx++)
        {
            if (_queuePos < _inputQueue.Length)
            {
                _inputQueue[_queuePos++] = samples[sIdx];
            }

            if (_queuePos >= FftSize)
            {
                ProcessFFT();

                // Shift queue by HopSize
                int remaining = _queuePos - HopSize;
                if (remaining > 0)
                {
                    Array.Copy(_inputQueue, HopSize, _inputQueue, 0, remaining);
                    _queuePos = remaining;
                }
                else
                {
                    _queuePos = 0;
                }
            }
        }
    }

    private void ProcessFFT()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = _inputQueue[i] * _window[i];
            _fftBuffer[i].Y = 0;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), _fftBuffer);

        float binHz = 44100f / FftSize;
        float[] bandEnergy = new float[6];

        for (int bin = 1; bin < FftSize / 2; bin++)
        {
            float hz = bin * binHz;
            float mag = (float)Math.Sqrt(_fftBuffer[bin].X * _fftBuffer[bin].X
                                       + _fftBuffer[bin].Y * _fftBuffer[bin].Y);

            int bandIdx = FrequencyToBand(hz);
            if (bandIdx >= 0) bandEnergy[bandIdx] += mag;
        }

        // Normalize each band by bin count
        bandEnergy[0] /= BinsInRange(20, 60, binHz);
        bandEnergy[1] /= BinsInRange(60, 250, binHz);
        bandEnergy[2] /= BinsInRange(250, 500, binHz);
        bandEnergy[3] /= BinsInRange(500, 2000, binHz);
        bandEnergy[4] /= BinsInRange(2000, 6000, binHz);
        bandEnergy[5] /= BinsInRange(6000, 20000, binHz);

        // Spectral flux for onset
        float flux = 0;
        for (int b = 0; b < 6; b++)
        {
            float diff = bandEnergy[b] - _prevBandEnergy[b];
            if (diff > 0) flux += diff;
            _prevBandEnergy[b] = bandEnergy[b];
        }

        float sens = (float)Math.Clamp(Sensitivity, 0.5, 3.0);

        // Smooth bands with asymmetric attack/release
        for (int b = 0; b < 6; b++)
        {
            float raw = Math.Clamp(bandEnergy[b] * 9f * sens, 0f, 1f);
            float coeff = raw > _smoothed[b] ? AttackCoeff : ReleaseCoeff;
            _smoothed[b] = _smoothed[b] * (1f - coeff) + raw * coeff;
        }

        float smoothedFlux = Math.Clamp(flux * 5f * sens, 0f, 1f);

        CurrentFrame = new SpectralFrame
        {
            SubBass = _smoothed[0],
            Bass = _smoothed[1],
            LowMid = _smoothed[2],
            Mid = _smoothed[3],
            UpperMid = _smoothed[4],
            Air = _smoothed[5],
            BeatOnset = smoothedFlux,
            Warmth = (_smoothed[0] + _smoothed[1]) * 0.5f,
            Brightness = (_smoothed[4] + _smoothed[5]) * 0.5f,
            Fullness = _smoothed.Average()
        };

        FrameReady?.Invoke(CurrentFrame);
    }

    private static int FrequencyToBand(float hz)
    {
        if (hz < 20) return -1;
        if (hz < 60) return 0;    // Sub-bass
        if (hz < 250) return 1;   // Bass
        if (hz < 500) return 2;   // Low-mid
        if (hz < 2000) return 3;  // Mid
        if (hz < 6000) return 4;  // Upper-mid
        if (hz < 20000) return 5; // Air
        return -1;
    }

    private static int BinsInRange(float low, float high, float binHz)
        => Math.Max(1, (int)((high - low) / binHz));
}
