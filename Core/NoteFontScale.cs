using System;

namespace Kobold.Core
{
    /// <summary>The note body's three size steps: a multiplier of UiTokens.FontBody.</summary>
    public static class NoteFontScale
    {
        public const double Small = 0.9;
        public const double Medium = 1.0;
        public const double Large = 1.2;

        /// <summary>
        /// Maps any stored value onto a usable step: NaN, infinity and
        /// non-positive values fall back to medium, and everything else is
        /// clamped into [Small, Large].
        /// </summary>
        public static double Normalize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return Medium;
            if (value < Small) return Small;
            if (value > Large) return Large;
            return value;
        }
    }
}
