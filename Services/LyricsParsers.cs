using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using LmenV2.Models;

namespace LmenV2.Services
{
    public class TimedRun
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public string Raw { get; set; } = string.Empty;

        public TimedRun() { }

        public TimedRun(long startMs, long endMs, string raw)
        {
            StartMs = startMs;
            EndMs = endMs;
            Raw = raw ?? string.Empty;
        }
    }

    /// <summary>
    /// Glues syllable runs into words with attached LyricSyllable timings, ported from BitChord (SyllableRuns.kt).
    /// </summary>
    public static class SyllableRuns
    {
        public static List<LyricWord> WordsFromRuns(List<TimedRun> runs, bool spacingIsExplicit = false)
        {
            if (runs == null || runs.Count == 0) return new List<LyricWord>();

            bool glue = spacingIsExplicit || runs.Any(r => r.Raw.Any(char.IsWhiteSpace));

            var words = new List<LyricWord>();
            var current = new StringBuilder();
            var parts = new List<LyricSyllable>();
            long start = 0L;
            long end = 0L;

            void Flush()
            {
                if (current.Length > 0)
                {
                    string text = current.ToString();
                    words.Add(new LyricWord
                    {
                        Index = words.Count,
                        StartTime = TimeSpan.FromMilliseconds(start),
                        EndTime = TimeSpan.FromMilliseconds(Math.Max(start, end)),
                        Text = text,
                        Syllables = LyricSyllable.Normalized(parts, text.Length)
                    });
                }
                current.Clear();
                parts.Clear();
            }

            foreach (var run in runs)
            {
                string content = run.Raw.Trim();
                if (string.IsNullOrEmpty(content))
                {
                    Flush();
                    continue;
                }

                bool joins = glue && current.Length > 0 && !char.IsWhiteSpace(run.Raw[0]) &&
                             !IsUnspacedScript(current[current.Length - 1]) && !IsUnspacedScript(content[0]);

                if (!joins) Flush();

                if (current.Length == 0)
                {
                    start = run.StartMs;
                    end = run.StartMs;
                }

                int from = current.Length;
                current.Append(content);
                long runEnd = Math.Max(run.StartMs, run.EndMs);
                parts.Add(new LyricSyllable(run.StartMs, runEnd, from, current.Length));
                end = Math.Max(end, runEnd);

                if (!glue || char.IsWhiteSpace(run.Raw[^1]))
                {
                    Flush();
                }
            }

            Flush();
            return words;
        }

        private static bool IsUnspacedScript(char c)
        {
            return (c >= '\u3400' && c <= '\u9FFF') ||  // Han
                   (c >= '\u3040' && c <= '\u30FF') ||  // Hiragana and Katakana
                   (c >= '\u3000' && c <= '\u303F') ||  // CJK punctuation
                   (c >= '\uFF00' && c <= '\uFFEF') ||  // Full-width forms
                   (c >= '\u0E00' && c <= '\u0E7F');    // Thai
        }
    }

    /// <summary>
    /// Background vocal detection and splitting, ported from BitChord (BackgroundVocals.kt).
    /// </summary>
    public static class BackgroundVocals
    {
        public static List<LyricLine> WithBackgroundVocals(List<LyricLine> lines)
        {
            if (lines == null || lines.Count == 0) return lines ?? new List<LyricLine>();
            var outList = new List<LyricLine>(lines.Count);
            foreach (var line in lines)
            {
                outList.Add(SplitTrailingBracket(line));
            }
            return outList;
        }

        private static LyricLine SplitTrailingBracket(LyricLine line)
        {
            if (line.BackgroundLine != null || line.IsGap || string.IsNullOrEmpty(line.Text)) return line;

            int? openIdx = BracketStart(line.Text);
            if (!openIdx.HasValue) return line;

            int open = openIdx.Value;
            string lead = line.Text.Substring(0, open).TrimEnd();
            string backing = line.Text.Substring(open).Trim();

            if (string.IsNullOrEmpty(lead) || !backing.Any(char.IsLetterOrDigit)) return line;

            if (line.Words == null || line.Words.Count == 0)
            {
                // Line-synced: share line timing and stack
                line.Text = lead;
                line.BackgroundLine = new LyricLine
                {
                    StartTime = line.StartTime,
                    EndTime = line.EndTime,
                    Text = backing,
                    SungUntil = line.SungUntil
                };
                line.BackgroundText = backing;
                return line;
            }

            int? splitIdx = IndexOfFirstStartingAt(line.Words, open);
            if (!splitIdx.HasValue || splitIdx.Value <= 0) return line;

            int split = splitIdx.Value;
            var leadWords = line.Words.Take(split).ToList();
            var backingWords = line.Words.Skip(split).ToList();

            line.Text = lead;
            line.Words = leadWords;
            line.BackgroundLine = new LyricLine
            {
                StartTime = backingWords[0].StartTime,
                EndTime = backingWords[^1].EndTime,
                Text = backing,
                Words = backingWords
            };
            line.BackgroundText = backing;
            return line;
        }

        private static int? IndexOfFirstStartingAt(List<LyricWord> words, int offset)
        {
            int at = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (at == offset) return i;
                if (at > offset) return null;
                at += words[i].Text.Length + 1; // Joined by single space
            }
            return null;
        }

        private static int? BracketStart(string text)
        {
            if (!text.EndsWith(")")) return null;
            int depth = 0;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == ')') depth++;
                else if (text[i] == '(')
                {
                    depth--;
                    if (depth == 0) return i > 0 ? i : null;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Instrumental gap and interlude breaks, ported from BitChord (LyricGaps.kt).
    /// </summary>
    public static class LyricGaps
    {
        public const long MinGapMs = 4000L;

        public static List<LyricLine> WithInstrumentalGaps(List<LyricLine> lines)
        {
            if (lines == null || lines.Count == 0) return lines ?? new List<LyricLine>();

            var result = new List<LyricLine>(lines.Count + 4);

            // Intro gap
            if (lines[0].StartTime.TotalMilliseconds >= MinGapMs)
            {
                result.Add(new LyricLine
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = lines[0].StartTime,
                    Text = "",
                    IsGap = true
                });
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                result.Add(line);

                if (i >= lines.Count - 1) continue;
                var next = lines[i + 1];

                if (!line.HasKnownEnd) continue;

                var lineEnd = line.KnownEnd;
                double silenceMs = (next.StartTime - lineEnd).TotalMilliseconds;

                if (silenceMs >= MinGapMs && lineEnd > line.StartTime)
                {
                    result.Add(new LyricLine
                    {
                        StartTime = lineEnd,
                        EndTime = next.StartTime,
                        Text = "",
                        IsGap = true
                    });
                }
            }

            // Assign indices
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Index = i;
            }

            return result;
        }
    }

    /// <summary>
    /// Duet left/right voice alignment calculation, ported from BitChord (LyricAlignments.kt).
    /// </summary>
    public static class LyricAlignments
    {
        private const string Person = "person";
        private const string Group = "group";
        private const string Other = "other";
        private const string GroupAgent = "v1000";
        private const string OtherAgent = "v2000";
        private const float MostlyRight = 0.85f;

        public static List<LyricAlignment> ComputeAlignments(List<string?> singers, Dictionary<string, string> types)
        {
            if (singers == null || singers.Count == 0) return new List<LyricAlignment>();

            bool left = true;
            string? lastVoice = null;
            int rightward = 0;
            int placed = 0;

            var sides = new List<LyricAlignment>(singers.Count);

            foreach (var singer in singers)
            {
                if (string.IsNullOrEmpty(singer))
                {
                    sides.Add(LyricAlignment.Start);
                    continue;
                }

                string type = (types != null && types.TryGetValue(singer, out var t))
                    ? t
                    : (singer == GroupAgent ? Group : (singer == OtherAgent ? Other : Person));

                placed++;
                if (type == Group)
                {
                    sides.Add(LyricAlignment.Start);
                    continue;
                }

                if (lastVoice == null)
                {
                    left = type != Other;
                }
                else if (singer != lastVoice)
                {
                    left = !left;
                }

                lastVoice = singer;
                if (!left) rightward++;
                sides.Add(left ? LyricAlignment.Start : LyricAlignment.End);
            }

            if (placed > 0 && (float)rightward / placed >= MostlyRight)
            {
                for (int i = 0; i < sides.Count; i++)
                {
                    sides[i] = sides[i] == LyricAlignment.Start ? LyricAlignment.End : LyricAlignment.Start;
                }
            }

            return sides;
        }
    }

    /// <summary>
    /// Apple Music TTML parser ported from BitChord (TtmlLyrics.kt).
    /// </summary>
    public static class TtmlLyrics
    {
        private static readonly HashSet<string> SkippedRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "x-translation", "x-roman"
        };
        private const string BackgroundRole = "x-bg";

        public static List<LyricLine> Parse(string ttml)
        {
            if (string.IsNullOrWhiteSpace(ttml)) return new List<LyricLine>();

            try
            {
                var doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.LoadXml(ttml);

                // Collect agent types in head
                var agentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var agentNodes = doc.GetElementsByTagName("ttm:agent");
                if (agentNodes.Count == 0) agentNodes = doc.GetElementsByTagName("agent");

                for (int i = 0; i < agentNodes.Count; i++)
                {
                    if (agentNodes[i] is XmlElement el)
                    {
                        string id = el.GetAttribute("xml:id");
                        if (string.IsNullOrEmpty(id)) id = el.GetAttribute("id");
                        string type = el.GetAttribute("type");
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(type))
                        {
                            agentTypes[id] = type;
                        }
                    }
                }

                var pNodes = doc.GetElementsByTagName("p");
                var sung = new List<(LyricLine line, string? agent)>(pNodes.Count);

                for (int i = 0; i < pNodes.Count; i++)
                {
                    if (pNodes[i] is not XmlElement pEl) continue;

                    var line = LineFromParagraph(pEl);
                    if (line == null) continue;

                    string agent = pEl.GetAttribute("ttm:agent");
                    sung.Add((line, string.IsNullOrEmpty(agent) ? null : agent));
                }

                sung.Sort((a, b) => a.line.StartTime.CompareTo(b.line.StartTime));

                var sides = LyricAlignments.ComputeAlignments(sung.Select(s => s.agent).ToList(), agentTypes);
                var lines = new List<LyricLine>(sung.Count);

                for (int i = 0; i < sung.Count; i++)
                {
                    var item = sung[i].line;
                    item.Alignment = i < sides.Count ? sides[i] : LyricAlignment.Start;
                    lines.Add(item);
                }

                return LyricGaps.WithInstrumentalGaps(lines);
            }
            catch
            {
                return new List<LyricLine>();
            }
        }

        private static LyricLine? LineFromParagraph(XmlElement p)
        {
            if (!TryParseSeconds(p.GetAttribute("begin"), out double lineBeginSec))
            {
                return null;
            }

            TryParseSeconds(p.GetAttribute("end"), out double lineEndSec);

            long lineStartMs = (long)(lineBeginSec * 1000.0);
            long lineEndMs = (long)(lineEndSec * 1000.0);

            var spans = p.GetElementsByTagName("span");
            var leadRuns = new List<TimedRun>();
            var bgRuns = new List<TimedRun>();

            for (int s = 0; s < spans.Count; s++)
            {
                if (spans[s] is not XmlElement span) continue;

                string role = span.GetAttribute("ttm:role");
                if (SkippedRoles.Contains(role)) continue;

                bool isBg = string.Equals(role, BackgroundRole, StringComparison.OrdinalIgnoreCase);

                if (TryParseSeconds(span.GetAttribute("begin"), out double sBegin) &&
                    TryParseSeconds(span.GetAttribute("end"), out double sEnd))
                {
                    long sStartMs = (long)(sBegin * 1000.0);
                    long sStopMs = (long)(sEnd * 1000.0);
                    string text = span.InnerText;

                    var run = new TimedRun(sStartMs, sStopMs, text);
                    if (isBg) bgRuns.Add(run);
                    else leadRuns.Add(run);
                }
            }

            var leadWords = SyllableRuns.WordsFromRuns(leadRuns);
            var bgWords = SyllableRuns.WordsFromRuns(bgRuns);

            string leadText = leadWords.Count > 0
                ? string.Join(" ", leadWords.Select(w => w.Text))
                : p.InnerText.Trim();

            if (string.IsNullOrWhiteSpace(leadText) && bgWords.Count == 0) return null;

            var line = new LyricLine
            {
                StartTime = TimeSpan.FromMilliseconds(lineStartMs),
                EndTime = TimeSpan.FromMilliseconds(lineEndMs > lineStartMs ? lineEndMs : (leadWords.Count > 0 ? (long)leadWords[^1].EndTime.TotalMilliseconds : lineStartMs + 3500)),
                Text = leadText,
                Words = leadWords
            };

            if (bgWords.Count > 0)
            {
                string bgText = string.Join(" ", bgWords.Select(w => w.Text));
                line.BackgroundLine = new LyricLine
                {
                    StartTime = bgWords[0].StartTime,
                    EndTime = bgWords[^1].EndTime,
                    Text = bgText,
                    Words = bgWords
                };
                line.BackgroundText = bgText;
            }

            return line;
        }

        private static bool TryParseSeconds(string s, out double sec)
        {
            sec = 0.0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimEnd('s', 'S');

            // Format mm:ss.xxx or hh:mm:ss.xxx
            if (s.Contains(':'))
            {
                string[] parts = s.Split(':');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double min) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double secVal))
                {
                    sec = min * 60.0 + secVal;
                    return true;
                }
                if (parts.Length == 3 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double hr) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double minVal) &&
                    double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double secVal2))
                {
                    sec = hr * 3600.0 + minVal * 60.0 + secVal2;
                    return true;
                }
            }

            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out sec);
        }
    }

    /// <summary>
    /// Karaoke LRC parser (QRC / NetEase YRC format), ported from BitChord (KaraokeLrc.kt).
    /// Format: [lineStart,lineDur](wStart,wDur)text...
    /// </summary>
    public static class KaraokeLrc
    {
        private static readonly Regex LineRegex = new Regex(@"^\[(\d{1,8}),(\d{1,8})\](.*)$", RegexOptions.Compiled);
        private static readonly Regex PrefixWordRegex = new Regex(@"\((\d{1,8}),(\d{1,8})(?:,\d{1,8})?\)([^()]*)", RegexOptions.Compiled);
        private static readonly Regex SuffixWordRegex = new Regex(@"([^()]*)\((\d{1,8}),(\d{1,8})(?:,\d{1,8})?\)", RegexOptions.Compiled);
        private static readonly Regex ContentRegex = new Regex(@"LyricContent\s*=\s*\""([^\""]*)\""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool LooksLike(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string content = ExtractLyricContent(raw);
            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var match = LineRegex.Match(line.Trim());
                if (match.Success)
                {
                    string body = match.Groups[3].Value;
                    if (PrefixWordRegex.IsMatch(body) || SuffixWordRegex.IsMatch(body)) return true;
                }
            }
            return false;
        }

        public static List<LyricLine> Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<LyricLine>();
            string content = ExtractLyricContent(raw);
            using var reader = new StringReader(content);

            var lines = new List<LyricLine>();
            string? row;

            while ((row = reader.ReadLine()) != null)
            {
                var match = LineRegex.Match(row.Trim());
                if (!match.Success) continue;

                if (!long.TryParse(match.Groups[1].Value, out long lineStart) ||
                    !long.TryParse(match.Groups[2].Value, out long lineDur)) continue;

                string body = match.Groups[3].Value;
                var prefixMatches = PrefixWordRegex.Matches(body);
                List<LyricWord> words;

                if (prefixMatches.Count > 0)
                {
                    var runs = prefixMatches.Select(m =>
                    {
                        long s = long.Parse(m.Groups[1].Value);
                        long d = long.Parse(m.Groups[2].Value);
                        return new TimedRun(s, s + d, m.Groups[3].Value);
                    }).ToList();
                    words = SyllableRuns.WordsFromRuns(runs);
                }
                else
                {
                    var suffixMatches = SuffixWordRegex.Matches(body);
                    var runs = suffixMatches.Select(m =>
                    {
                        long s = long.Parse(m.Groups[2].Value);
                        long d = long.Parse(m.Groups[3].Value);
                        return new TimedRun(s, s + d, m.Groups[1].Value);
                    }).ToList();
                    words = SyllableRuns.WordsFromRuns(runs);
                }

                string lineText = words.Count > 0 ? string.Join(" ", words.Select(w => w.Text)) : body.Trim();
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                lines.Add(new LyricLine
                {
                    StartTime = TimeSpan.FromMilliseconds(lineStart),
                    EndTime = TimeSpan.FromMilliseconds(lineStart + lineDur),
                    Text = lineText,
                    Words = words
                });
            }

            return LyricGaps.WithInstrumentalGaps(lines);
        }

        private static string ExtractLyricContent(string raw)
        {
            var match = ContentRegex.Match(raw);
            return match.Success ? match.Groups[1].Value : raw;
        }
    }

    /// <summary>
    /// Enhanced ("A2") LRC parser, ported from BitChord (EnhancedLrc.kt).
    /// Format: [mm:ss.xx]<mm:ss.xx>word <mm:ss.xx>word...
    /// </summary>
    public static class EnhancedLrc
    {
        private static readonly Regex LineRegex = new Regex(@"^\[(\d{1,3}):(\d{2})[.:](\d{2,3})\](.*)$", RegexOptions.Compiled);
        private static readonly Regex WordRegex = new Regex(@"<(\d{1,3}):(\d{2})[.:](\d{2,3})>([^<]*)", RegexOptions.Compiled);

        public static bool LooksLike(string lrc)
        {
            return !string.IsNullOrWhiteSpace(lrc) && WordRegex.IsMatch(lrc);
        }

        public static List<LyricLine> Parse(string lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc)) return new List<LyricLine>();

            var rows = new List<(long timeMs, List<Match> words, string plain)>();
            using var reader = new StringReader(lrc);
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                var match = LineRegex.Match(line.Trim());
                if (!match.Success) continue;

                long timeMs = Stamp(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                var wordMatches = WordRegex.Matches(match.Groups[4].Value).Cast<Match>().ToList();
                rows.Add((timeMs, wordMatches, match.Groups[4].Value.Trim()));
            }

            rows.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));
            if (rows.All(r => r.words.Count == 0)) return new List<LyricLine>();

            var result = new List<LyricLine>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.words.Count == 0)
                {
                    string plainText = WebUtility.HtmlDecode(row.plain).Trim();
                    if (!string.IsNullOrEmpty(plainText))
                    {
                        result.Add(new LyricLine
                        {
                            StartTime = TimeSpan.FromMilliseconds(row.timeMs),
                            EndTime = TimeSpan.FromMilliseconds(row.timeMs + 3500),
                            Text = plainText
                        });
                    }
                    continue;
                }

                long lineEnd = (i + 1 < rows.Count)
                    ? rows[i + 1].timeMs
                    : Stamp(row.words[^1]) + 1500;

                var runs = new List<TimedRun>(row.words.Count);
                for (int w = 0; w < row.words.Count; w++)
                {
                    var wm = row.words[w];
                    long wStart = Stamp(wm);
                    long wEnd = (w + 1 < row.words.Count) ? Stamp(row.words[w + 1]) : lineEnd;
                    string wordText = WebUtility.HtmlDecode(wm.Groups[4].Value);
                    runs.Add(new TimedRun(wStart, wEnd, wordText));
                }

                var words = SyllableRuns.WordsFromRuns(runs);
                if (words.Count == 0) continue;

                long finalStart = Math.Min(row.timeMs, (long)words[0].StartTime.TotalMilliseconds);
                result.Add(new LyricLine
                {
                    StartTime = TimeSpan.FromMilliseconds(finalStart),
                    EndTime = TimeSpan.FromMilliseconds(lineEnd),
                    Text = string.Join(" ", words.Select(w => w.Text)),
                    Words = words
                });
            }

            return LyricGaps.WithInstrumentalGaps(result);
        }

        private static long Stamp(Match m) => Stamp(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

        private static long Stamp(string min, string sec, string frac)
        {
            long fracMs = frac.Length == 3 ? long.Parse(frac) : long.Parse(frac) * 10;
            return long.Parse(min) * 60_000 + long.Parse(sec) * 1_000 + fracMs;
        }
    }

    /// <summary>
    /// Standard LRC parser ported from BitChord (LrcLib.kt).
    /// </summary>
    public static class LrcLibParser
    {
        private static readonly Regex StampRegex = new Regex(@"\[(\d{1,3}):(\d{2})[.:](\d{2,3})\]", RegexOptions.Compiled);
        private static readonly Regex WordStampRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        public static List<LyricLine> ParseLrc(string lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc)) return new List<LyricLine>();

            var all = new List<LyricLine>();
            using var reader = new StringReader(lrc);
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                var match = StampRegex.Match(line);
                if (!match.Success) continue;

                if (!long.TryParse(match.Groups[1].Value, out long min) ||
                    !long.TryParse(match.Groups[2].Value, out long sec)) continue;

                string fracStr = match.Groups[3].Value;
                long fracMs = fracStr.Length == 3 ? long.Parse(fracStr) : long.Parse(fracStr) * 10;
                long timeMs = min * 60_000 + sec * 1_000 + fracMs;

                string body = line.Substring(match.Index + match.Length);
                var alignment = LyricAlignment.Start;

                if (body.StartsWith("<R>", StringComparison.OrdinalIgnoreCase))
                {
                    alignment = LyricAlignment.End;
                    body = body.Substring(3);
                }

                string text = WordStampRegex.Replace(body, "").Trim();

                all.Add(new LyricLine
                {
                    StartTime = TimeSpan.FromMilliseconds(timeMs),
                    Text = text,
                    Alignment = alignment,
                    IsGap = string.IsNullOrEmpty(text)
                });
            }

            all.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            // Set line end times
            for (int i = 0; i < all.Count; i++)
            {
                if (i < all.Count - 1)
                {
                    all[i].EndTime = all[i + 1].StartTime;
                }
                else
                {
                    all[i].EndTime = all[i].StartTime + TimeSpan.FromSeconds(3.5);
                }
            }

            // Filter gaps shorter than MinGapMs
            var kept = all.Where((l, idx) =>
            {
                if (!l.IsGap) return true;
                if (idx >= all.Count - 1) return true;
                return (all[idx + 1].StartTime - l.StartTime).TotalMilliseconds >= LyricGaps.MinGapMs;
            }).ToList();

            if (kept.Count > 0 && !kept[0].IsGap && kept[0].StartTime.TotalMilliseconds >= LyricGaps.MinGapMs)
            {
                kept.Insert(0, new LyricLine
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = kept[0].StartTime,
                    Text = "",
                    IsGap = true
                });
            }

            for (int i = 0; i < kept.Count; i++)
            {
                kept[i].Index = i;
            }

            return kept;
        }
    }

    /// <summary>
    /// Universal lyric unwrap & routing parser ported from BitChord (ProviderLyrics.kt).
    /// </summary>
    public static class ProviderLyrics
    {
        private static readonly string[] ContentKeys = new[]
        {
            "ttml", "ttmlContent", "lyrics", "lrc", "content", "text",
            "plainLyrics", "syncedLyrics", "line", "lines", "lyric",
            "data", "result", "response"
        };

        public static List<LyricLine>? Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string? unwrapped = Unwrap(raw);
            if (string.IsNullOrWhiteSpace(unwrapped)) return null;

            string content = UnescapeTtml(unwrapped);

            List<LyricLine> lines;
            if (content.Contains("<tt", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("http://www.w3.org/ns/ttml", StringComparison.OrdinalIgnoreCase))
            {
                lines = TtmlLyrics.Parse(content);
            }
            else if (KaraokeLrc.LooksLike(content))
            {
                lines = KaraokeLrc.Parse(content);
            }
            else if (EnhancedLrc.LooksLike(content))
            {
                lines = EnhancedLrc.Parse(content);
            }
            else
            {
                lines = LrcLibParser.ParseLrc(content);
                if (lines.Count == 0 && !content.TrimStart().StartsWith("<"))
                {
                    lines = ParsePlain(content);
                }
            }

            return lines != null && lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)) ? lines : null;
        }

        private static List<LyricLine> ParsePlain(string content)
        {
            var lines = new List<LyricLine>();
            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) continue;
                lines.Add(new LyricLine { Text = line });
            }
            return lines;
        }

        public static string? Unwrap(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = raw.Replace("\uFEFF", "").Trim();

            if (value.StartsWith("```"))
            {
                var lines = value.Split('\n').Select(l => l.Trim()).ToList();
                if (lines.Count > 1)
                {
                    if (lines[^1] == "```") lines.RemoveAt(lines.Count - 1);
                    lines.RemoveAt(0);
                    value = string.Join("\n", lines).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                using var doc = JsonDocument.Parse(value);
                return Extract(doc.RootElement)?.Trim();
            }
            catch
            {
                return value;
            }
        }

        private static string? Extract(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    string str = element.GetString()?.Trim() ?? string.Empty;
                    try
                    {
                        using var nested = JsonDocument.Parse(str);
                        if (nested.RootElement.ValueKind != JsonValueKind.String)
                        {
                            return Extract(nested.RootElement);
                        }
                    }
                    catch { }
                    return str;

                case JsonValueKind.Array:
                    var items = new List<string>();
                    foreach (var item in element.EnumerateArray())
                    {
                        var extracted = Extract(item);
                        if (!string.IsNullOrWhiteSpace(extracted)) items.Add(extracted);
                    }
                    return items.Count > 0 ? string.Join("\n", items) : null;

                case JsonValueKind.Object:
                    if (element.TryGetProperty("isError", out var isErr) && isErr.GetBoolean()) return null;
                    if (element.TryGetProperty("ok", out var ok) && !ok.GetBoolean()) return null;

                    foreach (var key in ContentKeys)
                    {
                        if (element.TryGetProperty(key, out var prop))
                        {
                            var res = Extract(prop);
                            if (!string.IsNullOrWhiteSpace(res)) return res;
                        }
                    }

                    if (element.TryGetProperty("metadata", out var meta))
                    {
                        var res = Extract(meta);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }

                    if (element.TryGetProperty("words", out var wordsProp))
                    {
                        var res = Extract(wordsProp);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }
                    break;
            }

            return null;
        }

        private static string UnescapeTtml(string value)
        {
            if (value.Contains("&lt;tt", StringComparison.OrdinalIgnoreCase))
            {
                return value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                            .Replace("&#39;", "'").Replace("&apos;", "'").Replace("&amp;", "&");
            }
            return value;
        }
    }
}
