using System;
using System.Text;
using Kobold.Core;
using Kobold.Services;

namespace Kobold.MemoryTrimCheck
{
    /// <summary>
    /// Checks for the idle memory trim: the policy (idle threshold, busy gate,
    /// one trim per idle period, interaction resets) and the trimmer itself.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/MemoryTrimCheck
    /// </summary>
    internal static class Program
    {
        private static readonly System.Collections.Generic.List<string> Errors =
            new System.Collections.Generic.List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            PolicyTrimsOnlyAfterTheThreshold();
            PolicyNeverTrimsWhileBusy();
            PolicyTrimsOncePerIdlePeriod();
            PolicyResetsOnInteraction();

            TrimmerReportsSuccessAndDoesNotThrow();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL MEMORY-TRIM CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        // ---------- policy ----------

        private static void PolicyTrimsOnlyAfterTheThreshold()
        {
            var policy = new IdleTrimPolicy();

            Check(!policy.ShouldTrim(TimeSpan.FromSeconds(119), false),
                "policy: just under the threshold stays resident");
            Check(policy.ShouldTrim(IdleTrimPolicy.IdleThreshold, false),
                "policy: the threshold itself already trims");
            Check(policy.ShouldTrim(TimeSpan.FromMinutes(10), false),
                "policy: a long idle trims");
            Check(!new IdleTrimPolicy().ShouldTrim(TimeSpan.FromSeconds(-5), false),
                "policy: a negative idle (clock skew) does not trim");
        }

        private static void PolicyNeverTrimsWhileBusy()
        {
            Check(!new IdleTrimPolicy().ShouldTrim(TimeSpan.FromMinutes(10), true),
                "policy: a drag/modal/animation in flight blocks the trim");
        }

        private static void PolicyTrimsOncePerIdlePeriod()
        {
            var policy = new IdleTrimPolicy();
            Check(policy.ShouldTrim(TimeSpan.FromMinutes(5), false), "policy: first long idle trims");

            policy.NotifyTrimmed();
            Check(!policy.ShouldTrim(TimeSpan.FromMinutes(30), false),
                "policy: the same idle period is not trimmed twice");
        }

        private static void PolicyResetsOnInteraction()
        {
            var policy = new IdleTrimPolicy();
            policy.ShouldTrim(TimeSpan.FromMinutes(5), false);
            policy.NotifyTrimmed();

            policy.NotifyInteraction();
            Check(!policy.ShouldTrim(TimeSpan.FromSeconds(30), false),
                "policy: fresh interaction restarts the idle period");
            Check(policy.ShouldTrim(TimeSpan.FromMinutes(5), false),
                "policy: the next long idle trims again");
        }

        // ---------- trimmer ----------

        private static void TrimmerReportsSuccessAndDoesNotThrow()
        {
            bool first = MemoryTrimmer.Trim();
            bool second = MemoryTrimmer.Trim();

            Check(first && second,
                "trimmer: Trim() succeeds on the current process (got " + first + "/" + second + ")");
        }
    }
}
