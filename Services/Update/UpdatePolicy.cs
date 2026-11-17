using System;
using System.Globalization;

namespace Kobold.Services.Update
{
    /// <summary>
    /// Pure rules for when the updater may act: whether a release is worth
    /// offering given the running version and the user's skip choice, and
    /// whether enough time has passed since the last check. No state, no I/O.
    /// </summary>
    public static class UpdatePolicy
    {
        /// <summary>How long the app waits between automatic checks.</summary>
        public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// True only when the manifest advertises a version strictly newer than
        /// the running one and the user has not skipped that version.
        /// </summary>
        public static bool ShouldOffer(UpdateManifest manifest, string localVersion, string skippedVersion)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.Version)) return false;
            if (!UpdateVersion.IsNewer(localVersion, manifest.Version)) return false;
            if (IsSameVersion(skippedVersion, manifest.Version)) return false;
            return true;
        }

        /// <summary>
        /// True when checking is enabled and the previous check is stale (or
        /// never happened). An unreadable timestamp counts as "never checked"
        /// so a corrupted value can only make the app check sooner.
        /// </summary>
        public static bool ShouldCheck(bool enabled, string lastCheckUtcIso, DateTime nowUtc, TimeSpan interval)
        {
            if (!enabled) return false;
            if (string.IsNullOrWhiteSpace(lastCheckUtcIso)) return true;

            DateTime lastUtc;
            if (!DateTime.TryParse(lastCheckUtcIso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out lastUtc))
            {
                return true;
            }
            return nowUtc - lastUtc >= interval;
        }

        /// <summary>
        /// Version-aware skip comparison: "2.0.1" and "2.0.1.0" name the same
        /// release, so skipping one hides the other. Unparseable values never
        /// count as a match.
        /// </summary>
        private static bool IsSameVersion(string skippedVersion, string version)
        {
            if (string.IsNullOrEmpty(skippedVersion)) return false;
            if (string.Equals(skippedVersion, version, StringComparison.Ordinal)) return true;
            if (!UpdateVersion.TryParse(skippedVersion, out _) ||
                !UpdateVersion.TryParse(version, out _))
            {
                return false;
            }
            return !UpdateVersion.IsNewer(skippedVersion, version)
                && !UpdateVersion.IsNewer(version, skippedVersion);
        }
    }
}
