using System;

namespace Kobold.Core
{
    /// <summary>
    /// Decides when the idle working-set trim may run: after the app has been
    /// untouched for IdleThreshold, with nothing in flight, and only once per
    /// idle period (a fresh interaction starts a new period).
    /// </summary>
    public sealed class IdleTrimPolicy
    {
        /// <summary>Idle time after which the working set may be handed back to the OS.</summary>
        public static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(120);

        /// <summary>How often the app checks the policy.</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

        private bool _trimmedThisIdlePeriod;

        /// <summary>Any user interaction restarts the idle period.</summary>
        public void NotifyInteraction()
        {
            _trimmedThisIdlePeriod = false;
        }

        /// <summary>True when idle long enough, not busy, and not already trimmed this period.</summary>
        public bool ShouldTrim(TimeSpan idleFor, bool isBusy)
        {
            if (isBusy || _trimmedThisIdlePeriod) return false;
            return idleFor >= IdleThreshold;
        }

        /// <summary>Call after the trim ran, so the same idle period is not trimmed twice.</summary>
        public void NotifyTrimmed()
        {
            _trimmedThisIdlePeriod = true;
        }
    }
}
