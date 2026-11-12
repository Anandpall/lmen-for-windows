using System;
using System.Collections.Generic;
using System.Linq;

namespace LmenV2.Models
{
    public enum LyricAlignment
    {
        Start,
        End
    }

    public class LyricSyllable
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public int StartChar { get; set; }
        public int EndChar { get; set; }

        public LyricSyllable() { }

        public LyricSyllable(long startMs, long endMs, int startChar, int endChar)
        {
            StartMs = startMs;
            EndMs = endMs;
            StartChar = startChar;
            EndChar = endChar;
        }

        public static List<LyricSyllable> Normalized(List<LyricSyllable> parts, int length)
        {
            if (parts == null) return new List<LyricSyllable>();
            var usable = parts
                .Where(p => p.EndChar > p.StartChar && p.StartChar < length)
                .OrderBy(p => p.StartChar)
                .ToList();

            if (usable.Count < 2) return new List<LyricSyllable>();

            var result = new List<LyricSyllable>(usable.Count);
            for (int i = 0; i < usable.Count; i++)
            {
                var part = usable[i];
                int start = i == 0 ? 0 : part.StartChar;
                int end = i + 1 < usable.Count ? usable[i + 1].StartChar : length;
                result.Add(new LyricSyllable(part.StartMs, Math.Max(part.StartMs, part.EndMs), start, end));
            }
            return result;
        }
    }

    public class LyricWord
    {
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public List<LyricSyllable> Syllables { get; set; } = new List<LyricSyllable>();

        public float CharsSungAt(TimeSpan position)
        {
            long posMs = (long)position.TotalMilliseconds;
            long startMs = (long)StartTime.TotalMilliseconds;
            long endMs = (long)EndTime.TotalMilliseconds;

            if (posMs <= startMs) return 0f;
            if (posMs >= endMs) return Text.Length;

            if (Syllables == null || Syllables.Count == 0)
            {
                long span = Math.Max(1L, endMs - startMs);
                return (float)(posMs - startMs) / span * Text.Length;
            }

            for (int i = 0; i < Syllables.Count; i++)
            {
                var syl = Syllables[i];
                if (posMs < syl.StartMs) return syl.StartChar;
                if (posMs < syl.EndMs)
                {
                    long span = Math.Max(1L, syl.EndMs - syl.StartMs);
                    float through = (float)(posMs - syl.StartMs) / span;
                    return syl.StartChar + through * (syl.EndChar - syl.StartChar);
                }
            }

            return Text.Length;
        }
    }

    public class LyricLine
    {
        public int Index { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? BackgroundText { get; set; }
        public LyricLine? BackgroundLine { get; set; }
        public LyricAlignment Alignment { get; set; } = LyricAlignment.Start;
        public TimeSpan? SungUntil { get; set; }
        public bool IsGap { get; set; }
        public List<LyricWord> Words { get; set; } = new List<LyricWord>();
        public bool HasWordSync => Words != null && Words.Count > 0;
        public bool IsActive { get; set; }
        public bool IsPast { get; set; }

        public bool HasKnownEnd => (Words != null && Words.Count > 0) || SungUntil.HasValue;

        public TimeSpan KnownEnd
        {
            get
            {
                var lead = (Words != null && Words.Count > 0) ? Words[^1].EndTime : (SungUntil ?? EndTime);
                if (BackgroundLine != null)
                {
                    return lead > BackgroundLine.KnownEnd ? lead : BackgroundLine.KnownEnd;
                }
                return lead;
            }
        }
    }

    public class TrackLyrics
    {
        public string SongKey { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public string ArtistName { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string? Isrc { get; set; }
        public bool IsSynced { get; set; }
        public bool HasWordSync { get; set; }
        public bool IsInstrumental { get; set; }
        public List<LyricLine> Lines { get; set; } = new List<LyricLine>();
        public string PlainLyrics { get; set; } = string.Empty;
        public bool HasLyrics => IsSynced || IsInstrumental || !string.IsNullOrWhiteSpace(PlainLyrics);
    }
}
