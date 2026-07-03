using Core.Enums;

namespace Core.Models
{
    internal class SettingsModel
    {
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTrayOnStartup { get; set; }
        public string? Theme { get; set; }
        public UpdateFrequency UpdateFrequency { get; set; }
        public bool AutoClaimRewards { get; set; }
        public bool NotifyOnDropUnlocked { get; set; }
        public bool NotifyOnReadyToClaim { get; set; }
        public bool NotifyOnAutoClaimed { get; set; }
        public bool VerboseDebugLogging { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool NotifyOnNewUpdateAvailable { get; set; }
        public DateTime? LastUpdateCheck { get; set; }
        public MiningPriorityMode MiningPriorityMode { get; set; } = MiningPriorityMode.AvailabilityThenProgress;
        public List<string> TwitchGameWhitelistSlugs { get; set; } = new List<string>();
        public List<string> KickGameWhitelistSlugs { get; set; } = new List<string>();
        // When true, the selected games are EXCLUDED (mine everything else) instead of being an allow-list.
        public bool TwitchGameFilterExclude { get; set; }
        public bool KickGameFilterExclude { get; set; }
        // Run WebView2 without GPU acceleration (for machines with unstable graphics drivers).
        public bool SoftwareRendering { get; set; }
        // Put the computer to sleep once every campaign is fully mined and claimed.
        public bool SleepWhenDone { get; set; }
        // UI language ("en" / "pl").
        public string? Language { get; set; }
        // First-run onboarding has been shown.
        public bool FirstRunCompleted { get; set; }
    }
}