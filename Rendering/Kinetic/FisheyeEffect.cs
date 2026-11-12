using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LmenV2.Rendering.Kinetic
{
    /// <summary>
    /// Barrel/fisheye lens warp used by the Sticker lyric stage.
    /// Strength is the peak center magnification (0.45 ≈ 1.45x at the middle).
    /// </summary>
    public sealed class FisheyeEffect : ShaderEffect
    {
        private static readonly PixelShader Shader = LoadShader();

        public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
            nameof(Center), typeof(Point), typeof(FisheyeEffect),
            new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
            nameof(Strength), typeof(double), typeof(FisheyeEffect),
            new UIPropertyMetadata(0.45, PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
            nameof(Aspect), typeof(double), typeof(FisheyeEffect),
            new UIPropertyMetadata(1.6, PixelShaderConstantCallback(2)));

        public FisheyeEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(CenterProperty);
            UpdateShaderValue(StrengthProperty);
            UpdateShaderValue(AspectProperty);
        }

        public Point Center
        {
            get => (Point)GetValue(CenterProperty);
            set => SetValue(CenterProperty, value);
        }

        public double Strength
        {
            get => (double)GetValue(StrengthProperty);
            set => SetValue(StrengthProperty, value);
        }

        public double Aspect
        {
            get => (double)GetValue(AspectProperty);
            set => SetValue(AspectProperty, value);
        }

        private static PixelShader LoadShader()
        {
            var shader = new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/LmenV2;component/Rendering/Kinetic/Shaders/Fisheye.ps",
                    UriKind.Absolute)
            };
            shader.Freeze();
            return shader;
        }
    }
}
