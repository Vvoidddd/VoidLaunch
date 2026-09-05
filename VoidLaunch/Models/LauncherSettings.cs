using System.Collections.Generic;

namespace VoidLaunch.Models
{
    public sealed class LauncherSettings
    {
        public string ThemeName { get; set; } = "Void Purple";
        public string ThemeCode { get; set; } = string.Empty;
        public List<SavedTheme> SavedThemes { get; set; } = new List<SavedTheme>();
        public string LibrarySortMode { get; set; } = "Name";
        public bool CompactLibraryView { get; set; }
        public bool CloseToTrayWhilePlaying { get; set; } = true;
        public List<FolderScanState> FolderScanStates { get; set; } = new List<FolderScanState>();
    }
}
