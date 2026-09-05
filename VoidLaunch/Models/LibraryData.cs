using System.Collections.Generic;

namespace VoidLaunch.Models
{
    public sealed class LibraryData
    {
        public List<GameEntry> Games { get; set; } = new List<GameEntry>();
        public List<GameEntry> PendingGames { get; set; } = new List<GameEntry>();
        public List<string> IgnoredScanPaths { get; set; } = new List<string>();
        public List<string> Folders { get; set; } = new List<string>();
        public LauncherSettings Settings { get; set; } = new LauncherSettings();
    }
}
