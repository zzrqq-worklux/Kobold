using System;
using System.Diagnostics;
using System.Threading;
using Kobold.Helpers;

namespace Kobold.Core
{
    /// <summary>
    /// Applies the desired Quick Access state for the storage folder. Unlike
    /// the startup reconcile (which only ever pins), a control-plane patch is
    /// explicit about both directions: true pins when needed, false unpins
    /// when the folder is pinned. Explorer handles both verbs asynchronously,
    /// so the result is polled briefly before it is reported.
    /// </summary>
    public static class QuickAccessService
    {
        private const int PollAttempts = 10;
        private const int PollIntervalMs = 100;

        /// <summary>
        /// Test seam: when set, the checks fake the Shell action. Null in
        /// production, where pin/unpin probes the real Quick Access namespace.
        /// </summary>
        internal static Func<bool, bool> Applier { get; set; }

        /// <summary>
        /// Brings Quick Access in line with <paramref name="pinned"/> and
        /// returns false on refusal or exception. Never throws.
        /// </summary>
        public static bool Apply(bool pinned)
        {
            Func<bool, bool> applier = Applier;
            if (applier != null) return applier(pinned);

            try
            {
                string path = Utils.GetStoragePath();
                bool? state = QuickAccess.TryIsPinned(path);
                if (state == pinned) return true;

                bool changed = pinned
                    ? QuickAccess.Pin(path)
                    : QuickAccess.Unpin(path);
                if (!changed) return false;

                // The verb can succeed before Explorer publishes the new
                // entry. An inconclusive probe (null: the namespace could not
                // be enumerated) must not turn a successful verb into failure.
                bool? settled = WaitForPinState(path, pinned);
                return !settled.HasValue || settled.Value == pinned;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Quick Access apply failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Polls the read-only probe for Explorer's asynchronous publish. The
        /// final state is returned even when it never matches; null means the
        /// namespace could not be enumerated.
        /// </summary>
        private static bool? WaitForPinState(string path, bool wanted)
        {
            bool? state = null;
            for (int i = 0; i < PollAttempts; i++)
            {
                state = QuickAccess.TryIsPinned(path);
                if (state == wanted) return state;
                Thread.Sleep(PollIntervalMs);
            }
            return state;
        }
    }
}
