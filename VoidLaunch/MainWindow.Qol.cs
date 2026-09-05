using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VoidLaunch.Models;
using VoidLaunch.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace VoidLaunch
{
    public partial class MainWindow
    {
        private CancellationTokenSource? _scanCancellation;
        private bool _isScanning;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _trayIconImage;
        private string? _editingLaunchProfileId;
        private bool _editingProfileRunsAsAdministrator;
        private string? _lastCrashLogPath;

        private static readonly string[] LibrarySortModes =
        {
            "Name",
            "Recently played",
            "Most played",
            "Date added"
        };

        private void InitializeQolFeatures()
        {
            try
            {
                _trayIconImage = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                    ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
                    : null;
                _trayIconImage ??= System.Drawing.SystemIcons.Application;

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open VoidLaunch", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
                {
                    _allowCloseWithRunningGames = true;
                    Show();
                    Close();
                }));

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = _trayIconImage,
                    Text = "VoidLaunch",
                    ContextMenuStrip = menu,
                    Visible = false
                };
                _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
            }
            catch
            {
                _trayIcon = null;
            }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            UpdateTrayStatus();
        }

        private void UpdateTrayStatus()
        {
            if (_trayIcon is null)
                return;

            int running = _activeGameSessions.Values.Sum();
            _trayIcon.Visible = running > 0 || !IsVisible;
            _trayIcon.Text = running == 0
                ? "VoidLaunch"
                : $"VoidLaunch - {running} game{(running == 1 ? string.Empty : "s")} running";
        }

        private void ShowTrayBalloon(string title, string message)
        {
            if (_trayIcon is null)
                return;

            _trayIcon.Visible = true;
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }

        private void DisposeTrayIcon()
        {
            if (_trayIcon is null)
                return;

            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
            if (!ReferenceEquals(_trayIconImage, System.Drawing.SystemIcons.Application))
                _trayIconImage?.Dispose();
            _trayIconImage = null;
        }

        private void NormalizeGameEntry(GameEntry game)
        {
            if (string.IsNullOrWhiteSpace(game.Id))
                game.Id = Guid.NewGuid().ToString("N");

            game.TotalPlayTimeSeconds = Math.Max(0, game.TotalPlayTimeSeconds);
            game.LastSessionDurationSeconds = Math.Max(0, game.LastSessionDurationSeconds);
            game.LaunchCount = Math.Max(0, game.LaunchCount);
            game.ExecutablePaths ??= new List<string>();
            game.PlaySessions ??= new List<PlaySession>();
            game.LaunchProfiles ??= new List<LaunchProfile>();

            game.PlaySessions = game.PlaySessions
                .Where(session => session is not null)
                .Select(session =>
                {
                    if (string.IsNullOrWhiteSpace(session.Id))
                        session.Id = Guid.NewGuid().ToString("N");
                    session.DurationSeconds = Math.Max(0, session.DurationSeconds);
                    if (session.EndedAt == default && session.StartedAt != default)
                        session.EndedAt = session.StartedAt.AddSeconds(session.DurationSeconds);
                    return session;
                })
                .OrderByDescending(session => session.StartedAt)
                .Take(500)
                .ToList();

            bool keepManualExecutable =
                game.ExecutableManuallySelected && File.Exists(game.ExecutablePath);

            game.ExecutablePaths = game.ExecutablePaths
                .Append(game.ExecutablePath)
                .Where(path =>
                    GameScanner.IsPotentialGameExecutable(path) ||
                    (keepManualExecutable && string.Equals(
                        path,
                        game.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(GameScanner.GetExecutableScore)
                .ToList();

            if (!keepManualExecutable && !GameScanner.IsPotentialGameExecutable(game.ExecutablePath))
            {
                game.ExecutableManuallySelected = false;
                game.ExecutablePath = game.ExecutablePaths.FirstOrDefault() ?? string.Empty;
            }

            game.LaunchProfiles = game.LaunchProfiles
                .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.ExecutablePath))
                .Select(profile =>
                {
                    if (string.IsNullOrWhiteSpace(profile.Id))
                        profile.Id = Guid.NewGuid().ToString("N");
                    profile.Name = string.IsNullOrWhiteSpace(profile.Name)
                        ? "Launch profile"
                        : profile.Name.Trim();
                    if (string.IsNullOrWhiteSpace(profile.WorkingDirectory))
                        profile.WorkingDirectory = Path.GetDirectoryName(profile.ExecutablePath) ?? string.Empty;
                    return profile;
                })
                .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (game.LaunchProfiles.Count == 0)
            {
                game.LaunchProfiles.Add(new LaunchProfile
                {
                    Name = "Default",
                    ExecutablePath = game.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty
                });
            }

            if (!game.LaunchProfiles.Any(profile =>
                    string.Equals(profile.Id, game.SelectedLaunchProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                game.SelectedLaunchProfileId = game.LaunchProfiles[0].Id;
            }

            game.Name = GameNameFormatter.CleanDisplayName(game.Name, game.ExecutablePath);
        }

        private LaunchProfile GetSelectedLaunchProfile(GameEntry game)
        {
            NormalizeGameEntry(game);
            return game.LaunchProfiles.FirstOrDefault(profile =>
                       string.Equals(profile.Id, game.SelectedLaunchProfileId, StringComparison.OrdinalIgnoreCase))
                   ?? game.LaunchProfiles[0];
        }

        private void SyncSelectedProfileExecutable(GameEntry game, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(_editingLaunchProfileId))
                return;

            LaunchProfile profile = game.LaunchProfiles.FirstOrDefault(item =>
                                        string.Equals(item.Id, _editingLaunchProfileId, StringComparison.OrdinalIgnoreCase))
                                    ?? GetSelectedLaunchProfile(game);
            profile.ExecutablePath = executablePath;
            if (string.IsNullOrWhiteSpace(profile.WorkingDirectory) || !Directory.Exists(profile.WorkingDirectory))
                profile.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        }

        private void UpdateLibraryControls()
        {
            if (SortLibraryButton is null)
                return;

            SortLibraryButton.Content = $"Sort: {_library.Settings.LibrarySortMode}";
            ViewModeButton.Content = _library.Settings.CompactLibraryView ? "Grid view" : "Compact view";
            ScanReviewCountText.Text = _library.PendingGames.Count > 0
                ? _library.PendingGames.Count.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            ScanReviewCountBadge.Visibility = _library.PendingGames.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            TrayModeButton.Content = _library.Settings.CloseToTrayWhilePlaying
                ? "Close-to-tray: On"
                : "Close-to-tray: Off";
            StorageModeText.Text = _libraryService.IsPortableMode
                ? $"Portable mode · {_libraryService.DataDirectory}"
                : $"App-data mode · {_libraryService.DataDirectory}";
        }

        private async void SortLibrary_Click(object sender, RoutedEventArgs e)
        {
            int index = Array.FindIndex(
                LibrarySortModes,
                mode => string.Equals(mode, _library.Settings.LibrarySortMode, StringComparison.OrdinalIgnoreCase));
            _library.Settings.LibrarySortMode = LibrarySortModes[(index + 1 + LibrarySortModes.Length) % LibrarySortModes.Length];
            await _libraryService.SaveAsync(_library);
            RefreshLibrary();
        }

        private async void ToggleLibraryView_Click(object sender, RoutedEventArgs e)
        {
            _library.Settings.CompactLibraryView = !_library.Settings.CompactLibraryView;
            await _libraryService.SaveAsync(_library);
            RefreshLibrary();
        }

        private async void RefreshScan_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning)
            {
                _scanCancellation?.Cancel();
                return;
            }

            await RescanAllFoldersAsync(true);
        }

        private Border CreateCompactGameCard(GameEntry game)
        {
            var card = new Border
            {
                Width = 360,
                Height = 96,
                Margin = new Thickness(0, 0, 14, 14),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8),
                Cursor = Cursors.Hand
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cover = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true
            };
            cover.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
            cover.Child = (UIElement?)LoadArtwork(game.ImagePath) ?? CreateImagePlaceholder(game.Name);
            grid.Children.Add(cover);

            var text = new StackPanel
            {
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new TextBlock
            {
                Text = game.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var stats = new TextBlock
            {
                Text = game.TotalPlayTimeSeconds > 0
                    ? $"{FormatPlayTime(game.TotalPlayTimeSeconds)} played"
                    : "Not played yet",
                Margin = new Thickness(0, 5, 0, 0),
                FontSize = 11
            };
            stats.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            text.Children.Add(title);
            text.Children.Add(stats);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var play = new Button
            {
                Content = "▶ Play",
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            play.Click += (_, args) =>
            {
                args.Handled = true;
                LaunchGame(game);
            };
            var details = new Button
            {
                Content = "Details",
                Style = (Style)FindResource("SecondaryButton")
            };
            details.Click += (_, args) =>
            {
                args.Handled = true;
                ShowGameDetails(game);
            };
            buttons.Children.Add(play);
            buttons.Children.Add(details);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            card.Child = grid;
            card.MouseLeftButtonUp += (_, _) => ShowGameDetails(game);
            return card;
        }

        private void RebuildGameDetailsQol(GameEntry game)
        {
            DetailsCoverStatusText.Text = game.ImageManuallySelected
                ? "Using your custom cover"
                : "Using artwork detected by VoidLaunch";
            RebuildLaunchProfiles(game);
            RebuildSessionHistory(game);
        }

        private void RebuildSessionHistory(GameEntry game)
        {
            if (DetailsSessionHistory is null)
                return;

            DetailsSessionHistory.Children.Clear();
            game.PlaySessions ??= new List<PlaySession>();
            DetailsSessionEmptyText.Visibility = game.PlaySessions.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            foreach (PlaySession session in game.PlaySessions.OrderByDescending(item => item.StartedAt).Take(50))
            {
                var row = new Border
                {
                    Margin = new Thickness(0, 0, 0, 7),
                    Padding = new Thickness(11, 9, 8, 9),
                    CornerRadius = new CornerRadius(9),
                    BorderThickness = new Thickness(1)
                };
                row.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = $"{session.StartedAt:MMM d, yyyy · h:mm tt}  ·  {FormatPlayTime(session.DurationSeconds)}" +
                           (session.ExitCode == 0 ? string.Empty : $"  ·  exit {session.ExitCode}"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                label.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    session.ExitCode == 0 ? "TextBrush" : "ErrorBrush");
                grid.Children.Add(label);
                var remove = new Button
                {
                    Content = "Remove",
                    Margin = new Thickness(10, 0, 0, 0),
                    Style = (Style)FindResource("SecondaryButton")
                };
                remove.Click += async (_, _) =>
                {
                    if (_selectedGame is null)
                        return;
                    _selectedGame.PlaySessions.RemoveAll(item =>
                        string.Equals(item.Id, session.Id, StringComparison.OrdinalIgnoreCase));
                    _selectedGame.TotalPlayTimeSeconds = Math.Max(
                        0,
                        _selectedGame.TotalPlayTimeSeconds - Math.Max(0, session.DurationSeconds));
                    await _libraryService.SaveAsync(_library);
                    UpdateGameDetailsStats(_selectedGame);
                    RefreshLibrary();
                };
                Grid.SetColumn(remove, 1);
                grid.Children.Add(remove);
                row.Child = grid;
                DetailsSessionHistory.Children.Add(row);
            }
        }

        private async void SetManualPlaytime_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            string text = DetailsManualPlaytimeBox.Text.Trim();
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double hours) ||
                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hours);
            if (!parsed || double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0 || hours > 1_000_000)
            {
                MessageBox.Show(this, "Enter a valid total number of hours, such as 12.5.", "Invalid playtime");
                return;
            }

            _selectedGame.TotalPlayTimeSeconds = (long)Math.Round(hours * 3600d);
            DetailsManualPlaytimeBox.Clear();
            await _libraryService.SaveAsync(_library);
            UpdateGameDetailsStats(_selectedGame);
            RefreshLibrary();
        }

        private async void ChooseCover_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            var dialog = new OpenFileDialog
            {
                Title = $"Choose cover art for {_selectedGame.Name}",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                _selectedGame.ImagePath = await _libraryService.ImportCoverAsync(_selectedGame.Id, dialog.FileName);
                _selectedGame.ImageManuallySelected = true;
                await _libraryService.SaveAsync(_library);
                RebuildGameDetailsQol(_selectedGame);
                RefreshLibrary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Cover art", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ResetCover_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            _selectedGame.ImageManuallySelected = false;
            _selectedGame.ImagePath = GameScanner.FindBestArtwork(
                _selectedGame.InstallDirectory,
                _selectedGame.ExecutablePath);
            await _libraryService.SaveAsync(_library);
            RebuildGameDetailsQol(_selectedGame);
            RefreshLibrary();
        }

        private void RebuildLaunchProfiles(GameEntry game)
        {
            LaunchProfilesList.Children.Clear();
            NormalizeGameEntry(game);
            foreach (LaunchProfile profile in game.LaunchProfiles)
            {
                var button = new Button
                {
                    Content = string.Equals(profile.Id, game.SelectedLaunchProfileId, StringComparison.OrdinalIgnoreCase)
                        ? $"✓  {profile.Name}"
                        : profile.Name,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 7, 7),
                    Style = (Style)FindResource(
                        string.Equals(profile.Id, game.SelectedLaunchProfileId, StringComparison.OrdinalIgnoreCase)
                            ? "PrimaryButton"
                            : "SecondaryButton")
                };
                button.Click += async (_, _) =>
                {
                    if (_selectedGame is null)
                        return;
                    _selectedGame.SelectedLaunchProfileId = profile.Id;
                    _selectedGame.ExecutablePath = profile.ExecutablePath;
                    _selectedGame.ExecutableManuallySelected = true;
                    DetailsExecutableBox.Text = profile.ExecutablePath;
                    LoadLaunchProfileEditor(profile);
                    RebuildLaunchProfiles(_selectedGame);
                    RebuildExecutableChoices();
                    await _libraryService.SaveAsync(_library);
                };
                LaunchProfilesList.Children.Add(button);
            }

            LaunchProfile selected = GetSelectedLaunchProfile(game);
            if (string.IsNullOrWhiteSpace(_editingLaunchProfileId) ||
                !game.LaunchProfiles.Any(profile =>
                    string.Equals(profile.Id, _editingLaunchProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                LoadLaunchProfileEditor(selected);
            }
        }

        private void LoadLaunchProfileEditor(LaunchProfile profile)
        {
            _editingLaunchProfileId = profile.Id;
            _editingProfileRunsAsAdministrator = profile.RunAsAdministrator;
            LaunchProfileNameBox.Text = profile.Name;
            LaunchArgumentsBox.Text = profile.Arguments;
            LaunchWorkingDirectoryBox.Text = profile.WorkingDirectory;
            UpdateLaunchAdminButton();
        }

        private void NewLaunchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            _editingLaunchProfileId = null;
            _editingProfileRunsAsAdministrator = false;
            LaunchProfileNameBox.Text = "New profile";
            LaunchArgumentsBox.Clear();
            LaunchWorkingDirectoryBox.Text = Path.GetDirectoryName(_selectedGame.ExecutablePath) ?? string.Empty;
            UpdateLaunchAdminButton();
            LaunchProfileNameBox.Focus();
            LaunchProfileNameBox.SelectAll();
        }

        private async void SaveLaunchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            string name = LaunchProfileNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "Give this launch profile a name.", "Launch profile");
                return;
            }

            LaunchProfile? profile = _selectedGame.LaunchProfiles.FirstOrDefault(item =>
                string.Equals(item.Id, _editingLaunchProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                profile = new LaunchProfile();
                _selectedGame.LaunchProfiles.Add(profile);
            }

            profile.Name = name.Length > 40 ? name[..40] : name;
            profile.ExecutablePath = _selectedGame.ExecutablePath;
            profile.Arguments = LaunchArgumentsBox.Text.Trim();
            profile.WorkingDirectory = LaunchWorkingDirectoryBox.Text.Trim();
            profile.RunAsAdministrator = _editingProfileRunsAsAdministrator;
            _selectedGame.SelectedLaunchProfileId = profile.Id;
            _editingLaunchProfileId = profile.Id;
            await _libraryService.SaveAsync(_library);
            RebuildLaunchProfiles(_selectedGame);
        }

        private async void DeleteLaunchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null || _selectedGame.LaunchProfiles.Count <= 1)
                return;

            LaunchProfile? profile = _selectedGame.LaunchProfiles.FirstOrDefault(item =>
                string.Equals(item.Id, _editingLaunchProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                return;

            _selectedGame.LaunchProfiles.Remove(profile);
            _selectedGame.SelectedLaunchProfileId = _selectedGame.LaunchProfiles[0].Id;
            _editingLaunchProfileId = null;
            await _libraryService.SaveAsync(_library);
            RebuildLaunchProfiles(_selectedGame);
        }

        private void ToggleLaunchAdmin_Click(object sender, RoutedEventArgs e)
        {
            _editingProfileRunsAsAdministrator = !_editingProfileRunsAsAdministrator;
            UpdateLaunchAdminButton();
        }

        private void UpdateLaunchAdminButton()
        {
            LaunchAdminButton.Content = _editingProfileRunsAsAdministrator
                ? "Run as administrator: On"
                : "Run as administrator: Off";
        }

        private void ChooseWorkingDirectory_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose the working folder for this launch profile",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                LaunchWorkingDirectoryBox.Text = dialog.SelectedPath;
        }

        private void ScanReview_Click(object sender, RoutedEventArgs e)
        {
            BuildScanReview();
            ShowPage(ScanReviewPage);
        }

        private void BuildScanReview()
        {
            ScanReviewList.Children.Clear();
            ScanReviewEmptyText.Visibility = _library.PendingGames.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            foreach (GameEntry game in _library.PendingGames.ToList())
            {
                var card = new Border
                {
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(16),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1)
                };
                card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var copy = new StackPanel();
                var title = new TextBlock
                {
                    Text = game.Name,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var path = new TextBlock
                {
                    Text = game.ExecutablePath,
                    Margin = new Thickness(0, 5, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                path.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
                copy.Children.Add(title);
                copy.Children.Add(path);
                grid.Children.Add(copy);

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(14, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var ignore = new Button
                {
                    Content = "Ignore",
                    Style = (Style)FindResource("SecondaryButton")
                };
                ignore.Click += async (_, _) => await IgnorePendingGameAsync(game);
                var add = new Button
                {
                    Content = "Add game",
                    Margin = new Thickness(8, 0, 0, 0),
                    Style = (Style)FindResource("PrimaryButton")
                };
                add.Click += async (_, _) => await AcceptPendingGameAsync(game);
                actions.Children.Add(ignore);
                actions.Children.Add(add);
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);
                card.Child = grid;
                ScanReviewList.Children.Add(card);
            }
            UpdateLibraryControls();
        }

        private async Task AcceptPendingGameAsync(GameEntry game)
        {
            _library.PendingGames.Remove(game);
            _library.Games.Add(game);
            RemoveDuplicateGames();
            await _libraryService.SaveAsync(_library);
            BuildScanReview();
            UpdateFolderText();
            RefreshLibrary();
        }

        private async Task IgnorePendingGameAsync(GameEntry game)
        {
            _library.PendingGames.Remove(game);
            string path = NormalizePath(game.InstallDirectory);
            if (!string.IsNullOrWhiteSpace(path) &&
                !_library.IgnoredScanPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                _library.IgnoredScanPaths.Add(path);
            await _libraryService.SaveAsync(_library);
            BuildScanReview();
            UpdateFolderText();
        }

        private async void AcceptAllPending_Click(object sender, RoutedEventArgs e)
        {
            foreach (GameEntry game in _library.PendingGames.ToList())
                _library.Games.Add(game);
            _library.PendingGames.Clear();
            RemoveDuplicateGames();
            await _libraryService.SaveAsync(_library);
            BuildScanReview();
            UpdateFolderText();
            RefreshLibrary();
        }

        private async void ClearIgnored_Click(object sender, RoutedEventArgs e)
        {
            _library.IgnoredScanPaths.Clear();
            await _libraryService.SaveAsync(_library);
            MessageBox.Show(this, "Ignored scan entries were cleared. Run a refresh to review them again.", "Scan review");
        }

        private async void RemoveAndIgnoreGame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            MessageBoxResult result = MessageBox.Show(
                this,
                $"Remove {_selectedGame.Name} and ignore this install folder during future scans?",
                "Remove and ignore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            string path = NormalizePath(_selectedGame.InstallDirectory);
            _library.Games.Remove(_selectedGame);
            if (!string.IsNullOrWhiteSpace(path) &&
                !_library.IgnoredScanPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                _library.IgnoredScanPaths.Add(path);
            _selectedGame = null;
            await _libraryService.SaveAsync(_library);
            ShowPage(LibraryPage);
            RefreshLibrary();
        }

        private async void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Back up VoidLaunch",
                Filter = "VoidLaunch backup (*.voidbackup)|*.voidbackup",
                FileName = $"VoidLaunch-backup-{DateTime.Now:yyyy-MM-dd}.voidbackup",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                await _libraryService.CreateBackupAsync(dialog.FileName, _library);
                MessageBox.Show(this, "Your library, settings, playtime, profiles, themes, and custom covers were backed up.", "Backup complete");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Restore a VoidLaunch backup",
                Filter = "VoidLaunch backup (*.voidbackup)|*.voidbackup",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            if (MessageBox.Show(this, "Replace the current library and settings with this backup?", "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                _library = await _libraryService.RestoreBackupAsync(dialog.FileName);
                NormalizeLibraryData();
                ApplySavedTheme();
                BuildThemeColorEditors();
                BuildThemeButtons();
                await _libraryService.SaveAsync(_library);
                UpdateFolderText();
                RefreshLibrary();
                MessageBox.Show(this, "The backup was restored.", "Restore complete");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void TogglePortableMode_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameSessions.Values.Sum() > 0)
            {
                MessageBox.Show(this, "Close your running games before changing storage mode.", "Portable mode");
                return;
            }

            bool enable = !_libraryService.IsPortableMode;
            string message = enable
                ? "Move a copy of your data beside the VoidLaunch EXE and restart in portable mode?"
                : "Move a copy of your data back to AppData and restart in normal mode?";
            if (MessageBox.Show(this, message, "Change storage mode", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                await _libraryService.SetPortableModeAsync(enable, _library);
                _allowCloseWithRunningGames = true;
                string executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("VoidLaunch could not determine its EXE path.");
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Portable mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ToggleTrayMode_Click(object sender, RoutedEventArgs e)
        {
            _library.Settings.CloseToTrayWhilePlaying = !_library.Settings.CloseToTrayWhilePlaying;
            await _libraryService.SaveAsync(_library);
            UpdateLibraryControls();
        }

        private void ShowCrashNotification(GameEntry game, GameSessionEndedEventArgs session)
        {
            _lastCrashLogPath = session.LogFilePath;
            CrashNotificationTitleText.Text = $"{game.Name} closed unexpectedly";
            CrashNotificationDetailsText.Text =
                $"Exit code {session.ExitCode} (0x{session.ExitCode:X8}) · played {FormatPlayTime((long)Math.Max(0, session.Duration.TotalSeconds))}";
            CrashNotificationOverlay.Visibility = Visibility.Visible;
            CrashNotificationOverlay.Opacity = 1;
            MainWindowShell.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 10 };

            if (!IsVisible)
            {
                ShowTrayBalloon(
                    $"{game.Name} closed unexpectedly",
                    $"Exit code {session.ExitCode}. Open VoidLaunch to view or copy the crash log.");
            }
        }

        private void DismissCrashNotification_Click(object sender, RoutedEventArgs e)
        {
            CrashNotificationOverlay.Visibility = Visibility.Collapsed;
            CrashNotificationOverlay.Opacity = 0;
            if (UpdateNotificationOverlay.Visibility != Visibility.Visible)
                MainWindowShell.Effect = null;
        }

        private void OpenCrashLog_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastCrashLogPath) || !File.Exists(_lastCrashLogPath))
                return;
            Process.Start(new ProcessStartInfo(_lastCrashLogPath) { UseShellExecute = true, Verb = "open" });
        }

        private void CopyCrashDetails_Click(object sender, RoutedEventArgs e)
        {
            string text = CrashNotificationTitleText.Text + Environment.NewLine +
                          CrashNotificationDetailsText.Text;
            if (!string.IsNullOrWhiteSpace(_lastCrashLogPath) && File.Exists(_lastCrashLogPath))
                text += Environment.NewLine + Environment.NewLine + File.ReadAllText(_lastCrashLogPath);
            Clipboard.SetText(text);
            CrashNotificationDetailsText.Text += " · copied";
        }
    }
}
