using System;

namespace VoidLaunch.Models
{
    public sealed class PlaySession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public long DurationSeconds { get; set; }
        public int ExitCode { get; set; }
    }
}
