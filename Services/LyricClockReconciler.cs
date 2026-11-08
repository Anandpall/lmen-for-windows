using System;

namespace LmenV2.Services
{
    /// <summary>
    /// Reconciles the 60 FPS frame-driven lyric clock with slower, jittery media session playback reports.
    /// Inspired by BitChord's monotonic LyricClockReconciler: protects smooth 60 FPS interpolation from
    /// polling latency micro-rewinds while instantly snapping on real user seeks.
    /// </summary>
    public class LyricClockReconciler
    {
        private TimeSpan _lastReported = TimeSpan.Zero;
        private DateTime _lastObservedUtc = DateTime.UtcNow;
        private bool _wasPlaying = false;
        private const double SeekDiscontinuitySeconds = 1.25;

        public void Reset(TimeSpan initialPosition, bool isPlaying)
        {
            _lastReported = initialPosition;
            _lastObservedUtc = DateTime.UtcNow;
            _wasPlaying = isPlaying;
        }

        public TimeSpan Reconcile(TimeSpan displayedTime, TimeSpan reportedTime, bool isPlaying)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastObservedUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            var expected = _lastReported + (_wasPlaying ? elapsed : TimeSpan.Zero);
            bool discontinuity = !isPlaying || Math.Abs((reportedTime - expected).TotalSeconds) > SeekDiscontinuitySeconds;

            _lastReported = reportedTime;
            _lastObservedUtc = now;
            _wasPlaying = isPlaying;

            if (discontinuity)
            {
                return reportedTime;
            }

            // Normal playback: keep strictly monotonic so transport lag never rewinds or hitches words
            return displayedTime > reportedTime ? displayedTime : reportedTime;
        }
    }
}
