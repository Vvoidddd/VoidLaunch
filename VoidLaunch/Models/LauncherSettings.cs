using System.Collections.Generic;

namespace VoidLaunch.Models
{
    public sealed class LauncherSettings
    {
        public string ThemeName { get; set; } = "Void Purple";
        public string ThemeCode { get; set; } = string.Empty;
        public List<SavedTheme> SavedThemes { get; set; } = new List<SavedTheme>();
    }
}
