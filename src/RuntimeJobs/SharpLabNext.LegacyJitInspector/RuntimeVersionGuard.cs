using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SharpLabNext.LegacyJitInspector
{
    /// <summary>
    /// Keeps the low-target helper from silently running on a different
    /// CoreCLR when the requested framework is absent and roll-forward is
    /// enabled by the container.
    /// </summary>
    internal static class RuntimeVersionGuard
    {
        public const string Switch = "--runtime-version";
        private static readonly char[] InvalidCharacters = { '\r', '\n', '\0' };

        public static string[] Extract(string[] args, out string expectedVersion)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));

            expectedVersion = null;
            if (args.Length < 2 || !string.Equals(args[0], Switch, StringComparison.Ordinal))
                return args;

            expectedVersion = args[1];
            ValidateSyntax(expectedVersion);

            var remaining = new string[args.Length - 2];
            if (remaining.Length > 0)
                Array.Copy(args, 2, remaining, 0, remaining.Length);
            return remaining;
        }

        public static void Validate(string expectedVersion)
        {
            if (expectedVersion == null)
                return;

                Version expected = ParseNumericPrefix(expectedVersion);
            Version actual = CurrentRuntimeVersion();
            if (!IsCompatible(expected, actual))
            {
                throw new InvalidOperationException(
                    "The Legacy JIT helper is running on runtime " +
                    actual.ToString() + ", but the operation selected " +
                    expectedVersion + ".");
            }
        }

        internal static Version CurrentRuntimeVersion()
        {
            const string imageVersionPath = "/opt/sharplabnext/runtime-version.txt";
            if (File.Exists(imageVersionPath))
            {
                string imageVersion = File.ReadAllText(imageVersionPath).Trim();
                return ParseNumericPrefix(imageVersion);
            }

            // Environment.Version is the CLR compatibility version on old
            // CoreCLR (for example 4.0.30319.42000 for .NET Core 2.0). The
            // framework description is the product identity and remains
            // stable across CoreCLR, modern .NET, and Wine CoreCLR.
            string description = RuntimeInformation.FrameworkDescription;
            Match match = Regex.Match(
                description,
                @"\.NET(?:\s+Core)?\s+(?<version>[0-9]+(?:\.[0-9]+){1,3})",
                RegexOptions.CultureInvariant);
            if (!match.Success ||
                !Version.TryParse(match.Groups["version"].Value, out Version actual))
            {
                throw new InvalidOperationException(
                    "The Legacy JIT helper could not determine the product runtime version from " +
                    RuntimeInformation.FrameworkDescription + ".");
            }
            return actual;
        }

        internal static bool IsCompatible(Version expected, Version actual)
        {
            if (expected == null || actual == null)
                return false;
            if (expected.Major != actual.Major || expected.Minor != actual.Minor)
                return false;
            if (expected.Build >= 0 && expected.Build != actual.Build)
                return false;
            return expected.Revision < 0 || expected.Revision == actual.Revision;
        }

        internal static Version ParseNumericPrefix(string value)
        {
            ValidateSyntax(value);
            var numeric = value;
            var prerelease = numeric.IndexOf('-');
            if (prerelease >= 0)
                numeric = numeric.Substring(0, prerelease);

            var parts = numeric.Split('.');
            if (parts.Length < 2 || parts.Length > 4)
                throw new ArgumentException(
                    "The runtime version must contain two to four numeric components.",
                    nameof(value));

            var numbers = new int[4] { 0, 0, -1, -1 };
            for (var index = 0; index < parts.Length; index++)
            {
                if (!int.TryParse(
                        parts[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var number) || number < 0)
                {
                    throw new ArgumentException(
                        "The runtime version must contain only non-negative numeric components.",
                        nameof(value));
                }
                numbers[index] = number;
            }
            return parts.Length switch
            {
                2 => new Version(numbers[0], numbers[1]),
                3 => new Version(numbers[0], numbers[1], numbers[2]),
                _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3])
            };
        }

        private static void ValidateSyntax(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 128 ||
                value.IndexOfAny(InvalidCharacters) >= 0)
            {
                throw new ArgumentException(
                    "The runtime version is missing or has an invalid length/content.",
                    nameof(value));
            }
        }
    }
}
