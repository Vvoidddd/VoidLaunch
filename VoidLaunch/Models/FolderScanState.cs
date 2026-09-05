using System;

namespace VoidLaunch.Models
{
    public sealed class FolderScanState
    {
        public string Path { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public DateTime LastFullScanUtc { get; set; }
    }
}
