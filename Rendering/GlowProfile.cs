using System;

namespace LmenV2.Rendering
{
    /// <summary>
    /// Constants and mathematical shaping curves for the ambient screen-edge glow.
    /// </summary>
    public static class GlowProfile
    {
        /// <summary>GlowSpread reference value in pixels.</summary>
        public const double ReferenceSpread = 125.0;

        /// <summary>Share of the screen perimeter covered by the glowing arc (55% beam, 45% gap).</summary>
        public const double BeamRatio = 0.55;

        /// <summary>Angular frequency of the breathing cycle: 2*PI/14 -> one breath per 14 s.</summary>
        public const double BreathOmega = 0.4488;

        /// <summary>
        /// Breathing value in 0..1 for a phase in radians and a user amount in 0..1.
        /// Uses smoothstep shaping so the glow gently dwells at the peaks and troughs.
        /// </summary>
        public static double Breathing(double phaseRadians, double amount)
        {
            double a = amount < 0.0 ? 0.0 : (amount > 1.0 ? 1.0 : amount);
            double raw = 0.5 - 0.5 * Math.Cos(phaseRadians);
            double shaped = raw * raw * (3.0 - 2.0 * raw);
            return 0.5 + (shaped - 0.5) * a;
        }
    }
}
