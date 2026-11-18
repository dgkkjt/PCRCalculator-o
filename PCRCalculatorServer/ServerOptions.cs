using System;
using System.IO;
using Newtonsoft.Json;
using PCRCaculator;

namespace PCRCalculatorServer
{
    /// <summary>
    /// Holds server runtime switches derived from command line arguments.
    /// </summary>
    public sealed class ServerOptions
    {
        public bool DebugMode { get; init; }
        public int IdleReleaseSeconds { get; init; } = 300;

        public static int ParseIdleReleaseSeconds(string[] args)
        {
            string value = Environment.GetEnvironmentVariable("PCRCALC_IDLE_RELEASE_SECONDS");
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--idle-release-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= args.Length)
                        throw new ArgumentException("--idle-release-seconds requires a non-negative integer (0 disables idle release).");
                    value = args[index];
                }
                else if (args[index].StartsWith("--idle-release-seconds=", StringComparison.OrdinalIgnoreCase))
                    value = args[index].Substring("--idle-release-seconds=".Length);
            }
            if (value == null) return 300;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int seconds) || seconds < 0)
                throw new ArgumentException("Idle release seconds must be a non-negative integer (0 disables idle release).");
            return seconds;
        }

        public static void ApplyClientContext()
        {
            var version = Environment.GetEnvironmentVariable("PCRCALC_VERSION_DATA");
            if (!string.IsNullOrWhiteSpace(version))
                MainManager.Instance.Version = JsonConvert.DeserializeObject<MainManager.VersionData>(version)
                    ?? throw new InvalidDataException("PCRCALC_VERSION_DATA cannot be null.");
            var persistentPath = Environment.GetEnvironmentVariable("PCRCALC_PERSISTENT_DATA_PATH");
            if (!string.IsNullOrWhiteSpace(persistentPath))
                UnityEngine.Application.persistentDataPath = Path.GetFullPath(persistentPath);
        }
    }
}
