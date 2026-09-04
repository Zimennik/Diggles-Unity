#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using Diggles.Runtime;

    internal static class OriginalDataTestFixture
    {
        internal const string EnvironmentVariableName = "DIGGLES_DATA_ROOT";

        private static readonly Lazy<string> ResolvedDataRoot = new(
            () => ResolveDataRoot(ResolveConfiguredValue()));

        internal static string DataRoot => ResolvedDataRoot.Value;

        // The port saves into the same shared Gamesave directory (F2 quicksaves and autosaves,
        // 2026+). Native-corpus sweeps (exact pins, native-writer domain laws) must exclude
        // them; mtimes are unreliable in that directory (an external sync rewrites them), so
        // the port's own fixed name shapes are the discriminator.
        internal static bool IsPortWrittenSaveName(string fileName) =>
            fileName.StartsWith("auto_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("quick_Quicksave_202", StringComparison.OrdinalIgnoreCase) &&
                !fileName.StartsWith("quick_Quicksave_2020", StringComparison.OrdinalIgnoreCase) &&
                !fileName.StartsWith("quick_Quicksave_2021", StringComparison.OrdinalIgnoreCase) &&
                !fileName.StartsWith("quick_Quicksave_2022", StringComparison.OrdinalIgnoreCase);

        private static string? ResolveConfiguredValue()
        {
            var environmentValue = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return string.IsNullOrWhiteSpace(environmentValue)
                ? DigglesConfig.ResolveDataRoot(string.Empty)
                : environmentValue;
        }

        internal static string ResolveDataRoot(string? configuredValue)
        {
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                throw new InvalidOperationException(
                    $"Set {EnvironmentVariableName} or configure diggles_config.json with the legally obtained original Diggles data directory before running data-backed tests.");
            }

            var dataRoot = Path.GetFullPath(configuredValue.Trim());
            if (!Directory.Exists(dataRoot))
                throw new DirectoryNotFoundException($"Original Diggles data directory was not found at '{dataRoot}'.");

            var scriptsPath = Path.Combine(dataRoot, "Scripts");
            if (!Directory.Exists(scriptsPath))
                throw new DirectoryNotFoundException($"Original Diggles Scripts directory was not found at '{scriptsPath}'.");

            var blockDatabasePath = Path.Combine(dataRoot, "BlockDB2.BDB");
            if (!File.Exists(blockDatabasePath))
                throw new FileNotFoundException("Original Diggles BlockDB2.BDB was not found.", blockDatabasePath);

            return dataRoot.Replace('\\', '/');
        }
    }
}
