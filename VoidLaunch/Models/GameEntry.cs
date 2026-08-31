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
        public bool IsFavorite { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime DateAdded { get; set; } = DateTime.Now;
    }
}
