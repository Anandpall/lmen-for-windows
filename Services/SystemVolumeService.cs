using System;
using NAudio.CoreAudioApi;

namespace LmenV2.Services
{
    public sealed class SystemVolumeService : IDisposable
    {
        private static readonly Lazy<SystemVolumeService> _lazy = new(() => new SystemVolumeService());
        public static SystemVolumeService Instance => _lazy.Value;

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private AudioEndpointVolume? _volume;
        private readonly object _lock = new();
        private bool _disposed;

        public event Action<float, bool>? VolumeChanged;

        public SystemVolumeService()
        {
            InitializeDevice();
        }

        private void InitializeDevice()
        {
            lock (_lock)
            {
                try
                {
                    CleanupDevice();

                    _enumerator = new MMDeviceEnumerator();
                    _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (_device != null)
                    {
                        _volume = _device.AudioEndpointVolume;
                        if (_volume != null)
                        {
                            _volume.OnVolumeNotification += OnVolumeNotification;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SystemVolumeService] Init failed: {ex.Message}");
                }
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try
            {
                VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
            }
            catch { }
        }

        public float MasterVolume
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (_volume != null)
                        {
                            return _volume.MasterVolumeLevelScalar;
                        }
                    }
                    catch
                    {
                        InitializeDevice();
                        try { return _volume?.MasterVolumeLevelScalar ?? 0.5f; } catch { }
                    }
                    return 0.5f;
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        float clamped = Math.Clamp(value, 0f, 1f);
                        if (_volume != null)
                        {
                            _volume.MasterVolumeLevelScalar = clamped;
                        }
                    }
                    catch
                    {
                        InitializeDevice();
                        try
                        {
                            if (_volume != null)
                            {
                                _volume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        public bool IsMuted
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (_volume != null)
                        {
                            return _volume.Mute;
                        }
                    }
                    catch
                    {
                        InitializeDevice();
                        try { return _volume?.Mute ?? false; } catch { }
                    }
                    return false;
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        if (_volume != null)
                        {
                            _volume.Mute = value;
                        }
                    }
                    catch
                    {
                        InitializeDevice();
                        try
                        {
                            if (_volume != null)
                            {
                                _volume.Mute = value;
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
        }

        public void StepVolume(float delta)
        {
            float current = MasterVolume;
            float target = Math.Clamp(current + delta, 0f, 1f);
            MasterVolume = target;
            if (target > 0f && IsMuted)
            {
                IsMuted = false;
            }
        }

        private void CleanupDevice()
        {
            if (_volume != null)
            {
                try { _volume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                _volume = null;
            }
            if (_device != null)
            {
                try { _device.Dispose(); } catch { }
                _device = null;
            }
            if (_enumerator != null)
            {
                try { _enumerator.Dispose(); } catch { }
                _enumerator = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                CleanupDevice();
            }
        }
    }
}
