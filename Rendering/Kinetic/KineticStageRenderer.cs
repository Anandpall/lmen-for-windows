using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LmenV2.Models;
using LmenV2.Services;

namespace LmenV2.Rendering.Kinetic
{
    /// <summary>
    /// Cotodama / LyricalStudio kinetic typography stage engine for WPF.
    /// Implements Stack, Slide, Zoom, Letters, Scatter, Graph, Flow, and Auto Style.
    /// </summary>
    public sealed class KineticStageRenderer
    {
        private readonly Canvas _hostCanvas;
        private readonly Canvas _cameraCanvas;
        private readonly Canvas _rippleCanvas;
        private readonly Canvas _titleCardCanvas;

        private TrackLyrics? _lyrics;
        private string _songTitle = "Ready to Play";
        private string _artistName = "Play music to begin";
        private string _genre = "";
        private uint _songSeed = 1337;

        private KineticTemplate _activeTemplateChoice = KineticTemplate.Auto;
        private KineticPalette _palette = KineticPalette.CreateFallback(1337);

        private int _currentLineIndex = -2;
        private int _revealedWords = 0;
        private double _lineElapsed = 0.0;
        private double _ambientTime = 0.0;

        private readonly TransformGroup _cameraTransformGroup;
        private readonly RotateTransform _cameraRotateTransform;
        private readonly TranslateTransform _cameraTranslateTransform;
        private readonly ScaleTransform _cameraScaleTransform;

        // TitleCard UI elements
        private readonly StackPanel _titleCardPanel;
        private readonly TextBlock _titleCardTitle;
        private readonly TextBlock _titleCardArtist;
        private readonly Ellipse _titleCardDot;
        private readonly TranslateTransform _titleCardTranslate = new TranslateTransform(0, 0);
        private double _lastTitleW = -1;
        private double _lastTitleH = -1;
        private bool _titleNeedsMeasure = true;

        // Static frozen high-performance drop shadow for kinetic words
        private static readonly DropShadowEffect DefaultWordShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 8.0,
            ShadowDepth = 2.0,
            Opacity = 0.55,
            RenderingBias = RenderingBias.Performance
        };

        static KineticStageRenderer()
        {
            DefaultWordShadow.Freeze();
        }

        // Active line nodes cache
        private readonly List<KineticWordElement> _activeWordElements = new List<KineticWordElement>();
        private readonly List<Path> _activeGraphPaths = new List<Path>();

        // Word ripples
        private struct Ripple
        {
            public double X;
            public double Y;
            public double Age;
            public double MaxAge;
            public double MaxRadius;
            public Color Color;
        }
        private readonly List<Ripple> _ripples = new List<Ripple>();
        private readonly List<Ellipse> _ripplePool = new List<Ellipse>();
        private readonly List<(ScaleTransform Scale, TranslateTransform Translate)> _rippleTransforms = new List<(ScaleTransform, TranslateTransform)>();

        private KineticScene? _currentScene;
        private FrameworkElement? _layoutRoot;

        // Fisheye convex lens + vertical traversal
        private TranslateTransform? _fisheyeRiseTransform;
        private ScaleTransform? _fisheyeLensScale;
        private ScaleTransform? _stickerFitScale;
        private double _stickerLensXCap = 1.38;
        private bool _fisheyeRiseActive;
        private TimeSpan _fisheyeLineStart;
        private TimeSpan _fisheyeLineEnd;
        private double _fisheyeRiseOffsetY;

        public KineticTemplate CurrentTemplateChoice => _activeTemplateChoice;

        public KineticStageRenderer(Canvas hostCanvas)
        {
            _hostCanvas = hostCanvas ?? throw new ArgumentNullException(nameof(hostCanvas));
            _hostCanvas.ClipToBounds = true;
            _hostCanvas.Background = Brushes.Transparent;

            // Ripple layer in background
            _rippleCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _hostCanvas.Children.Add(_rippleCanvas);

            // Camera canvas containing lyric lines
            _cameraCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _cameraRotateTransform = new RotateTransform(0);
            _cameraTranslateTransform = new TranslateTransform(0, 0);
            _cameraScaleTransform = new ScaleTransform(1, 1);
            _cameraTransformGroup = new TransformGroup();
            _cameraTransformGroup.Children.Add(_cameraScaleTransform);
            _cameraTransformGroup.Children.Add(_cameraRotateTransform);
            _cameraTransformGroup.Children.Add(_cameraTranslateTransform);
            _cameraCanvas.RenderTransform = _cameraTransformGroup;
            _hostCanvas.Children.Add(_cameraCanvas);

            // Title card layer
            _titleCardCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _hostCanvas.Children.Add(_titleCardCanvas);

            // Create TitleCard UI
            _titleCardPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _titleCardTranslate,
                Opacity = 1.0
            };

            _titleCardTitle = new TextBlock
            {
                Text = "READY TO PLAY",
                FontFamily = new FontFamily("Segoe UI, Inter, Arial Black"),
                FontWeight = FontWeights.Black,
                FontSize = 54,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            var titleShadow = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 4,
                Opacity = 0.6,
                RenderingBias = RenderingBias.Performance
            };
            titleShadow.Freeze();
            _titleCardTitle.Effect = titleShadow;

            var artistRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _titleCardDot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(255, 55, 95)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleCardDot.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(255, 55, 95),
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.8,
                RenderingBias = RenderingBias.Performance
            };

            _titleCardArtist = new TextBlock
            {
                Text = "Play music to begin",
                FontFamily = new FontFamily("Segoe UI, Inter"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };

            artistRow.Children.Add(_titleCardDot);
            artistRow.Children.Add(_titleCardArtist);

            _titleCardPanel.Children.Add(_titleCardTitle);
            _titleCardPanel.Children.Add(artistRow);
            _titleCardCanvas.Children.Add(_titleCardPanel);

            // Pre-allocate ripples pool with transform groups to eliminate layout invalidation
            for (int i = 0; i < 12; i++)
            {
                var st = new ScaleTransform(1, 1);
                var tt = new TranslateTransform(0, 0);
                var tg = new TransformGroup();
                tg.Children.Add(st);
                tg.Children.Add(tt);

                var el = new Ellipse
                {
                    Width = 100,
                    Height = 100,
                    StrokeThickness = 1.5,
                    Stroke = new SolidColorBrush(Colors.White),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                    RenderTransform = tg,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                _rippleTransforms.Add((st, tt));
                _ripplePool.Add(el);
                _rippleCanvas.Children.Add(el);
            }
        }

        public void SetTemplateChoice(KineticTemplate template)
        {
            _activeTemplateChoice = template;
            _currentLineIndex = -2; // Force rebuild
        }

        public void CycleTemplate()
        {
            var values = (KineticTemplate[])Enum.GetValues(typeof(KineticTemplate));
            int next = ((int)_activeTemplateChoice + 1) % values.Length;
            SetTemplateChoice(values[next]);
        }

        public string GetCurrentTemplateDisplayName()
        {
            return _activeTemplateChoice switch
            {
                KineticTemplate.Auto => "Auto Style",
                KineticTemplate.Stack => "Stack",
                KineticTemplate.Slide => "Slide",
                KineticTemplate.Zoom => "Zoom",
                KineticTemplate.Letters => "Letters",
                KineticTemplate.Scatter => "Scatter",
                KineticTemplate.Graph => "Graph",
                KineticTemplate.Flow => "Flow",
                KineticTemplate.Sticker => "Sticker",
                KineticTemplate.Fisheye => "Fisheye Lens",
                _ => "Auto Style"
            };
        }

        public void SetLyrics(TrackLyrics? lyrics, string title, string artist, Color dominant, Color accent, string genre = "")
        {
            _lyrics = lyrics;
            _songTitle = string.IsNullOrWhiteSpace(title) ? "Ready to Play" : title;
            _artistName = string.IsNullOrWhiteSpace(artist) ? "Play music to begin" : artist;
            _genre = genre;
            _songSeed = KineticLayout.HashString(_songTitle + " " + _artistName);
            _palette = KineticPalette.FromDominantAndAccent(dominant, accent, (int)_songSeed);

            _titleCardTitle.Text = _songTitle.ToUpperInvariant();
            _titleCardArtist.Text = _artistName;
            _titleNeedsMeasure = true;
            _titleCardDot.Fill = new SolidColorBrush(_palette.Accents[0]);
            if (_titleCardDot.Effect is DropShadowEffect dse)
            {
                dse.Color = _palette.Accents[0];
            }

            _currentLineIndex = -2;
            _revealedWords = 0;
            _activeWordElements.Clear();
            _cameraCanvas.Children.Clear();
            _activeGraphPaths.Clear();
            _layoutRoot = null;
            DetachFisheyeLens();
        }

        public void UpdateFrame(TimeSpan position, double dt)
        {
            try
            {
                double w = _hostCanvas.ActualWidth;
                double h = _hostCanvas.ActualHeight;
                if (w < 40 || h < 40) return;

                if (_cameraCanvas.Width != w) _cameraCanvas.Width = w;
                if (_cameraCanvas.Height != h) _cameraCanvas.Height = h;
                if (!_cameraCanvas.ClipToBounds) _cameraCanvas.ClipToBounds = true;

                _ambientTime += dt;

                // Ambient harmonic floating camera drift
                double driftY = Math.Sin(_ambientTime * 0.9) * 4.0;
                double driftX = Math.Cos(_ambientTime * 0.7) * 3.0;

                if (_lyrics == null || !_lyrics.HasLyrics || _lyrics.Lines == null || _lyrics.Lines.Count == 0)
                {
                    ShowTitleCard(true, w, h, driftX, driftY);
                    UpdateRipples(dt);
                    return;
                }

                // Find line at current position. The moment a line reaches its end it
                // stops being rendered: holding it through a pause left the last word
                // parked at the top of the stage (its rise animation was already at the
                // clamp) for seconds while no new lyric arrived.
                int activeLineIdx = -1;
                for (int i = 0; i < _lyrics.Lines.Count; i++)
                {
                    var l = _lyrics.Lines[i];
                    if (l.IsGap) continue;

                    if (position >= l.StartTime && position < l.EndTime)
                    {
                        activeLineIdx = i;
                        break;
                    }
                }

                if (activeLineIdx == -1)
                {
                    if (ShowTitleCardForPause(position))
                    {
                        ShowTitleCard(true, w, h, driftX, driftY);
                    }
                    else
                    {
                        HideStage();
                    }
                    UpdateRipples(dt);
                    return;
                }

                // If we have an active line, hide title card and render kinetic line
                ShowTitleCard(false, w, h, driftX, driftY);

                if (activeLineIdx >= 0 && activeLineIdx < _lyrics.Lines.Count)
                {
                    var line = _lyrics.Lines[activeLineIdx];

                    // If new line entered, rebuild layout
                    if (activeLineIdx != _currentLineIndex)
                    {
                        _currentLineIndex = activeLineIdx;
                        _revealedWords = 0;
                        _lineElapsed = 0.0;
                        BuildKineticLine(line, w, h);
                    }

                    _lineElapsed += dt;

                    // Determine how many words are revealed
                    int revealed = 0;
                    if (line.Words != null && line.Words.Count > 0)
                    {
                        for (int i = 0; i < line.Words.Count; i++)
                        {
                            if (position >= line.Words[i].StartTime) revealed = i + 1;
                        }
                        if (revealed == 0 && position >= line.StartTime) revealed = 1;
                    }
                    else
                    {
                        // Line without word sync: smoothly reveal words across line duration
                        double lineDur = Math.Max(0.5, (line.EndTime - line.StartTime).TotalSeconds);
                        double progress = Math.Clamp((position - line.StartTime).TotalSeconds / lineDur, 0.0, 1.0);
                        int totalWords = Math.Max(1, _activeWordElements.Count);
                        revealed = Math.Min(totalWords, (int)Math.Floor(progress * totalWords) + 1);
                    }

                    // Check for new word arrival to emit ripple
                    if (revealed > _revealedWords && _revealedWords < _activeWordElements.Count)
                    {
                        for (int i = _revealedWords; i < revealed && i < _activeWordElements.Count; i++)
                        {
                            var elem = _activeWordElements[i];
                            if (elem.CenterX > 1.0 || elem.CenterY > 1.0)
                            {
                                EmitWordRipple(elem.CenterX, elem.CenterY, elem.IsHighlighted ? elem.HighlightColor : _palette.Accents[0]);
                            }
                        }
                        _revealedWords = revealed;
                    }

                    // Rise the fisheye scene from below the stage up through the lens
                    if (_fisheyeRiseActive)
                    {
                        UpdateFisheyeRise(position, w, h);
                    }

                    // Update word springs and layout animations
                    UpdateWordAnimations(revealed, dt, w, h, driftX, driftY);
                }

                UpdateRipples(dt);
            }
            catch (Exception ex)
            {
                App.Log($"[KineticStageRenderer] Error in UpdateFrame: {ex}");
            }
        }

        /// <summary>
        /// Decides what fills a no-lyric moment: the full "now playing" card for the
        /// intro, the outro, explicit instrumental breaks and long silences, or a
        /// clean stage for the brief pauses between consecutive lyric lines.
        /// </summary>
        private bool ShowTitleCardForPause(TimeSpan position)
        {
            var lines = _lyrics?.Lines;
            if (lines == null || lines.Count == 0) return true;

            if (position < lines[0].StartTime) return true;

            var last = lines[^1];
            if (position >= last.EndTime) return true;

            foreach (var line in lines)
            {
                if (line.IsGap && position >= line.StartTime && position < line.EndTime) return true;
            }

            for (int i = 0; i < lines.Count - 1; i++)
            {
                var line = lines[i];
                if (line.IsGap) continue;
                var next = lines[i + 1];
                if (position >= line.EndTime && position < next.StartTime)
                {
                    return (next.StartTime - line.EndTime).TotalSeconds > 2.8;
                }
            }

            return false;
        }

        private void HideStage()
        {
            if (_cameraCanvas.Visibility != Visibility.Collapsed)
            {
                _cameraCanvas.Visibility = Visibility.Collapsed;
            }

            if (_titleCardPanel.Visibility != Visibility.Collapsed)
            {
                _titleCardPanel.Opacity = 0.0;
                _titleCardPanel.Visibility = Visibility.Collapsed;
            }

            // The next line must rebuild from scratch rather than replay the finished
            // scene's cached (already-risen) layout.
            _currentLineIndex = -2;
        }

        private void ShowTitleCard(bool show, double w, double h, double driftX, double driftY)
        {
            if (show)
            {
                if (_titleCardPanel.Visibility != Visibility.Visible)
                {
                    _titleCardPanel.Visibility = Visibility.Visible;
                }
                _titleCardPanel.Opacity = Math.Min(1.0, _titleCardPanel.Opacity + 0.08);

                if (Math.Abs(w - _lastTitleW) > 1.0 || Math.Abs(h - _lastTitleH) > 1.0 || _titleNeedsMeasure)
                {
                    _titleCardPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double panelW = _titleCardPanel.DesiredSize.Width;
                    double panelH = _titleCardPanel.DesiredSize.Height;
                    Canvas.SetLeft(_titleCardPanel, (w - panelW) / 2.0);
                    Canvas.SetTop(_titleCardPanel, (h - panelH) / 2.0);
                    _lastTitleW = w;
                    _lastTitleH = h;
                    _titleNeedsMeasure = false;
                }

                _titleCardTranslate.X = driftX;
                _titleCardTranslate.Y = driftY;

                if (_cameraCanvas.Visibility != Visibility.Collapsed)
                {
                    _cameraCanvas.Visibility = Visibility.Collapsed;
                }

                // Title card covers the stage: drop the fisheye lens so it is not
                // left bound to a hidden canvas when the next line is built.
                DetachFisheyeLens();
            }
            else
            {
                if (_titleCardPanel.Opacity > 0.01)
                {
                    _titleCardPanel.Opacity = Math.Max(0.0, _titleCardPanel.Opacity - 0.12);
                    if (_titleCardPanel.Opacity <= 0.01)
                    {
                        _titleCardPanel.Visibility = Visibility.Collapsed;
                    }
                }
                if (_cameraCanvas.Visibility != Visibility.Visible)
                {
                    _cameraCanvas.Visibility = Visibility.Visible;
                }
            }
        }

        private void BuildKineticLine(LyricLine line, double stageW, double stageH)
        {
            _activeWordElements.Clear();
            _cameraCanvas.Children.Clear();
            _activeGraphPaths.Clear();
            _layoutRoot = null;
            DetachFisheyeLens();

            _currentScene = KineticLayout.ChooseScene(line, _songSeed, _genre, _activeTemplateChoice);
            var template = _currentScene.Template;

            // Camera tilt
            _cameraRotateTransform.Angle = _currentScene.TiltDeg;
            _cameraRotateTransform.CenterX = stageW / 2.0;
            _cameraRotateTransform.CenterY = stageH / 2.0;

            uint lineSeed = _songSeed ^ KineticLayout.HashString(line.Text);
            var highlights = KineticLayout.GetHighlightIndices(line, lineSeed);

            // Fallback word list if line has no words
            var words = (line.Words != null && line.Words.Count > 0)
                ? line.Words
                : CreateFallbackWords(line.Text);

            if (words == null || words.Count == 0)
            {
                ShowTitleCard(true, stageW, stageH, 0, 0);
                return;
            }

            switch (template)
            {
                case KineticTemplate.Stack:
                    BuildStackLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Slide:
                    BuildSlideLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Zoom:
                    BuildZoomLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Letters:
                    BuildLettersLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Scatter:
                    BuildScatterLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Graph:
                    BuildGraphLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Sticker:
                    BuildStickerLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Fisheye:
                    BuildFisheyeLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
                case KineticTemplate.Flow:
                default:
                    BuildFlowLayout(words, line, highlights, lineSeed, stageW, stageH);
                    break;
            }

            // Apply FitBox scaling so content never overflows stage bounds
            _layoutRoot = _cameraCanvas.Children.Count > 0
                ? _cameraCanvas.Children[^1] as FrameworkElement
                : null;
            ApplyFitBox(stageW, stageH);
        }

        private List<LyricWord> CreateFallbackWords(string text)
        {
            var list = new List<LyricWord>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                list.Add(new LyricWord { Text = p });
            }
            return list;
        }

        #region Template Builders

        private void BuildStackLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed);
            bool alignLeft = rng() < 0.5;

            int maxRows = Math.Min(4, Math.Max(2, (int)Math.Ceiling(words.Count / 2.0)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.86, stageH * 0.65, 16.0, 72.0);

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = alignLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = alignLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.08)
                };

                foreach (var w in rows[r])
                {
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);
                    Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                    var elem = CreateWordElement(w.Text, baseFontSize, col, isHigh, KineticTemplate.Stack);
                    // Entrance: slide up from +0.7em with scale
                    elem.TranslateYSpring = KineticSpring.Create(baseFontSize * 0.70, 150.0, 24.5);
                    elem.ScaleYSpring = KineticSpring.Create(1.30, 150.0, 24.5);
                    elem.ScaleXSpring = KineticSpring.Create(0.90, 150.0, 24.5);
                    elem.OpacitySpring = KineticSpring.Create(0.0, 160.0, 25.0);

                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(elem.Container);
                }

                rootStack.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(rootStack);
        }

        private void BuildSlideLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed + 5);
            int maxRows = Math.Min(3, Math.Max(1, (int)Math.Ceiling(words.Count / 2.0)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.88, stageH * 0.68, 18.0, 80.0);
            bool reverse = rng() < 0.5;

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                bool fromLeft = (r % 2 == 0) != reverse;
                double indent = (reverse ? rows.Count - 1 - r : r) * (stageW * 0.035);

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(indent, 0, 0, baseFontSize * 0.06)
                };

                foreach (var w in rows[r])
                {
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);
                    Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                    var elem = CreateWordElement(w.Text, baseFontSize, col, isHigh, KineticTemplate.Slide);
                    // Slide horizontally from edge with skew
                    elem.TranslateXSpring = KineticSpring.Create(fromLeft ? -stageW * 0.15 : stageW * 0.25, 120.0, 22.0);
                    elem.SkewX = fromLeft ? -10.0 : 16.0;
                    elem.ScaleXSpring = KineticSpring.Create(fromLeft ? 1.20 : 1.45, 120.0, 22.0);
                    elem.ScaleYSpring = KineticSpring.Create(0.85, 120.0, 22.0);
                    elem.OpacitySpring = KineticSpring.Create(0.0, 150.0, 24.0);

                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(elem.Container);
                }

                rootStack.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(rootStack);
        }

        private void BuildZoomLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            int maxRows = Math.Min(4, Math.Max(1, (int)Math.Ceiling(words.Count / 2.0)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.88, stageH * 0.68, 18.0, 76.0);

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.08)
                };

                foreach (var w in rows[r])
                {
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);
                    Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                    var elem = CreateWordElement(w.Text, baseFontSize, col, isHigh, KineticTemplate.Zoom);
                    // Zoom entrance: flies in from huge scale (2.6x)
                    elem.ScaleXSpring = KineticSpring.Create(2.6, 90.0, 18.0);
                    elem.ScaleYSpring = KineticSpring.Create(1.95, 90.0, 18.0);
                    elem.SkewX = -10.0;
                    elem.OpacitySpring = KineticSpring.Create(0.0, 120.0, 20.0);

                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(elem.Container);
                }

                rootStack.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(rootStack);
        }

        private void BuildLettersLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed + 9);
            bool fromTop = rng() < 0.5;

            int maxRows = Math.Min(4, Math.Max(1, (int)Math.Ceiling(words.Count / 2.0)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.88, stageH * 0.68, 16.0, 72.0);

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.08)
                };

                foreach (var w in rows[r])
                {
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);
                    Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                    var elem = CreateLettersElement(w.Text, baseFontSize, col, isHigh, fromTop);
                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(elem.Container);
                }

                rootStack.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(rootStack);
        }

        private void BuildScatterLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed);
            double totalLen = 0;
            foreach (var w in words) totalLen += KineticLayout.VisualLength(w.Text);

            double baseFontSize = Math.Clamp(stageW * 0.80 / Math.Max(10.0, totalLen / 2.6), 22.0, 58.0);

            var wrapPanel = new WrapPanel
            {
                MaxWidth = stageW * 0.86,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                bool isHigh = highlights.Contains(i);
                Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                double scale = 0.70 + rng() * 0.55;
                double fontSize = baseFontSize * scale;
                double offsetY = (rng() - 0.5) * baseFontSize * 0.40;
                double fromX = (rng() - 0.5) * stageW * 0.20;
                double fromY = (rng() - 0.5) * stageH * 0.15;

                var elem = CreateWordElement(w.Text, fontSize, col, isHigh, KineticTemplate.Scatter);
                elem.TranslateXSpring = KineticSpring.Create(fromX, 140.0, 23.0);
                elem.TranslateYSpring = KineticSpring.Create(fromY + offsetY, 140.0, 23.0);
                elem.ScaleXSpring = KineticSpring.Create(0.65, 140.0, 23.0);
                elem.ScaleYSpring = KineticSpring.Create(0.65, 140.0, 23.0);
                elem.OpacitySpring = KineticSpring.Create(0.0, 160.0, 24.0);
                elem.Container.Margin = new Thickness(baseFontSize * 0.18, baseFontSize * 0.10, baseFontSize * 0.18, baseFontSize * 0.10);

                _activeWordElements.Add(elem);
                wrapPanel.Children.Add(elem.Container);
            }

            _cameraCanvas.Children.Add(wrapPanel);
        }

        private void BuildGraphLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed ^ 0x2545F491u);
            double trunkFont = Math.Clamp(Math.Min(stageH * 0.13, stageW * 0.075), 24.0, 54.0);
            double leafFont = trunkFont * 0.46;

            var graphCanvas = new Canvas
            {
                Width = stageW * 0.88,
                Height = stageH * 0.68,
                ClipToBounds = false
            };

            double curX = stageW * 0.05;
            double curY = stageH * 0.28;
            double waveAmp = trunkFont * 0.75;

            var trunkPoints = new List<Point>();

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                bool isHigh = highlights.Contains(i);
                Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                string clean = w.Text.Trim().ToLowerInvariant();
                bool isLeaf = (clean.Length <= 2 || clean == "the" || clean == "to" || clean == "of" || clean == "in" || clean == "on" || clean == "my");

                double fontSize = isLeaf ? leafFont : trunkFont;
                var elem = CreateWordElement(w.Text, fontSize, col, isHigh, KineticTemplate.Graph);

                double wordW = KineticLayout.VisualLength(w.Text) * fontSize;
                double targetX = curX;
                double targetY = curY + (isLeaf ? trunkFont * 0.85 : Math.Sin(i * 1.2 + seed) * waveAmp);

                Canvas.SetLeft(elem.Container, targetX);
                Canvas.SetTop(elem.Container, targetY);

                elem.ScaleXSpring = KineticSpring.Create(0.4, 150.0, 24.0);
                elem.ScaleYSpring = KineticSpring.Create(0.4, 150.0, 24.0);
                elem.OpacitySpring = KineticSpring.Create(0.0, 160.0, 24.0);

                if (!isLeaf)
                {
                    trunkPoints.Add(new Point(targetX + wordW / 2.0, targetY + fontSize / 2.0));
                }

                _activeWordElements.Add(elem);
                graphCanvas.Children.Add(elem.Container);

                curX += wordW + trunkFont * 0.45;
                if (curX > stageW * 0.75 && i < words.Count - 2)
                {
                    curX = stageW * 0.05;
                    curY += trunkFont * 2.2;
                }
            }

            // Draw smooth bezier vine connecting trunk words
            if (trunkPoints.Count > 1)
            {
                var pathGeom = new PathGeometry();
                var figure = new PathFigure { StartPoint = trunkPoints[0], IsClosed = false };

                for (int i = 1; i < trunkPoints.Count; i++)
                {
                    var p0 = trunkPoints[i - 1];
                    var p1 = trunkPoints[i];
                    double midX = (p0.X + p1.X) / 2.0;
                    figure.Segments.Add(new BezierSegment(
                        new Point(midX, p0.Y),
                        new Point(midX, p1.Y),
                        p1,
                        true
                    ));
                }

                pathGeom.Figures.Add(figure);

                var vinePath = new Path
                {
                    Data = pathGeom,
                    Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    StrokeThickness = 2.0,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };

                _activeGraphPaths.Add(vinePath);
                graphCanvas.Children.Insert(0, vinePath);
            }

            _cameraCanvas.Children.Add(graphCanvas);
        }

        private void BuildFlowLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            int maxRows = Math.Min(4, Math.Max(1, (int)Math.Ceiling(words.Count / 2.5)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.86, stageH * 0.66, 18.0, 64.0);

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.08)
                };

                foreach (var w in rows[r])
                {
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);
                    Color col = isHigh ? _palette.Accents[(seed >> 3) % 3] : Colors.White;

                    var elem = CreateWordElement(w.Text, baseFontSize, col, isHigh, KineticTemplate.Flow);
                    // Flow layout: all words visible at 0.32 opacity, lights up to 1.0 when sung
                    elem.Container.Opacity = 0.32;
                    elem.TargetOpacity = 0.32;
                    elem.ScaleXSpring = KineticSpring.Create(1.0, 200.0, 26.0);
                    elem.ScaleYSpring = KineticSpring.Create(1.0, 200.0, 26.0);

                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(elem.Container);
                }

                rootStack.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(rootStack);
        }

        /// <summary>
        /// Lyric-video "sticker" style: die-cut vinyl sticker badges with crisp outlines,
        /// jaunty angles, glossy highlight type, hand-drawn doodles, and punchy pop springs.
        /// </summary>
        private void BuildStickerLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed ^ 0x9E3779B9u);

            // Poster-like stack: one word per row until the line gets long, then
            // balance into at most four rows so the cut-outs stay oversized.
            int maxRows = Math.Min(4, Math.Max(1, words.Count));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.84, stageH * 0.66, 26.0, 132.0);

            Color pink = _palette.Accents[0];
            Color red = _palette.Accents[1];

            var root = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(baseFontSize * 0.04, 0, baseFontSize * 0.04, baseFontSize * 0.03)
                };

                for (int k = 0; k < rows[r].Count; k++)
                {
                    var w = rows[r][k];
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);

                    double kind = rng();
                    double rotation = (rng() - 0.5) * 8.0;
                    Color stickerTint = rng() < 0.58 ? pink : red;

                    KineticWordElement elem;
                    if (kind < 0.42)
                    {
                        elem = CreateStickerGlossElement(w.Text, baseFontSize * (0.96 + rng() * 0.20), isHigh ? pink : red, isHigh);
                    }
                    else
                    {
                        elem = CreateStickerBlockElement(w.Text, baseFontSize, stickerTint, Colors.White, isHigh);
                    }

                    elem.BaseRotation = rotation;
                    elem.LensBaseOffsetY = (r - (rows.Count - 1) * 0.5) * baseFontSize * 1.35;
                    elem.WobblePhase = rng() * Math.PI * 2.0;
                    elem.RotateTransform.Angle = rotation;
                    elem.RiseOffset = 0;
                    elem.ScaleXSpring = KineticSpring.Create(1.0, 190.0, 22.0);
                    elem.ScaleYSpring = KineticSpring.Create(1.0, 190.0, 22.0);
                    elem.OpacitySpring = KineticSpring.Create(0.42, 180.0, 22.0);

                    _activeWordElements.Add(elem);

                    if (k == 0 && rng() < 0.35)
                    {
                        rowPanel.Children.Add(CreateStickerDoodle((int)(rng() * 4.0), baseFontSize * 1.0, pink, rng));
                    }

                    rowPanel.Children.Add(elem.Container);

                    if (k == rows[r].Count - 1 && rng() < 0.35)
                    {
                        rowPanel.Children.Add(CreateStickerDoodle((int)(rng() * 4.0), baseFontSize * 0.9, red, rng));
                    }
                }

                root.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(root);

            // Measure the assembled stack once so oversized lines can be fitted to
            // the stage instead of spilling past its left/right edges.
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double layoutW = Math.Max(1.0, root.DesiredSize.Width);
            double layoutH = Math.Max(1.0, root.DesiredSize.Height);
            double fit = Math.Min(
                1.0,
                Math.Min(stageW * 0.92 / layoutW, stageH * 0.92 / layoutH));
            _stickerFitScale = new ScaleTransform(fit, fit);

            // The lens swells the stack while it crosses the middle; cap the
            // horizontal swell so no word can be pushed outside the stage.
            double fittedW = layoutW * fit;
            _stickerLensXCap = Math.Clamp(stageW * 0.97 / (fittedW * 1.16), 1.0, 1.38);

            // Sticker lyrics ride up through the fisheye lens: they enter from below
            // the stage, swell as they cross the lens center, then fade out the top.
            _fisheyeLensScale = new ScaleTransform(1, 1);
            _fisheyeRiseTransform = new TranslateTransform(0, stageH * 0.62);
            _fisheyeRiseOffsetY = stageH * 0.62;
            var lensGroup = new TransformGroup();
            lensGroup.Children.Add(_stickerFitScale);
            lensGroup.Children.Add(_fisheyeLensScale);
            lensGroup.Children.Add(_fisheyeRiseTransform);
            root.RenderTransform = lensGroup;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            _fisheyeRiseActive = true;
            _fisheyeLineStart = line.StartTime;
            _fisheyeLineEnd = line.EndTime;
            AttachFisheyeLens(stageW, stageH);
        }

        private void AttachFisheyeLens(double stageW, double stageH)
        {
            _cameraCanvas.Width = stageW;
            _cameraCanvas.Height = stageH;
            _cameraCanvas.ClipToBounds = true;

            // The lens is simulated with per-word scale/offset in UpdateWordAnimations.
            // A full-stage ShaderEffect is avoided on purpose: WPF re-rasterises effect
            // input at a lower resolution, which turns large sticker type into a blurry
            // smear (and renders nothing at all on some GPUs).
            if (_cameraCanvas.Effect != null)
            {
                _cameraCanvas.Effect = null;
            }
        }

        /// <summary>
        /// Verci-inspired Fisheye Lens style: real barrel-lens pixel shader warping lyrics
        /// and paired emojis as they glide vertically through the center of the lens.
        /// </summary>
        private void BuildFisheyeLayout(List<LyricWord> words, LyricLine line, HashSet<int> highlights, uint seed, double stageW, double stageH)
        {
            var rng = KineticLayout.CreateRng(seed);

            int maxRows = Math.Min(3, Math.Max(1, (int)Math.Ceiling(words.Count / 3.0)));
            var rows = KineticLayout.GroupRows(words, maxRows);

            double baseFontSize = FitFontToRows(rows, stageW * 0.76, stageH * 0.52, 28.0, 96.0);

            var root = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Color accentCol = _palette.Accents[0];

            int wordIdx = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.12)
                };

                for (int k = 0; k < rows[r].Count; k++)
                {
                    var w = rows[r][k];
                    int i = wordIdx++;
                    bool isHigh = highlights.Contains(i);

                    var scale = new ScaleTransform(1, 1);
                    var translate = new TranslateTransform(0, 0);
                    var tg = new TransformGroup();
                    tg.Children.Add(scale);
                    tg.Children.Add(translate);

                    Color wordColor = isHigh ? accentCol : Colors.White;

                    var tb = new TextBlock
                    {
                        Text = w.Text.ToUpperInvariant(),
                        FontFamily = new FontFamily("Segoe UI, Inter"),
                        FontWeight = FontWeights.Bold,
                        FontSize = baseFontSize,
                        Foreground = new SolidColorBrush(wordColor),
                        RenderTransform = tg,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        Margin = new Thickness(baseFontSize * 0.09, 0, baseFontSize * 0.09, 0),
                        Effect = DefaultWordShadow,
                        Opacity = 0.35
                    };

                    var elem = new KineticWordElement
                    {
                        Container = tb,
                        TextBlock = tb,
                        ScaleTransform = scale,
                        TranslateTransform = translate,
                        SkewTransform = new SkewTransform(0, 0),
                        RotateTransform = new RotateTransform(0),
                        ScaleXSpring = KineticSpring.Create(1.0, 210.0, 24.0),
                        ScaleYSpring = KineticSpring.Create(1.0, 210.0, 24.0),
                        TranslateXSpring = KineticSpring.Create(0.0),
                        TranslateYSpring = KineticSpring.Create(0.0),
                        OpacitySpring = KineticSpring.Create(0.35, 190.0, 22.0),
                        TargetOpacity = 0.35,
                        IsHighlighted = isHigh,
                        HighlightColor = wordColor,
                        Template = KineticTemplate.Fisheye,
                        LensBaseOffsetY = (r - (rows.Count - 1) * 0.5) * baseFontSize * 1.32
                    };

                    _activeWordElements.Add(elem);
                    rowPanel.Children.Add(tb);

                    // Verci word-to-symbol emoji pairing:
                    string? symbol = LyricSymbolMapper.TryGetSymbol(w.Text);
                    if (symbol != null)
                    {
                        var symbolBlock = new TextBlock
                        {
                            Text = symbol,
                            FontSize = baseFontSize * 0.70,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(baseFontSize * 0.04, 0, baseFontSize * 0.08, 0),
                            Opacity = 0.55
                        };
                        rowPanel.Children.Add(symbolBlock);
                    }
                }

                root.Children.Add(rowPanel);
            }

            _cameraCanvas.Children.Add(root);

            // Connect vertical rise through the lens
            _stickerFitScale = null;
            _stickerLensXCap = 1.38;
            _fisheyeLensScale = new ScaleTransform(1, 1);
            _fisheyeRiseTransform = new TranslateTransform(0, 0);
            var lensGroup = new TransformGroup();
            lensGroup.Children.Add(_fisheyeLensScale);
            lensGroup.Children.Add(_fisheyeRiseTransform);
            root.RenderTransform = lensGroup;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            _fisheyeRiseActive = true;
            _fisheyeLineStart = line.StartTime;
            _fisheyeLineEnd = line.EndTime;

            AttachFisheyeLens(stageW, stageH);
        }

        private void DetachFisheyeLens()
        {
            _fisheyeRiseActive = false;
            _fisheyeRiseTransform = null;
            _fisheyeLensScale = null;
            _stickerFitScale = null;
            _stickerLensXCap = 1.38;
            _fisheyeRiseOffsetY = 0.0;
            if (_cameraCanvas.Effect != null)
            {
                _cameraCanvas.Effect = null;
            }
        }

        private void UpdateFisheyeRise(TimeSpan position, double stageW, double stageH)
        {
            if (!_fisheyeRiseActive || _fisheyeRiseTransform == null) return;

            double duration = Math.Max(0.5, (_fisheyeLineEnd - _fisheyeLineStart).TotalSeconds);
            double progress = Math.Clamp((position - _fisheyeLineStart).TotalSeconds / duration, 0.0, 1.0);

            // Continuous upward glide: enters from below the stage (+amp), lingers under
            // the lens at the midpoint, then exits above the stage (-amp). The quadratic
            // easing slows the line down exactly where the lens magnification peaks so
            // the sung words stay readable at the center of the glass.
            double q = 1.0 - 2.0 * progress;
            double offsetY = stageH * 0.62 * q * Math.Abs(q);

            _fisheyeRiseOffsetY = offsetY;
            _fisheyeRiseTransform.Y = offsetY;

            // Convex-lens swell: the whole line grows as it crosses the glass and
            // relaxes back to 1.0 as it leaves through the rim. Scaling the root
            // (rather than each word) keeps the sticker layout from overlapping.
            if (_fisheyeLensScale != null)
            {
                double half = Math.Max(1.0, stageH * 0.5);
                double dist = Math.Min(1.0, Math.Abs(offsetY) / half);
                double lens = 1.0 + 0.38 * (1.0 - dist * dist);
                _fisheyeLensScale.ScaleX = Math.Min(lens, _stickerLensXCap);
                _fisheyeLensScale.ScaleY = lens;
            }
        }

        private KineticWordElement CreateStickerBlockElement(string text, double fontSize, Color blockColor, Color textColor, bool isHighlighted)
        {
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var skew = new SkewTransform(0, 0);
            var rotate = new RotateTransform(0);

            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(skew);
            tg.Children.Add(rotate);
            tg.Children.Add(translate);

            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontFamily = new FontFamily("Segoe UI, Inter, Arial Black"),
                FontWeight = FontWeights.Black,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(textColor),
                TextAlignment = TextAlignment.Center
            };

            var block = new Border
            {
                Background = new SolidColorBrush(blockColor),
                CornerRadius = new CornerRadius(fontSize * 0.08),
                Padding = new Thickness(fontSize * 0.15, fontSize * 0.02, fontSize * 0.15, fontSize * 0.02),
                Child = tb,
                RenderTransform = tg,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness(fontSize * 0.14, 0, fontSize * 0.14, 0),
                Effect = DefaultWordShadow,
                Opacity = 0.42
            };

            return new KineticWordElement
            {
                Container = block,
                TextBlock = tb,
                ScaleTransform = scale,
                TranslateTransform = translate,
                SkewTransform = skew,
                RotateTransform = rotate,
                ScaleXSpring = KineticSpring.Create(1.0, 190.0, 22.0),
                ScaleYSpring = KineticSpring.Create(1.0, 190.0, 22.0),
                TranslateXSpring = KineticSpring.Create(0.0),
                TranslateYSpring = KineticSpring.Create(0.0),
                OpacitySpring = KineticSpring.Create(0.42, 180.0, 22.0),
                TargetOpacity = 0.42,
                IsHighlighted = isHighlighted,
                HighlightColor = blockColor,
                Template = KineticTemplate.Sticker
            };
        }

        private KineticWordElement CreateStickerGlossElement(string text, double fontSize, Color color, bool isHighlighted)
        {
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var skew = new SkewTransform(0, 0);
            var rotate = new RotateTransform(0);

            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(skew);
            tg.Children.Add(rotate);
            tg.Children.Add(translate);

            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontFamily = new FontFamily("Segoe UI, Inter, Arial Black"),
                FontWeight = FontWeights.Black,
                FontSize = fontSize,
                Foreground = CreateGlossBrush(color),
                RenderTransform = tg,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness(fontSize * 0.10, 0, fontSize * 0.10, 0),
                Opacity = 0.42
            };

            var halo = new DropShadowEffect
            {
                Color = color,
                BlurRadius = isHighlighted ? 24.0 : 14.0,
                ShadowDepth = 0.0,
                Opacity = isHighlighted ? 0.75 : 0.50,
                RenderingBias = RenderingBias.Performance
            };
            halo.Freeze();
            tb.Effect = halo;

            return new KineticWordElement
            {
                Container = tb,
                TextBlock = tb,
                ScaleTransform = scale,
                TranslateTransform = translate,
                SkewTransform = skew,
                RotateTransform = rotate,
                ScaleXSpring = KineticSpring.Create(1.0, 190.0, 22.0),
                ScaleYSpring = KineticSpring.Create(1.0, 190.0, 22.0),
                TranslateXSpring = KineticSpring.Create(0.0),
                TranslateYSpring = KineticSpring.Create(0.0),
                OpacitySpring = KineticSpring.Create(0.42, 180.0, 22.0),
                TargetOpacity = 0.42,
                IsHighlighted = isHighlighted,
                HighlightColor = color,
                Template = KineticTemplate.Sticker
            };
        }

        private static LinearGradientBrush CreateGlossBrush(Color baseColor)
        {
            var lite = Color.FromRgb(
                (byte)Math.Min(255, baseColor.R + 140),
                (byte)Math.Min(255, baseColor.G + 110),
                (byte)Math.Min(255, baseColor.B + 110));
            var dark = Color.FromRgb(
                (byte)(baseColor.R * 0.86),
                (byte)(baseColor.G * 0.72),
                (byte)(baseColor.B * 0.72));

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0.12, 0.0),
                EndPoint = new Point(0.42, 1.0)
            };
            brush.GradientStops.Add(new GradientStop(lite, 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(175, 255, 255, 255), 0.30));
            brush.GradientStops.Add(new GradientStop(lite, 0.44));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.60));
            brush.GradientStops.Add(new GradientStop(dark, 1.0));
            brush.Freeze();
            return brush;
        }

        private FrameworkElement CreateStickerDoodle(int kind, double size, Color color, Func<double> rng)
        {
            var brush = new SolidColorBrush(color);
            if (!brush.IsFrozen) brush.Freeze();

            FrameworkElement el;
            double stroke = Math.Max(2.0, size * 0.07);

            switch (kind)
            {
                case 0:
                    el = new Path
                    {
                        Data = Geometry.Parse("M10,2 L90,2 Q98,2 98,10 L98,50 Q98,58 90,58 L46,58 L28,76 L31,58 L10,58 Q2,58 2,50 L2,10 Q2,2 10,2 Z"),
                        Fill = brush,
                        Stretch = Stretch.Uniform,
                        Width = size,
                        Height = size * 0.76
                    };
                    break;
                case 1:
                    {
                        var grid = new Grid { Width = size, Height = size * 0.66 };
                        grid.Children.Add(new Path
                        {
                            Data = Geometry.Parse("M2,14 C22,-6 44,34 62,12 C74,-2 88,6 96,0"),
                            Stroke = brush,
                            StrokeThickness = stroke,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            Stretch = Stretch.Uniform
                        });
                        grid.Children.Add(new Path
                        {
                            Data = Geometry.Parse("M2,38 C24,14 46,62 64,36 C76,20 90,26 98,20"),
                            Stroke = brush,
                            StrokeThickness = stroke * 0.8,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            Stretch = Stretch.Uniform
                        });
                        el = grid;
                        break;
                    }
                case 2:
                    el = new Path
                    {
                        Data = Geometry.Parse("M50,0 L58,42 L100,50 L58,58 L50,100 L42,58 L0,50 L42,42 Z"),
                        Fill = brush,
                        Stretch = Stretch.Uniform,
                        Width = size * 0.72,
                        Height = size * 0.72
                    };
                    break;
                default:
                    el = new Path
                    {
                        Data = Geometry.Parse("M50,2 L50,26 M50,98 L50,74 M2,50 L26,50 M98,50 L74,50 M16,16 L32,32 M84,84 L68,68 M84,16 L68,32 M16,84 L32,68"),
                        Stroke = brush,
                        StrokeThickness = stroke,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stretch = Stretch.Uniform,
                        Width = size * 0.80,
                        Height = size * 0.80
                    };
                    break;
            }

            el.VerticalAlignment = VerticalAlignment.Center;
            el.Margin = new Thickness(size * 0.18, 0, size * 0.18, 0);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new RotateTransform((rng() - 0.5) * 30.0);
            el.Opacity = 0.45;
            System.Windows.Controls.Panel.SetZIndex(el, 2);
            return el;
        }

        #endregion

        #region Word Elements & Springs

        private sealed class KineticWordElement
        {
            public FrameworkElement Container { get; set; } = null!;
            public TextBlock TextBlock { get; set; } = null!;
            public ScaleTransform ScaleTransform { get; set; } = null!;
            public TranslateTransform TranslateTransform { get; set; } = null!;
            public SkewTransform SkewTransform { get; set; } = null!;
            public RotateTransform RotateTransform { get; set; } = null!;

            public KineticSpring ScaleXSpring;
            public KineticSpring ScaleYSpring;
            public KineticSpring TranslateXSpring;
            public KineticSpring TranslateYSpring;
            public KineticSpring OpacitySpring;

            public double SkewX;
            public double TargetOpacity = 1.0;
            public double CenterX;
            public double CenterY;
            public bool IsHighlighted;
            public Color HighlightColor;
            public KineticTemplate Template;

            // Sticker mode support (seeded tilt + per-frame wobble)
            public double BaseRotation;
            public double WobblePhase;
            public double RiseOffset;

            // Lens mode support: resting vertical offset of this word's row from stage center
            public double LensBaseOffsetY;

            // Letters mode support
            public List<TextBlock>? LetterBlocks;
            public List<KineticSpring>? LetterRotateXSprings;
        }

        private KineticWordElement CreateWordElement(string text, double fontSize, Color color, bool isHighlighted, KineticTemplate template)
        {
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var skew = new SkewTransform(0, 0);
            var rotate = new RotateTransform(0);

            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(skew);
            tg.Children.Add(rotate);
            tg.Children.Add(translate);

            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontFamily = new FontFamily("Segoe UI, Inter, Arial Black"),
                FontWeight = FontWeights.Black,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(color),
                RenderTransform = tg,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness(fontSize * 0.12, 0, fontSize * 0.12, 0)
            };

            // Deep drop shadow + glowing accent halo if highlighted
            if (isHighlighted)
            {
                var halo = new DropShadowEffect
                {
                    Color = color,
                    BlurRadius = 14.0,
                    ShadowDepth = 0.0,
                    Opacity = 0.85,
                    RenderingBias = RenderingBias.Performance
                };
                halo.Freeze();
                tb.Effect = halo;
            }
            else
            {
                tb.Effect = DefaultWordShadow;
            }

            var elem = new KineticWordElement
            {
                Container = tb,
                TextBlock = tb,
                ScaleTransform = scale,
                TranslateTransform = translate,
                SkewTransform = skew,
                RotateTransform = rotate,
                ScaleXSpring = KineticSpring.Create(1.0),
                ScaleYSpring = KineticSpring.Create(1.0),
                TranslateXSpring = KineticSpring.Create(0.0),
                TranslateYSpring = KineticSpring.Create(0.0),
                OpacitySpring = KineticSpring.Create(1.0),
                IsHighlighted = isHighlighted,
                HighlightColor = color,
                Template = template
            };

            return elem;
        }

        private KineticWordElement CreateLettersElement(string text, double fontSize, Color color, bool isHighlighted, bool fromTop)
        {
            var letters = KineticLayout.SplitLetters(text.ToUpperInvariant());
            var letterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(fontSize * 0.12, 0, fontSize * 0.12, 0)
            };

            var letterBlocks = new List<TextBlock>();
            var springs = new List<KineticSpring>();

            // Single frozen drop shadow on the word's letter panel instead of per-letter
            if (isHighlighted)
            {
                var halo = new DropShadowEffect
                {
                    Color = color,
                    BlurRadius = 14.0,
                    ShadowDepth = 0.0,
                    Opacity = 0.85,
                    RenderingBias = RenderingBias.Performance
                };
                halo.Freeze();
                letterPanel.Effect = halo;
            }
            else
            {
                letterPanel.Effect = DefaultWordShadow;
            }

            foreach (var ch in letters)
            {
                var scale = new ScaleTransform(1, 1);
                var tb = new TextBlock
                {
                    Text = ch,
                    FontFamily = new FontFamily("Segoe UI, Inter, Arial Black"),
                    FontWeight = FontWeights.Black,
                    FontSize = fontSize,
                    Foreground = new SolidColorBrush(color),
                    RenderTransform = scale,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };

                letterBlocks.Add(tb);
                // Flip in scaleY from 0 to 1
                springs.Add(KineticSpring.Create(0.0, 160.0, 22.0));
                letterPanel.Children.Add(tb);
            }

            var elem = new KineticWordElement
            {
                Container = letterPanel,
                LetterBlocks = letterBlocks,
                LetterRotateXSprings = springs,
                ScaleTransform = new ScaleTransform(1, 1),
                TranslateTransform = new TranslateTransform(0, 0),
                SkewTransform = new SkewTransform(0, 0),
                RotateTransform = new RotateTransform(0),
                ScaleXSpring = KineticSpring.Create(1.0),
                ScaleYSpring = KineticSpring.Create(1.0),
                TranslateXSpring = KineticSpring.Create(0.0),
                TranslateYSpring = KineticSpring.Create(0.0),
                OpacitySpring = KineticSpring.Create(0.0),
                IsHighlighted = isHighlighted,
                HighlightColor = color,
                Template = KineticTemplate.Letters
            };

            return elem;
        }

        private void UpdateWordAnimations(int revealed, double dt, double stageW, double stageH, double driftX, double driftY)
        {
            for (int i = 0; i < _activeWordElements.Count; i++)
            {
                var elem = _activeWordElements[i];
                bool isSung = i < revealed;
                bool isActive = i == revealed - 1;

                if (elem.Template == KineticTemplate.Flow)
                {
                    elem.TargetOpacity = isSung ? 1.0 : 0.32;
                    elem.ScaleXSpring.Target = isActive ? 1.05 : 1.0;
                    elem.ScaleYSpring.Target = isActive ? 1.05 : 1.0;
                    elem.Container.Opacity += (elem.TargetOpacity - elem.Container.Opacity) * Math.Min(1.0, dt * 10.0);
                }
                else if (elem.Template == KineticTemplate.Letters)
                {
                    if (isSung)
                    {
                        elem.Container.Opacity = 1.0;
                        if (elem.LetterRotateXSprings != null && elem.LetterBlocks != null)
                        {
                            for (int j = 0; j < elem.LetterRotateXSprings.Count; j++)
                            {
                                var s = elem.LetterRotateXSprings[j];
                                s.Target = 1.0;
                                s.Update(dt);
                                elem.LetterRotateXSprings[j] = s;

                                if (elem.LetterBlocks[j].RenderTransform is ScaleTransform st)
                                {
                                    st.ScaleY = Math.Clamp(s.Value, 0.0, 1.1);
                                }
                            }
                        }
                    }
                    else
                    {
                        elem.Container.Opacity = 0.0;
                    }
                }
                else if (elem.Template == KineticTemplate.Sticker)
                {
                    if (isSung)
                    {
                        elem.TargetOpacity = 1.0;
                        elem.ScaleXSpring.Target = isActive ? 1.18 : 1.0;
                        elem.ScaleYSpring.Target = isActive ? 1.18 : 1.0;
                    }
                    else
                    {
                        elem.TargetOpacity = 0.42;
                        elem.ScaleXSpring.Target = 1.0;
                        elem.ScaleYSpring.Target = 1.0;
                    }

                    elem.OpacitySpring.Target = elem.TargetOpacity;
                    elem.OpacitySpring.Update(dt);
                    elem.ScaleXSpring.Update(dt);
                    elem.ScaleYSpring.Update(dt);

                    elem.Container.Opacity = Math.Clamp(elem.OpacitySpring.Value * ComputeLensFade(elem, stageH), 0.0, 1.0);
                    elem.ScaleTransform.ScaleX = elem.ScaleXSpring.Value;
                    elem.ScaleTransform.ScaleY = elem.ScaleYSpring.Value;

                    // Sticker wobble: each cut-out sways slightly, the active singing word more
                    double wobble = Math.Sin(_ambientTime * 2.5 + elem.WobblePhase) * (isActive ? 3.0 : 0.8);
                    elem.RotateTransform.Angle = elem.BaseRotation + wobble;
                }
                else if (elem.Template == KineticTemplate.Fisheye)
                {
                    if (isSung)
                    {
                        elem.TargetOpacity = 1.0;
                        elem.ScaleXSpring.Target = isActive ? 1.14 : 1.0;
                        elem.ScaleYSpring.Target = isActive ? 1.14 : 1.0;
                    }
                    else
                    {
                        elem.TargetOpacity = 0.35;
                        elem.ScaleXSpring.Target = 1.0;
                        elem.ScaleYSpring.Target = 1.0;
                    }

                    elem.OpacitySpring.Target = elem.TargetOpacity;
                    elem.OpacitySpring.Update(dt);
                    elem.ScaleXSpring.Update(dt);
                    elem.ScaleYSpring.Update(dt);

                    elem.Container.Opacity = Math.Clamp(elem.OpacitySpring.Value * ComputeLensFade(elem, stageH), 0.0, 1.0);
                    elem.ScaleTransform.ScaleX = elem.ScaleXSpring.Value;
                    elem.ScaleTransform.ScaleY = elem.ScaleYSpring.Value;
                }
                else
                {
                    if (isSung)
                    {
                        elem.OpacitySpring.Target = 1.0;
                        elem.ScaleXSpring.Target = 1.0;
                        elem.ScaleYSpring.Target = 1.0;
                        elem.TranslateXSpring.Target = 0.0;
                        elem.TranslateYSpring.Target = 0.0;
                        elem.SkewX *= Math.Max(0.0, 1.0 - dt * 8.0);
                    }
                    else
                    {
                        elem.OpacitySpring.Target = 0.0;
                    }

                    elem.OpacitySpring.Update(dt);
                    elem.ScaleXSpring.Update(dt);
                    elem.ScaleYSpring.Update(dt);
                    elem.TranslateXSpring.Update(dt);
                    elem.TranslateYSpring.Update(dt);

                    elem.Container.Opacity = Math.Clamp(elem.OpacitySpring.Value, 0.0, 1.0);
                    elem.ScaleTransform.ScaleX = elem.ScaleXSpring.Value;
                    elem.ScaleTransform.ScaleY = elem.ScaleYSpring.Value;
                    elem.TranslateTransform.X = elem.TranslateXSpring.Value;
                    elem.TranslateTransform.Y = elem.TranslateYSpring.Value;
                    elem.SkewTransform.AngleX = elem.SkewX;
                }
            }

            // Camera drift
            _cameraTranslateTransform.X = driftX;
            _cameraTranslateTransform.Y = driftY;
        }

        #endregion

        #region FitBox & Helpers

        /// <summary>
        /// Fades a lens-riding word by its distance from the center of the glass so
        /// lyrics materialise out of the bottom and dissolve as they leave the top.
        /// </summary>
        private double ComputeLensFade(KineticWordElement elem, double stageH)
        {
            if (!_fisheyeRiseActive) return 1.0;

            // Row offset + live rise, measured from the center of the lens. Both are
            // in camera-canvas space, so the camera scale converts them to the stage.
            double wordY = (elem.LensBaseOffsetY + _fisheyeRiseOffsetY) * _cameraScaleTransform.ScaleY;
            double half = Math.Max(1.0, stageH * 0.5);
            double dist = Math.Abs(wordY) / half;

            double fade = 1.0 - SmoothStep(0.30, 0.95, dist);
            return Math.Clamp(fade, 0.0, 1.0);
        }

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Clamp((x - edge0) / Math.Max(1e-4, edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private double FitFontToRows(List<List<LyricWord>> rows, double maxW, double maxH, double minFont, double maxFont)
        {
            double longestEm = 0.5;
            foreach (var r in rows)
            {
                double rowLen = 0;
                foreach (var w in r) rowLen += KineticLayout.VisualLength(w.Text) + 0.25;
                if (rowLen > longestEm) longestEm = rowLen;
            }

            double fontByWidth = maxW / longestEm;
            double fontByHeight = (maxH / Math.Max(1, rows.Count)) * 0.72;
            double chosen = Math.Min(fontByWidth, fontByHeight);

            return Math.Clamp(chosen, minFont, maxFont);
        }

        private void ApplyFitBox(double stageW, double stageH)
        {
            if (_cameraCanvas.Children.Count == 0) return;

            var root = _layoutRoot as FrameworkElement;
            if (root == null) return;

            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double measuredW = root.DesiredSize.Width;
            double measuredH = root.DesiredSize.Height;

            double safeW = stageW * 0.88;
            double safeH = stageH * 0.70;

            double scale = 1.0;
            if (measuredW > safeW || measuredH > safeH)
            {
                double scaleX = safeW / Math.Max(1.0, measuredW);
                double scaleY = safeH / Math.Max(1.0, measuredH);
                scale = Math.Min(scaleX, scaleY);
            }

            _cameraScaleTransform.ScaleX = scale;
            _cameraScaleTransform.ScaleY = scale;
            _cameraScaleTransform.CenterX = stageW / 2.0;
            _cameraScaleTransform.CenterY = stageH / 2.0;

            Canvas.SetLeft(root, (stageW - measuredW) / 2.0);
            Canvas.SetTop(root, (stageH - measuredH) / 2.0);

            // Compute center of each word element for word ripples
            foreach (var elem in _activeWordElements)
            {
                Point pos = elem.Container.TranslatePoint(new Point(0, 0), _hostCanvas);
                elem.CenterX = pos.X + elem.Container.ActualWidth / 2.0;
                elem.CenterY = pos.Y + elem.Container.ActualHeight / 2.0;
                if (double.IsNaN(elem.CenterX)) elem.CenterX = stageW / 2.0;
                if (double.IsNaN(elem.CenterY)) elem.CenterY = stageH / 2.0;
            }
        }

        #endregion

        #region Word Ripples

        private void EmitWordRipple(double x, double y, Color color)
        {
            _ripples.Add(new Ripple
            {
                X = x,
                Y = y,
                Age = 0.0,
                MaxAge = 1.4,
                MaxRadius = 140.0,
                Color = color
            });
        }

        private void UpdateRipples(double dt)
        {
            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                var r = _ripples[i];
                r.Age += dt;
                if (r.Age >= r.MaxAge)
                {
                    _ripples.RemoveAt(i);
                }
                else
                {
                    _ripples[i] = r;
                }
            }

            for (int i = 0; i < _ripplePool.Count; i++)
            {
                var el = _ripplePool[i];
                if (i < _ripples.Count)
                {
                    var r = _ripples[i];
                    double progress = r.Age / r.MaxAge;
                    double radius = r.MaxRadius * (1.0 - Math.Pow(1.0 - progress, 2.5));
                    double opacity = (1.0 - progress) * 0.45;

                    var (st, tt) = _rippleTransforms[i];
                    double scale = Math.Max(0.01, radius / 50.0);
                    st.ScaleX = scale;
                    st.ScaleY = scale;
                    tt.X = r.X - 50.0;
                    tt.Y = r.Y - 50.0;

                    if (el.Stroke is SolidColorBrush scb && !scb.IsFrozen)
                    {
                        if (scb.Color != r.Color) scb.Color = r.Color;
                    }
                    else
                    {
                        el.Stroke = new SolidColorBrush(r.Color);
                    }
                    el.Opacity = opacity;

                    if (el.Visibility != Visibility.Visible) el.Visibility = Visibility.Visible;
                }
                else
                {
                    if (el.Visibility != Visibility.Collapsed) el.Visibility = Visibility.Collapsed;
                }
            }
        }

        #endregion
    }
}
