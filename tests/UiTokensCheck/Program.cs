using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Kobold.Core;

namespace Kobold.UiTokensCheck
{
    /// <summary>
    /// Guards the UI token layer (Core/UiTokens.cs):
    /// 1. Dark/Light palettes exist, parse as hex, expose the same field set,
    ///    and keep the six gray roles distinct.
    /// 2. Radius / type / spacing / motion scales stay on their declared steps.
    /// 3. Utils.HexToColor understands both RGB and ARGB token strings.
    /// 4. No hex color literal survives outside UiTokens.cs (the stored-badge
    ///    illustration colors are the only allowed exception).
    /// Exit code 0 = all green; 1 = failures. Run via:
    /// dotnet run --project tests/UiTokensCheck
    /// </summary>
    internal static class Program
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        private static readonly List<string> Errors = new List<string>();

        private static readonly Regex TokenHex = new Regex("^#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?$", RegexOptions.Compiled);
        private static readonly Regex HexLiteral = new Regex("#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})(?![0-9A-Fa-f])", RegexOptions.Compiled);

        // Colors of the stored-badge illustration (FolderWidget.xaml) - a
        // vendor drawing, not part of the theme palette.
        private static readonly HashSet<string> AllowedHexLiterals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "#1296db", "#2c2c2c", "#F5F7FA"
        };

        private static readonly string[] GrayRoles =
        {
            "Background", "Surface", "Border", "TextMuted", "TextSecondary", "TextPrimary"
        };

        private static readonly string[] OverlayRoles =
        {
            "Background", "Border", "Hover", "Checked", "Text", "Muted", "Divider",
            "TooltipBackground", "TooltipBorder"
        };

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            CheckPalette(typeof(UiTokens.Dark), "dark", GrayRoles, true);
            CheckPalette(typeof(UiTokens.Light), "light", GrayRoles, true);
            CheckPaletteParity(typeof(UiTokens.Dark), typeof(UiTokens.Light), "dark/light palettes");
            CheckPalette(typeof(UiTokens.OverlayDark), "overlay-dark", OverlayRoles, false);
            CheckPalette(typeof(UiTokens.OverlayLight), "overlay-light", OverlayRoles, false);
            CheckPaletteParity(typeof(UiTokens.OverlayDark), typeof(UiTokens.OverlayLight), "menu overlay palettes");
            CheckExactScale("radius", new[] { UiTokens.RadiusSmall, UiTokens.RadiusControl, UiTokens.RadiusWindow, UiTokens.RadiusPanel }, new[] { 4.0, 8.0, 12.0, 16.0 });
            CheckExactScale("type", new[] { UiTokens.FontCaption, UiTokens.FontBody, UiTokens.FontSubtitle, UiTokens.FontTitle }, new[] { 11.0, 12.0, 13.0, 16.0 });
            CheckExactScale("spacing", new[] { UiTokens.Space1, UiTokens.Space2, UiTokens.Space3, UiTokens.Space4, UiTokens.Space6 }, new[] { 4.0, 8.0, 12.0, 16.0, 24.0 });
            CheckExactScale("motion", new[] { (double)UiTokens.DurationFastMs, UiTokens.DurationNormalMs, UiTokens.DurationSlowMs }, new[] { 100.0, 150.0, 200.0 });
            CheckHexParsing();
            CheckColorMixing();
            CheckNoStrayHexLiterals();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL UI-TOKENS CHECKS PASSED");
                return 0;
            }

            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void CheckPalette(Type theme, string name, string[] requiredRoles, bool requireDistinctRoles)
        {
            var fields = theme.GetFields(PublicStatic).Where(f => f.FieldType == typeof(string)).ToList();
            var missing = requiredRoles.Where(role => fields.All(f => f.Name != role)).ToList();
            if (missing.Count > 0)
            {
                Errors.Add(name + " palette missing roles: " + string.Join(",", missing));
                return;
            }

            int bad = 0;
            foreach (var field in fields)
            {
                var value = (string)field.GetValue(null);
                if (value == null || !TokenHex.IsMatch(value)) bad++;
            }
            if (bad > 0)
                Errors.Add(name + " palette has " + bad + " values that are not #RRGGBB/#AARRGGBB");

            if (requireDistinctRoles)
            {
                var roleValues = requiredRoles
                    .Select(role => (string)theme.GetField(role, PublicStatic).GetValue(null))
                    .ToList();
                if (roleValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requiredRoles.Length)
                    Errors.Add(name + " palette roles are not distinct");
            }

            if (missing.Count == 0 && bad == 0)
                Console.WriteLine("PASS " + name + " palette: " + fields.Count + " colors, " + requiredRoles.Length + " roles present");
        }

        private static void CheckPaletteParity(Type darkType, Type lightType, string label)
        {
            var dark = StringFieldNames(darkType);
            var light = StringFieldNames(lightType);
            var missing = dark.Except(light).Concat(light.Except(dark)).ToList();
            if (missing.Count == 0)
                Console.WriteLine("PASS " + label + " expose the same " + dark.Count + " fields");
            else
                Errors.Add(label + " field mismatch: " + string.Join(",", missing));
        }

        private static void CheckExactScale(string name, double[] actual, double[] expected)
        {
            var ok = actual.Length == expected.Length;
            for (int i = 0; ok && i < actual.Length; i++)
                ok = Math.Abs(actual[i] - expected[i]) < 0.001;

            if (ok) Console.WriteLine("PASS " + name + " scale = " + string.Join("/", expected));
            else Errors.Add(name + " scale = " + string.Join("/", actual) + " (expected " + string.Join("/", expected) + ")");
        }

        private static void CheckHexParsing()
        {
            var rgb = Utils.HexToColor("#3B82F6");
            var argb = Utils.HexToColor("#CC202020");
            var fallback = Utils.HexToColor("not-a-color");

            bool ok = rgb.A == 255 && rgb.R == 0x3B && rgb.G == 0x82 && rgb.B == 0xF6
                      && argb.A == 0xCC && argb.R == 0x20 && argb.G == 0x20 && argb.B == 0x20
                      && fallback.A == 255;

            if (ok) Console.WriteLine("PASS HexToColor parses #RRGGBB, #AARRGGBB and falls back safely");
            else Errors.Add("HexToColor: rgb=" + rgb + " argb=" + argb + " fallback=" + fallback);
        }

        private static void CheckColorMixing()
        {
            var baseColor = Color.FromRgb(0x28, 0x28, 0x28);
            var tint = Color.FromRgb(0x3B, 0x82, 0xF6);

            bool ok =
                Utils.MixColor(baseColor, tint, 0.0) == baseColor &&
                Utils.MixColor(baseColor, tint, 1.0) == tint &&
                Utils.MixColor(baseColor, tint, 0.14) == Color.FromRgb(0x2B, 0x35, 0x45) &&
                Utils.MixColor(baseColor, tint, -1.0) == baseColor &&
                Utils.MixColor(baseColor, tint, 2.0) == tint &&
                Utils.MixColor(Colors.Black, Colors.White, 0.5) == Color.FromRgb(0x80, 0x80, 0x80);

            if (ok) Console.WriteLine("PASS MixColor blends, clamps and rounds");
            else Errors.Add("MixColor failed (blend amount, clamping or rounding)");
        }

        private static void CheckNoStrayHexLiterals()
        {
            string root = FindRepoRoot();
            if (root == null)
            {
                Errors.Add("repo root (Kobold.csproj) not found");
                return;
            }

            var offenders = new List<string>();
            foreach (var file in EnumerateSourceFiles(root))
            {
                if (file.EndsWith(Path.Combine("Core", "UiTokens.cs"), StringComparison.OrdinalIgnoreCase))
                    continue;

                int lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    foreach (Match match in HexLiteral.Matches(line))
                    {
                        if (AllowedHexLiterals.Contains(match.Value)) continue;
                        offenders.Add(file.Substring(root.Length).TrimStart('\\') + ":" + lineNo + " " + match.Value);
                    }
                }
            }

            if (offenders.Count == 0)
            {
                Console.WriteLine("PASS no stray hex color literals outside Core/UiTokens.cs");
            }
            else
            {
                var shown = string.Join(" | ", offenders.Take(12));
                if (offenders.Count > 12) shown += " (+" + (offenders.Count - 12) + " more)";
                Errors.Add("hex literals outside UiTokens.cs: " + shown);
            }
        }

        private static HashSet<string> StringFieldNames(Type theme)
        {
            return new HashSet<string>(theme.GetFields(PublicStatic)
                .Where(f => f.FieldType == typeof(string))
                .Select(f => f.Name), StringComparer.Ordinal);
        }

        private static IEnumerable<string> EnumerateSourceFiles(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".cs" && ext != ".xaml") continue;

                string lower = file.ToLowerInvariant();
                if (lower.Contains("\\bin\\") || lower.Contains("\\obj\\") ||
                    lower.Contains("\\.git\\") || lower.Contains("\\.vs\\") ||
                    lower.Contains("\\tests\\"))
                    continue;

                yield return file;
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Kobold.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
