using System;
using System.Collections.Generic;

namespace VoidLaunch.Models
{
    public sealed class GameEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public List<string> ExecutablePaths { get; set; } = new List<string>();
        public bool ExecutableManuallySelected { get; set; }
        public string InstallDirectory { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public bool ImageManuallySelected { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime? LastPlayed { get; set; }
        public long TotalPlayTimeSeconds { get; set; }
        public long LastSessionDurationSeconds { get; set; }
        public DateTime? LastSessionEnded { get; set; }
        public int LaunchCount { get; set; }
        public List<PlaySession> PlaySessions { get; set; } = new List<PlaySession>();
        public List<LaunchProfile> LaunchProfiles { get; set; } = new List<LaunchProfile>();
        public string SelectedLaunchProfileId { get; set; } = string.Empty;
        public DateTime DateAdded { get; set; } = DateTime.Now;
    }
}
