using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VoidLaunch.Models;
using VoidLaunch.Services;

namespace VoidLaunch
{
    public sealed class GameSessionEndedEventArgs : EventArgs
    {
        public GameSessionEndedEventArgs(
            string gameId,
            DateTimeOffset startedAt,
            DateTimeOffset endedAt,
            TimeSpan duration,
            int exitCode)
        {
            GameId = gameId;
            StartedAt = startedAt;
            EndedAt = endedAt;
            Duration = duration;
            ExitCode = exitCode;
        }

        public string GameId { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset EndedAt { get; }
        public TimeSpan Duration { get; }
        public int ExitCode { get; }
    }

    public partial class GameLogWindow : Window
    {
        private readonly GameEntry _game;
        private Process? _process;
        private Stopwatch? _sessionStopwatch;
        private DockSide _dockSide;
        private const double DockGap = 6;
        private const double SnapDistance = 42;

        public event EventHandler<GameSessionEndedEventArgs>? SessionEnded;
        public DateTimeOffset? SessionStartedAt { get; private set; }

        private enum DockSide
        {
            None,
            Left,
            Right
        }

        public GameLogWindow(GameEntry game)
        {
            InitializeComponent();

            ThemeService.ApplyTo(Resources);

            _game = game;
            Title = $"{game.Name} - Game Log";
            GameNameText.Text = game.Name;
            StatusText.Text = "Preparing to launch...";

            Loaded += GameLogWindow_Loaded;
            Closed += GameLogWindow_Closed;
        }

        public async Task<bool> StartAsync()
        {
            // Give WPF a render pass so the log window is visible before the
            // game creates its own window and potentially takes focus.
            await Dispatcher.Yield(DispatcherPriority.Render);

            // Explorer starts an executable from the folder that contains it.
            // Using that folder (rather than the scanned game root) is important
            // for games that load DLLs and configuration through relative paths.
            var workingDirectory = Path.GetDirectoryName(_game.ExecutablePath)
                ?? string.Empty;

            AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Launching: {_game.ExecutablePath}");
            AppendLog($"Working directory: {workingDirectory}");
            AppendLog(string.Empty);

            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _game.ExecutablePath,
                        WorkingDirectory = workingDirectory,
                        Verb = "open",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    },
                    EnableRaisingEvents = true
                };

                if (!_process.Start())
                    throw new InvalidOperationException("Windows did not start the game process.");

                SessionStartedAt = DateTimeOffset.UtcNow;
                _sessionStopwatch = Stopwatch.StartNew();
                StatusText.Text = $"Running (process {_process.Id})";
                AppendLog($"Process started with ID {_process.Id}.");
                AppendLog("Started through Windows Shell (the same launch path as double-clicking the executable).");
                AppendLog("Console output belongs to the game and is not redirected into VoidLaunch.");
                _ = ObserveExitAsync(_process);
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Launch failed";
                AppendLog(string.Empty);
                AppendLog($"LAUNCH ERROR: {ex}");
                await Task.CompletedTask;
                return false;
            }
        }

        private async Task ObserveExitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync();
                _sessionStopwatch?.Stop();
                DateTimeOffset endedAt = DateTimeOffset.UtcNow;
                DateTimeOffset startedAt = SessionStartedAt ?? endedAt;
                TimeSpan duration = _sessionStopwatch?.Elapsed ?? endedAt - startedAt;
                int exitCode = process.ExitCode;

                await Dispatcher.InvokeAsync(() =>
                {
                    AppendLog(string.Empty);
                    AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Process exited with code {exitCode} (0x{exitCode:X8}).");
                    AppendLog($"Session time: {FormatDuration(duration)}.");
                    StatusText.Text = exitCode == 0
                        ? $"Exited normally · {FormatDuration(duration)} played"
                        : $"Exited with error code {exitCode} · {FormatDuration(duration)} played";

                    SessionEnded?.Invoke(
                        this,
                        new GameSessionEndedEventArgs(
                            _game.Id,
                            startedAt,
                            endedAt,
                            duration,
                            exitCode));
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = "Could not monitor process";
                    AppendLog($"MONITOR ERROR: {ex}");
                });
            }
            finally
            {
                process.Dispose();
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 1)
                return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} sec";

            if (duration.TotalHours < 1)
                return $"{(int)duration.TotalMinutes} min";

            int hours = (int)duration.TotalHours;
            return duration.Minutes == 0
                ? $"{hours} hr"
                : $"{hours} hr {duration.Minutes} min";
        }

        private void AppendLog(string text)
        {
            LogTextBox.AppendText(text + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(LogTextBox.Text))
                System.Windows.Clipboard.SetText(LogTextBox.Text);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void GameLogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Owner is not Window owner)
                return;

            owner.LocationChanged += Owner_LocationChanged;
            owner.SizeChanged += Owner_SizeChanged;
            DockInitially(owner);
        }

        private void GameLogWindow_Closed(object? sender, EventArgs e)
        {
            if (Owner is not Window owner)
                return;

            owner.LocationChanged -= Owner_LocationChanged;
            owner.SizeChanged -= Owner_SizeChanged;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
                return;

            SetDockSide(DockSide.None);

            try
            {
                DragMove();
                TrySnapToOwner();
            }
            catch
            {
                // DragMove can be interrupted if mouse capture changes.
            }
        }

        private void DockInitially(Window owner)
        {
            Height = owner.ActualHeight;
            var workArea = SystemParameters.WorkArea;
            var combinedWidth = owner.ActualWidth + DockGap + ActualWidth;

            // Keep the combined pair on screen when there is enough room.
            if (combinedWidth <= workArea.Width)
            {
                owner.Left = Math.Clamp(
                    owner.Left,
                    workArea.Left,
                    workArea.Right - combinedWidth);
            }

            SetDockSide(DockSide.Right);
            PositionBesideOwner();
        }

        private void TrySnapToOwner()
        {
            if (Owner is not Window owner)
                return;

            var rightDockLeft = owner.Left + owner.ActualWidth + DockGap;
            var leftDockLeft = owner.Left - ActualWidth - DockGap;
            var verticallyNear = Top < owner.Top + owner.ActualHeight + SnapDistance
                && Top + ActualHeight > owner.Top - SnapDistance;

            if (!verticallyNear)
                return;

            var rightDistance = Math.Abs(Left - rightDockLeft);
            var leftDistance = Math.Abs(Left - leftDockLeft);

            if (Math.Min(rightDistance, leftDistance) > SnapDistance)
                return;

            SetDockSide(rightDistance <= leftDistance ? DockSide.Right : DockSide.Left);
            PositionBesideOwner();
        }

        private void SetDockSide(DockSide side)
        {
            _dockSide = side;
            DockStatusText.Text = side switch
            {
                DockSide.Left => "Attached to launcher · drag to detach",
                DockSide.Right => "Attached to launcher · drag to detach",
                _ => "Drag near the launcher to attach"
            };
        }

        private void Owner_LocationChanged(object? sender, EventArgs e)
        {
            PositionBesideOwner();
        }

        private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_dockSide == DockSide.None)
                return;

            Height = e.NewSize.Height;
            PositionBesideOwner();
        }

        private void PositionBesideOwner()
        {
            if (_dockSide == DockSide.None || Owner is not Window owner || owner.WindowState != WindowState.Normal)
                return;

            Top = owner.Top;
            Left = _dockSide == DockSide.Right
                ? owner.Left + owner.ActualWidth + DockGap
                : owner.Left - ActualWidth - DockGap;
        }
    }
}
