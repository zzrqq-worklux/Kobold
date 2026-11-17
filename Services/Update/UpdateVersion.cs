using System;

namespace Kobold.Services.Update
{
    /// <summary>
    /// Version rules for the updater. System.Version treats "2.0.1" and
    /// "2.0.1.0" as different, so comparisons pad the missing build/revision
    /// parts with zero; a side that does not parse never looks like an update.
    /// </summary>
    public static class UpdateVersion
    {
        /// <summary>
        /// Parses a dotted version such as "2.0.1" or "2.0.1.0". Trailing
        /// whitespace (a stray newline from a file or header) is ignored.
        /// </summary>
        public static bool TryParse(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Version.TryParse(text.TrimEnd(), out version);
        }

        /// <summary>
        /// True only when both versions parse and <paramref name="remote"/> is
        /// strictly newer than <paramref name="local"/>.
        /// </summary>
        public static bool IsNewer(string local, string remote)
        {
            Version localVersion;
            Version remoteVersion;
            if (!TryParse(local, out localVersion) || !TryParse(remote, out remoteVersion)) return false;
            return Compare(remoteVersion, localVersion) > 0;
        }

        private static int Compare(Version left, Version right)
        {
            int result = left.Major.CompareTo(right.Major);
            if (result != 0) return result;
            result = left.Minor.CompareTo(right.Minor);
            if (result != 0) return result;
            result = Build(left).CompareTo(Build(right));
            if (result != 0) return result;
            return Revision(left).CompareTo(Revision(right));
        }

        // Version reports -1 for an absent part; for release comparison an
        // absent part is the same as an explicit 0.
        private static int Build(Version version)
        {
            return version.Build < 0 ? 0 : version.Build;
        }

        private static int Revision(Version version)
        {
            return version.Revision < 0 ? 0 : version.Revision;
        }
    }
}
