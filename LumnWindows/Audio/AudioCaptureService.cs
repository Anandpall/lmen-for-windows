using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LumnWindows.Audio;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    public event Action<float[]>? SamplesReady;

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            var format = _capture.WaveFormat;

            _capture.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded == 0) return;

                if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    int floatCount = e.BytesRecorded / 4;
                    float[] newSamples = new float[floatCount];
                    Buffer.BlockCopy(e.Buffer, 0, newSamples, 0, e.BytesRecorded);

                    if (format.Channels == 2)
                    {
                        int monoCount = floatCount / 2;
                        float[] mono = new float[monoCount];
                        for (int i = 0; i < monoCount; i++)
                        {
                            mono[i] = (newSamples[i * 2] + newSamples[i * 2 + 1]) * 0.5f;
                        }
                        SamplesReady?.Invoke(mono);
                    }
                    else if (format.Channels > 2)
                    {
                        int channels = format.Channels;
                        int monoCount = floatCount / channels;
                        float[] mono = new float[monoCount];
                        for (int i = 0; i < monoCount; i++)
                        {
                            float sum = 0f;
                            for (int c = 0; c < channels; c++) sum += newSamples[i * channels + c];
                            mono[i] = sum / channels;
                        }
                        SamplesReady?.Invoke(mono);
                    }
                    else
                    {
                        SamplesReady?.Invoke(newSamples);
                    }
                }
                else if (format.BitsPerSample == 16)
                {
                    int sampleCount = e.BytesRecorded / 2;
                    int channels = format.Channels;
                    int monoCount = sampleCount / channels;
                    float[] mono = new float[monoCount];

                    for (int i = 0; i < monoCount; i++)
                    {
                        float sum = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            short val = BitConverter.ToInt16(e.Buffer, (i * channels + c) * 2);
                            sum += val / 32768f;
                        }
                        mono[i] = sum / channels;
                    }
                    SamplesReady?.Invoke(mono);
                }
            };

            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioCaptureService error: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
