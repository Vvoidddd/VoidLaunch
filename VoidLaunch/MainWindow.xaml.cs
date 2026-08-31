using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VoidLaunch.Models;
using VoidLaunch.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;

namespace VoidLaunch
{
    public partial class MainWindow : Window
    {
        private readonly LibraryService _libraryService;
        private readonly GameScanner _scanner;
        private readonly UpdateService _updateService;

        private LibraryData _library =
            new LibraryData();

        private bool _showFavorites;
        private bool _showRecent;

        private bool _isRefreshing;
        private GameEntry? _selectedGame;
        private UpdateCheckResult? _latestUpdate;

        public MainWindow()
        {
            InitializeComponent();

            _libraryService =
                new LibraryService();

            _scanner =
                new GameScanner();

            _updateService =
                new UpdateService();

            Loaded +=
                MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            await LoadLibraryAsync();
            await CheckForUpdatesAsync();
        }


        // ============================================================
        // STARTUP
        // ============================================================

        private async Task LoadLibraryAsync()
        {
            _library =
                await _libraryService.LoadAsync();

            NormalizeLibraryData();
            ApplySavedTheme();
            BuildThemeButtons();

            UpdateFolderText();

            RefreshLibrary();

            // Automatically rescan every configured folder.
            await RescanAllFoldersAsync();
        }

        private async Task RescanAllFoldersAsync()
        {
            if (_library.Folders.Count == 0)
                return;

            try
            {
                GameFolderText.Text =
                    "Checking game folders...";

                var scannedGames =
                    new List<GameEntry>();

                foreach (string folder in
                         _library.Folders.ToList())
                {
                    if (!Directory.Exists(folder))
                        continue;

                    var progress =
                        new Progress<int>(
                            value =>
                            {
                                GameFolderText.Text =
                                    $"Scanning {Path.GetFileName(folder)}... {value}%";
                            });

                    List<GameEntry> found =
                        await _scanner.ScanAsync(
                            folder,
                            progress);

                    scannedGames.AddRange(found);
                }

                MergeScannedGames(
                    scannedGames);

                RemoveDuplicateGames();

                RemoveMissingGamesFromConfiguredFolders();

                await _libraryService
                    .SaveAsync(_library);

                UpdateFolderText();
                RefreshLibrary();
            }
            catch (Exception ex)
            {
                GameFolderText.Text =
                    "Library scan failed.";

                System.Windows.MessageBox.Show(
                    $"VoidLaunch could not refresh the game library.\n\n{ex.Message}",
                    "Library Scan Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }


        // ============================================================
        // SCAN / MERGE
        // ============================================================

        private void MergeScannedGames(
            IEnumerable<GameEntry> scannedGames)
        {
            foreach (GameEntry scanned in scannedGames)
            {
                string executablePath =
                    NormalizePath(
                        scanned.ExecutablePath);

                string installDirectory =
                    NormalizePath(
                        scanned.InstallDirectory);

                scanned.ExecutablePaths ??= new List<string>();
                if (!scanned.ExecutablePaths.Any(x =>
                        string.Equals(NormalizePath(x), executablePath, StringComparison.OrdinalIgnoreCase)))
                {
                    scanned.ExecutablePaths.Insert(0, scanned.ExecutablePath);
                }

                GameEntry? existing =
                    _library.Games.FirstOrDefault(
                        x =>
                            string.Equals(
                                NormalizePath(x.InstallDirectory),
                                installDirectory,
                                StringComparison.OrdinalIgnoreCase));

                // Migration fallback for older libraries that only stored an EXE.
                if (existing == null)
                {
                    existing =
                        _library.Games.FirstOrDefault(
                            x =>
                                string.Equals(
                                    NormalizePath(x.ExecutablePath),
                                    executablePath,
                                    StringComparison.OrdinalIgnoreCase));
                }

                if (existing == null)
                {
                    scanned.DateAdded =
                        DateTime.Now;

                    _library.Games.Add(
                        scanned);

                    continue;
                }

                // Keep the existing ID.
                // Keep favorite and LastPlayed.
                // Update information discovered by scanner.

                existing.Name = GameNameFormatter.CleanDisplayName(
                    string.IsNullOrWhiteSpace(existing.Name) ? scanned.Name : existing.Name,
                    scanned.ExecutablePath);

                existing.ExecutablePaths ??= new List<string>();
                existing.ExecutablePaths = existing.ExecutablePaths
                    .Concat(scanned.ExecutablePaths)
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!existing.ExecutableManuallySelected || !File.Exists(existing.ExecutablePath))
                    existing.ExecutablePath = scanned.ExecutablePath;

                existing.InstallDirectory =
                    scanned.InstallDirectory;

                // Always update artwork if the new scan
                // found a valid image.
                if (!string.IsNullOrWhiteSpace(
                        scanned.ImagePath) &&
                    File.Exists(
                        scanned.ImagePath))
                {
                    existing.ImagePath =
                        scanned.ImagePath;
                }

                if (existing.DateAdded ==
                    default)
                {
                    existing.DateAdded =
                        scanned.DateAdded;
                }
            }
        }

        private void RemoveDuplicateGames()
        {
            ConsolidateNestedInstallDirectories();

            var unique =
                new Dictionary<string, GameEntry>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (GameEntry game in
                     _library.Games.ToList())
            {
                string executable =
                    NormalizePath(
                        game.ExecutablePath);

                string install =
                    NormalizePath(
                        game.InstallDirectory);

                string key = !string.IsNullOrWhiteSpace(install)
                    ? "DIR|" + install
                    : "EXE|" + executable;

                if (!unique.TryGetValue(
                        key,
                        out GameEntry? existing))
                {
                    unique[key] =
                        game;

                    continue;
                }

                // Merge important user data before deleting
                // the duplicate.
                existing.IsFavorite |=
                    game.IsFavorite;

                existing.ExecutablePaths ??= new List<string>();
                game.ExecutablePaths ??= new List<string>();
                existing.ExecutablePaths = existing.ExecutablePaths
                    .Concat(game.ExecutablePaths)
                    .Append(existing.ExecutablePath)
                    .Append(game.ExecutablePath)
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (game.ExecutableManuallySelected && File.Exists(game.ExecutablePath))
                {
                    existing.ExecutablePath = game.ExecutablePath;
                    existing.ExecutableManuallySelected = true;
                }

                if (game.LastPlayed.HasValue &&
                    (!existing.LastPlayed.HasValue ||
                     game.LastPlayed >
                     existing.LastPlayed))
                {
                    existing.LastPlayed =
                        game.LastPlayed;
                }

                if (string.IsNullOrWhiteSpace(
                        existing.ImagePath) &&
                    !string.IsNullOrWhiteSpace(
                        game.ImagePath))
                {
                    existing.ImagePath =
                        game.ImagePath;
                }
            }

            _library.Games =
                unique.Values
                    .Select(game =>
                    {
                        game.Name = GameNameFormatter.CleanDisplayName(game.Name, game.ExecutablePath);
                        return game;
                    })
                    .OrderBy(
                        x => x.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        private void RemoveMissingGamesFromConfiguredFolders()
        {
            var configuredFolders =
                _library.Folders
                    .Where(
                        Directory.Exists)
                    .Select(
                        NormalizePath)
                    .ToList();

            if (configuredFolders.Count == 0)
                return;

            _library.Games =
                _library.Games
                    .Where(
                        game =>
                        {
                            if (File.Exists(
                                    game.ExecutablePath))
                            {
                                return true;
                            }

                            string install =
                                NormalizePath(
                                    game.InstallDirectory);

                            bool belongsToLibrary =
                                configuredFolders.Any(
                                    folder =>
                                        IsPathInside(
                                            install,
                                            folder));

                            // Only remove missing entries if
                            // they actually belonged to one
                            // of the folders we scan.
                            return !belongsToLibrary;
                        })
                    .ToList();
        }

        private static bool IsPathInside(
            string child,
            string parent)
        {
            if (string.IsNullOrWhiteSpace(child) ||
                string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            string normalizedChild =
                NormalizePath(child);

            string normalizedParent =
                NormalizePath(parent);

            if (string.Equals(
                    normalizedChild,
                    normalizedParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedChild.StartsWith(
                normalizedParent +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(
            string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
        }


        // ============================================================
        // WINDOW
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.LeftButton !=
                MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
                // Ignore drag failures.
            }
        }

        private void Minimize_Click(
            object sender,
            RoutedEventArgs e)
        {
            WindowState =
                WindowState.Minimized;
        }

        private void Close_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }


        // ============================================================
        // ADD FOLDER
        // ============================================================

        private async void AddFolder_Click(
            object sender,
            RoutedEventArgs e)
        {
            string? folder =
                PickFolder();

            if (string.IsNullOrWhiteSpace(folder))
                return;

            bool alreadyExists =
                _library.Folders.Any(
                    x =>
                        string.Equals(
                            NormalizePath(x),
                            NormalizePath(folder),
                            StringComparison.OrdinalIgnoreCase));

            if (!alreadyExists)
            {
                _library.Folders.Add(
                    Path.GetFullPath(folder));
            }

            GameFolderText.Text =
                "Scanning game folder...";

            try
            {
                var progress =
                    new Progress<int>(
                        value =>
                        {
                            GameFolderText.Text =
                                $"Scanning... {value}%";
                        });

                List<GameEntry> found =
                    await _scanner.ScanAsync(
                        folder,
                        progress);

                MergeScannedGames(found);

                RemoveDuplicateGames();

                RemoveMissingGamesFromConfiguredFolders();

                await _libraryService
                    .SaveAsync(_library);

                UpdateFolderText();
                RefreshLibrary();
            }
            catch (Exception ex)
            {
                GameFolderText.Text =
                    "Scan failed.";

                System.Windows.MessageBox.Show(
                    $"VoidLaunch could not scan this folder.\n\n{ex.Message}",
                    "Scanner Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }


        // ============================================================
        // FOLDER PICKER
        // ============================================================

        private static string? PickFolder()
        {
            var dialog =
                new System.Windows.Forms.FolderBrowserDialog
                {
                    Description =
                        "Select your local game library folder",

                    UseDescriptionForTitle =
                        true,

                    ShowNewFolderButton =
                        false
                };

            try
            {
                System.Windows.Forms.DialogResult result =
                    dialog.ShowDialog();

                if (result ==
                    System.Windows.Forms.DialogResult.OK)
                {
                    return dialog.SelectedPath;
                }
            }
            finally
            {
                dialog.Dispose();
            }

            return null;
        }


        // ============================================================
        // LIBRARY
        // ============================================================

        private void RefreshLibrary()
        {
            if (_isRefreshing)
                return;

            _isRefreshing = true;

            try
            {
                GameLibrary.Children.Clear();

                IEnumerable<GameEntry> games =
                    _library.Games;

                if (_showFavorites)
                {
                    games =
                        games.Where(
                            x => x.IsFavorite);
                }

                if (_showRecent)
                {
                    games =
                        games
                            .Where(
                                x => x.LastPlayed.HasValue)
                            .OrderByDescending(
                                x => x.LastPlayed);
                }

                string search =
                    SearchBox?.Text?.Trim()
                    ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(search))
                {
                    games =
                        games.Where(
                            x =>
                                x.Name.Contains(
                                    search,
                                    StringComparison.OrdinalIgnoreCase));
                }

                List<GameEntry> list =
                    games.ToList();

                GameCountText.Text =
                    list.Count == 1
                        ? "1 Game"
                        : $"{list.Count} Games";

                if (list.Count == 0)
                {
                    AddEmptyState();
                    return;
                }

                foreach (GameEntry game in list)
                {
                    GameLibrary.Children.Add(
                        CreateGameCard(game));
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }


        // ============================================================
        // GAME CARD
        // ============================================================

        private Border CreateGameCard(
            GameEntry game)
        {
            var card =
                new Border
                {
                    Width = 238,
                    Height = 282,

                    Margin =
                        new Thickness(
                            0,
                            0,
                            14,
                            14),

                    Background =
                        FindBrush("CardBrush"),

                    BorderBrush =
                        FindBrush("BorderBrush"),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(12),

                    Opacity = 0,

                    RenderTransform =
                        new TranslateTransform(
                            0,
                            10)
                };

            var root =
                new Grid();

            root.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(142)
                });

            root.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });


            // ========================================================
            // ARTWORK
            // ========================================================

            var imageContainer =
                new Border
                {
                    Background =
                        FindBrush("CardHoverBrush"),

                    CornerRadius =
                        new CornerRadius(
                            12,
                            12,
                            0,
                            0),

                    ClipToBounds =
                        true
                };

            Image? artwork =
                LoadArtwork(
                    game.ImagePath);

            if (artwork != null)
            {
                imageContainer.Child =
                    artwork;
            }
            else
            {
                imageContainer.Child =
                    CreateImagePlaceholder(
                        game.Name);
            }

            Grid.SetRow(
                imageContainer,
                0);

            root.Children.Add(
                imageContainer);


            // ========================================================
            // INFO
            // ========================================================

            var info =
                new Grid
                {
                    Margin =
                        new Thickness(
                            13,
                            9,
                            10,
                            9)
                };

            info.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            info.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(37)
                });


            var name =
                new TextBlock
                {
                    Text =
                        game.Name,

                    FontSize =
                        14,

                    FontWeight =
                        FontWeights.SemiBold,

                    Foreground =
                        FindBrush("TextBrush"),

                    TextWrapping =
                        TextWrapping.NoWrap,

                    TextTrimming =
                        TextTrimming.CharacterEllipsis,

                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            Grid.SetRow(
                name,
                0);

            info.Children.Add(name);


            // ========================================================
            // BUTTONS
            // ========================================================

            var buttonGrid =
                new Grid();

            buttonGrid.ColumnDefinitions.Add(
                new ColumnDefinition());

            buttonGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(34)
                });


            // PLAY

            var play =
                new Button
                {
                    Content =
                        "▶  Play",

                    Background =
                        FindBrush("AccentBrush"),

                    Foreground =
                        FindBrush("TextBrush"),

                    BorderThickness =
                        new Thickness(0),

                    FocusVisualStyle =
                        null,

                    Cursor =
                        Cursors.Hand,

                    FontSize =
                        12,

                    Template =
                        CreatePlayButtonTemplate()
                };

            play.Click +=
                (_, _) =>
                    LaunchGame(game);

            Grid.SetColumn(
                play,
                0);

            buttonGrid.Children.Add(
                play);


            // FAVORITE

            var favorite =
                new Button
                {
                    Content =
                        game.IsFavorite
                            ? "★"
                            : "☆",

                    Background =
                        Brushes.Transparent,

                    Foreground =
                        game.IsFavorite
                            ? FindBrush("AccentBrush")
                            : FindBrush("SecondaryBrush"),

                    Style =
                        (Style)FindResource(
                            "FavoriteButton"),

                    ToolTip =
                        game.IsFavorite
                            ? "Remove from favorites"
                            : "Add to favorites"
                };

            favorite.Click +=
                async (_, _) =>
                {
                    game.IsFavorite =
                        !game.IsFavorite;

                    await _libraryService
                        .SaveAsync(_library);

                    RefreshLibrary();
                };

            Grid.SetColumn(
                favorite,
                1);

            buttonGrid.Children.Add(
                favorite);


            Grid.SetRow(
                buttonGrid,
                1);

            info.Children.Add(
                buttonGrid);


            Grid.SetRow(
                info,
                1);

            root.Children.Add(info);

            card.Child =
                root;

            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null)
                    return;

                ShowGameDetails(game);
            };


            // ========================================================
            // HOVER
            // ========================================================

            card.MouseEnter +=
                (_, _) =>
                {
                    AnimateCard(
                        card,
                        -4,
                        150);

                    card.Background =
                        FindBrush("CardHoverBrush");
                };

            card.MouseLeave +=
                (_, _) =>
                {
                    AnimateCard(
                        card,
                        0,
                        180);

                    card.Background =
                        FindBrush("CardBrush");
                };


            AnimateEntrance(card);

            return card;
        }


        // ============================================================
        // PLAY BUTTON TEMPLATE
        // ============================================================

        private ControlTemplate CreatePlayButtonTemplate()
        {
            var template =
                new ControlTemplate(
                    typeof(Button));

            var border =
                new FrameworkElementFactory(
                    typeof(Border));

            border.Name =
                "PlayBorder";

            border.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(8));

            border.SetBinding(
                Border.BackgroundProperty,
                new System.Windows.Data.Binding(
                    "Background")
                {
                    RelativeSource =
                        new System.Windows.Data.RelativeSource(
                            System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });

            var content =
                new FrameworkElementFactory(
                    typeof(ContentPresenter));

            content.SetValue(
                ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);

            content.SetValue(
                ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);

            content.SetBinding(
                ContentPresenter.ContentProperty,
                new System.Windows.Data.Binding(
                    "Content")
                {
                    RelativeSource =
                        new System.Windows.Data.RelativeSource(
                            System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });

            border.AppendChild(content);

            template.VisualTree =
                border;

            return template;
        }


        // ============================================================
        // ARTWORK
        // ============================================================

        private static Image? LoadArtwork(
            string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return null;

            if (!File.Exists(imagePath))
                return null;

            try
            {
                var bitmap =
                    new BitmapImage();

                bitmap.BeginInit();

                bitmap.UriSource =
                    new Uri(
                        imagePath,
                        UriKind.Absolute);

                bitmap.CacheOption =
                    BitmapCacheOption.OnLoad;

                bitmap.DecodePixelWidth =
                    500;

                bitmap.EndInit();

                bitmap.Freeze();

                return new Image
                {
                    Source =
                        bitmap,

                    Stretch =
                        Stretch.UniformToFill
                };
            }
            catch
            {
                return null;
            }
        }

        private Border CreateImagePlaceholder(
            string name)
        {
            return new Border
            {
                Background =
                    FindBrush("CardHoverBrush"),

                Child =
                    new TextBlock
                    {
                        Text =
                            GetInitials(name),

                        Foreground =
                            FindBrush("SecondaryBrush"),

                        FontSize =
                            34,

                        FontWeight =
                            FontWeights.Bold,

                        HorizontalAlignment =
                            HorizontalAlignment.Center,

                        VerticalAlignment =
                            VerticalAlignment.Center
                    }
            };
        }

        private static string GetInitials(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";

            string[] words =
                name.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 1)
            {
                return words[0][0]
                    .ToString()
                    .ToUpperInvariant();
            }

            return string.Concat(
                words
                    .Take(2)
                    .Select(
                        x =>
                            x[0]
                                .ToString()
                                .ToUpperInvariant()));
        }


        // ============================================================
        // ANIMATIONS
        // ============================================================

        private static void AnimateEntrance(
            Border card)
        {
            if (card.RenderTransform
                is not TranslateTransform transform)
            {
                return;
            }

            var fade =
                new DoubleAnimation
                {
                    From = 0,
                    To = 1,

                    Duration =
                        TimeSpan.FromMilliseconds(280),

                    EasingFunction =
                        new CubicEase
                        {
                            EasingMode =
                                EasingMode.EaseOut
                        }
                };

            var slide =
                new DoubleAnimation
                {
                    From = 10,
                    To = 0,

                    Duration =
                        TimeSpan.FromMilliseconds(300),

                    EasingFunction =
                        new CubicEase
                        {
                            EasingMode =
                                EasingMode.EaseOut
                        }
                };

            card.BeginAnimation(
                OpacityProperty,
                fade);

            transform.BeginAnimation(
                TranslateTransform.YProperty,
                slide);
        }

        private static void AnimateCard(
            Border card,
            double y,
            int milliseconds)
        {
            if (card.RenderTransform
                is not TranslateTransform transform)
            {
                return;
            }

            var animation =
                new DoubleAnimation
                {
                    To = y,

                    Duration =
                        TimeSpan.FromMilliseconds(
                            milliseconds),

                    EasingFunction =
                        new CubicEase
                        {
                            EasingMode =
                                EasingMode.EaseOut
                        }
                };

            transform.BeginAnimation(
                TranslateTransform.YProperty,
                animation);
        }


        // ============================================================
        // LAUNCH
        // ============================================================

        private async void LaunchGame(
            GameEntry game)
        {
            if (!File.Exists(
                    game.ExecutablePath))
            {
                System.Windows.MessageBox.Show(
                    $"The game executable could not be found.\n\n{game.ExecutablePath}",
                    "Game Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            try
            {
                var logWindow =
                    new GameLogWindow(game)
                    {
                        Owner = this
                    };

                logWindow.Show();

                if (!await logWindow.StartAsync())
                    return;

                game.LastPlayed =
                    DateTime.Now;

                await _libraryService
                    .SaveAsync(_library);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Unable to launch {game.Name}.\n\n{ex.Message}",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // ============================================================
        // GAME DETAILS
        // ============================================================

        private void ShowGameDetails(GameEntry game)
        {
            _selectedGame = game;
            DetailsGameName.Text = game.Name;
            DetailsNameBox.Text = game.Name;
            DetailsExecutableBox.Text = game.ExecutablePath;
            RebuildExecutableChoices();
            ShowPage(GameDetailsPage);
        }

        private void RebuildExecutableChoices()
        {
            ExecutableChoices.Children.Clear();

            if (_selectedGame is null)
                return;

            _selectedGame.ExecutablePaths ??= new List<string>();
            var paths = _selectedGame.ExecutablePaths
                .Append(_selectedGame.ExecutablePath)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            _selectedGame.ExecutablePaths = paths;

            foreach (string path in paths)
            {
                string relativePath;
                try
                {
                    relativePath = Path.GetRelativePath(_selectedGame.InstallDirectory, path);
                }
                catch
                {
                    relativePath = path;
                }

                var button = new Button
                {
                    Content = string.Equals(path, _selectedGame.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        ? $"✓  {relativePath}"
                        : relativePath,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 7),
                    Style = (Style)FindResource("SecondaryButton")
                };

                button.Click += async (_, _) =>
                {
                    if (_selectedGame is null)
                        return;

                    _selectedGame.ExecutablePath = path;
                    _selectedGame.ExecutableManuallySelected = true;
                    DetailsExecutableBox.Text = path;
                    RebuildExecutableChoices();
                    await _libraryService.SaveAsync(_library);
                };

                ExecutableChoices.Children.Add(button);
            }

            if (paths.Count == 0)
            {
                ExecutableChoices.Children.Add(new TextBlock
                {
                    Text = "No executable candidates are currently available.",
                    Foreground = FindBrush("SecondaryBrush"),
                    Margin = new Thickness(0, 7, 0, 7)
                });
            }
        }

        private async void ChooseExecutable_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            string initialDirectory = Directory.Exists(_selectedGame.InstallDirectory)
                ? _selectedGame.InstallDirectory
                : Path.GetDirectoryName(_selectedGame.ExecutablePath) ?? string.Empty;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Choose the executable for {_selectedGame.Name}",
                InitialDirectory = initialDirectory,
                Filter = "Windows applications (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            _selectedGame.ExecutablePath = Path.GetFullPath(dialog.FileName);
            _selectedGame.ExecutableManuallySelected = true;
            _selectedGame.ExecutablePaths ??= new List<string>();

            if (!_selectedGame.ExecutablePaths.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
                _selectedGame.ExecutablePaths.Add(dialog.FileName);

            DetailsExecutableBox.Text = _selectedGame.ExecutablePath;
            RebuildExecutableChoices();
            await _libraryService.SaveAsync(_library);
        }

        private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null || !Directory.Exists(_selectedGame.InstallDirectory))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = _selectedGame.InstallDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private async void SaveGameDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame is null)
                return;

            _selectedGame.Name = GameNameFormatter.CleanDisplayName(
                DetailsNameBox.Text,
                _selectedGame.ExecutablePath);

            DetailsGameName.Text = _selectedGame.Name;
            DetailsNameBox.Text = _selectedGame.Name;
            await _libraryService.SaveAsync(_library);
            RefreshLibrary();
        }

        private void PlaySelectedGame_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame != null)
                LaunchGame(_selectedGame);
        }


        // ============================================================
        // SETTINGS AND THEMES
        // ============================================================

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            BuildThemeButtons();
            ThemeCodeBox.Text = ThemeService.Themes.ContainsKey(_library.Settings.ThemeName)
                ? ThemeService.GetCode(_library.Settings.ThemeName)
                : _library.Settings.ThemeCode;
            ThemeStatusText.Text = $"Current theme: {_library.Settings.ThemeName}";
            ShowPage(SettingsPage);
        }

        private void BuildThemeButtons()
        {
            if (ThemeButtons is null)
                return;

            ThemeButtons.Children.Clear();

            foreach (string themeName in ThemeService.Themes.Keys)
            {
                var button = new Button
                {
                    Content = themeName,
                    Margin = new Thickness(0, 0, 8, 8),
                    Style = (Style)FindResource(
                        string.Equals(themeName, _library.Settings.ThemeName, StringComparison.OrdinalIgnoreCase)
                            ? "PrimaryButton"
                            : "SecondaryButton")
                };

                button.Click += async (_, _) =>
                {
                    string code = ThemeService.GetCode(themeName);
                    ThemeCodeBox.Text = code;
                    _library.Settings.ThemeName = themeName;
                    _library.Settings.ThemeCode = code;
                    ApplyThemeCode(code, $"Applied {themeName}");
                    BuildThemeButtons();
                    await _libraryService.SaveAsync(_library);
                };

                ThemeButtons.Children.Add(button);
            }
        }

        private async void ApplyTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyThemeCode(ThemeCodeBox.Text, "Custom theme applied"))
                return;

            _library.Settings.ThemeName = "Custom";
            _library.Settings.ThemeCode = ThemeCodeBox.Text;
            BuildThemeButtons();
            await _libraryService.SaveAsync(_library);
        }

        private async void ResetTheme_Click(object sender, RoutedEventArgs e)
        {
            const string themeName = "Void Purple";
            string code = ThemeService.GetCode(themeName);
            ThemeCodeBox.Text = code;
            _library.Settings.ThemeName = themeName;
            _library.Settings.ThemeCode = code;
            ApplyThemeCode(code, "Default theme restored");
            BuildThemeButtons();
            await _libraryService.SaveAsync(_library);
        }

        private bool ApplyThemeCode(string code, string successMessage)
        {
            if (!ThemeService.TryApply(Resources, code, out string error))
            {
                ThemeStatusText.Text = error;
                ThemeStatusText.Foreground = FindBrush("ErrorBrush");
                return false;
            }

            if (System.Windows.Application.Current != null)
            {
                foreach (Window window in System.Windows.Application.Current.Windows)
                    ThemeService.ApplyTo(window.Resources);
            }

            ThemeStatusText.Text = successMessage;
            ThemeStatusText.Foreground = FindBrush("AccentBrush");
            RefreshLibrary();
            return true;
        }

        private void ApplySavedTheme()
        {
            string code = ThemeService.Themes.ContainsKey(_library.Settings.ThemeName)
                ? ThemeService.GetCode(_library.Settings.ThemeName)
                : _library.Settings.ThemeCode;

            if (string.IsNullOrWhiteSpace(code))
                code = ThemeService.GetCode("Void Purple");

            ThemeService.TryApply(Resources, code, out _);
            _library.Settings.ThemeCode = code;
        }

        private void ShowPage(UIElement page)
        {
            LibraryPage.Visibility = page == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
            GameDetailsPage.Visibility = page == GameDetailsPage ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
            DeveloperPage.Visibility = page == DeveloperPage ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = page == AboutPage ? Visibility.Visible : Visibility.Collapsed;
            PrivacyPage.Visibility = page == PrivacyPage ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackToLibrary_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(LibraryPage);
            RefreshLibrary();
        }


        // ============================================================
        // DEVELOPER / ABOUT / PRIVACY / UPDATES
        // ============================================================

        private void Developer_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(DeveloperPage);
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            PopulateAboutPage();
            ShowPage(AboutPage);
        }

        private void Privacy_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(PrivacyPage);
        }

        private void PopulateAboutPage()
        {
            AboutVersionText.Text = $"Version {AppInfo.DisplayVersion} · Windows x64";
            AboutHealthText.Text = "Healthy";
            AboutGameCountText.Text = $"Library: {_library.Games.Count} game{(_library.Games.Count == 1 ? string.Empty : "s")} loaded";
            AboutInstallPathText.Text = $"Running from: {Environment.ProcessPath ?? "Unknown"}";
            AboutDataPathText.Text = $"Local data: {_libraryService.DataFilePath}";
            AboutUpdateSourceText.Text = $"Update source: {AppInfo.ReleasesUrl}";

            if (_latestUpdate != null)
                AboutUpdateStatusText.Text = _latestUpdate.Message;
            else if (string.IsNullOrWhiteSpace(AboutUpdateStatusText.Text))
                AboutUpdateStatusText.Text = "Updates are checked automatically when VoidLaunch starts.";
        }

        private async Task CheckForUpdatesAsync()
        {
            CheckUpdatesButton.IsEnabled = false;
            AboutUpdateStatusText.Text = "Checking GitHub Releases…";

            try
            {
                _latestUpdate = await _updateService.CheckAsync();
                AboutUpdateStatusText.Text = _latestUpdate.Message;
                UpdateNowButton.Visibility =
                    _latestUpdate.UpdateAvailable && _latestUpdate.Asset != null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }

        private async void UpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdate?.Asset is not UpdateAsset asset)
                return;

            CheckUpdatesButton.IsEnabled = false;
            UpdateNowButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Visibility.Visible;
            AboutUpdateStatusText.Text = "Downloading verified update…";

            try
            {
                var progress = new Progress<int>(value =>
                {
                    UpdateProgressBar.Value = value;
                    AboutUpdateStatusText.Text = $"Downloading update… {value}%";
                });

                string downloadedExecutable = await _updateService.DownloadAsync(asset, progress);
                AboutUpdateStatusText.Text = "Update verified. Restarting VoidLaunch…";
                _updateService.ScheduleReplacementAndRestart(downloadedExecutable);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AboutUpdateStatusText.Text = $"Update failed: {ex.Message}";
                AboutUpdateStatusText.Foreground = FindBrush("ErrorBrush");
                CheckUpdatesButton.IsEnabled = true;
                UpdateNowButton.IsEnabled = true;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenDeveloperGitHub_Click(object sender, RoutedEventArgs e)
        {
            OpenWebPage(AppInfo.DeveloperUrl);
        }

        private void OpenRepository_Click(object sender, RoutedEventArgs e)
        {
            OpenWebPage(AppInfo.RepositoryUrl);
        }

        private void OpenReleases_Click(object sender, RoutedEventArgs e)
        {
            OpenWebPage(AppInfo.ReleasesUrl);
        }

        private static void OpenWebPage(string url)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private void NormalizeLibraryData()
        {
            _library.Games ??= new List<GameEntry>();
            _library.Folders ??= new List<string>();
            _library.Settings ??= new LauncherSettings();

            foreach (GameEntry game in _library.Games)
            {
                game.ExecutablePaths ??= new List<string>();

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

                if (!keepManualExecutable &&
                    !GameScanner.IsPotentialGameExecutable(game.ExecutablePath))
                {
                    game.ExecutableManuallySelected = false;
                    game.ExecutablePath = game.ExecutablePaths.FirstOrDefault() ?? string.Empty;
                }

                game.Name = GameNameFormatter.CleanDisplayName(game.Name, game.ExecutablePath);
            }

            _library.Games = _library.Games
                .Where(game =>
                    (game.ExecutableManuallySelected && File.Exists(game.ExecutablePath)) ||
                    GameScanner.IsPotentialGameExecutable(game.ExecutablePath))
                .ToList();

            RemoveDuplicateGames();
        }

        private void ConsolidateNestedInstallDirectories()
        {
            for (int i = 0; i < _library.Games.Count; i++)
            {
                GameEntry outer = _library.Games[i];
                outer.ExecutablePaths ??= new List<string>();

                for (int j = 0; j < _library.Games.Count; j++)
                {
                    if (i == j)
                        continue;

                    GameEntry inner = _library.Games[j];
                    inner.ExecutablePaths ??= new List<string>();

                    string outerInstall = NormalizePath(outer.InstallDirectory);
                    string innerInstall = NormalizePath(inner.InstallDirectory);

                    if (string.Equals(outerInstall, innerInstall, StringComparison.OrdinalIgnoreCase) ||
                        !IsPathInside(innerInstall, outerInstall))
                    {
                        continue;
                    }

                    var outerExecutables = outer.ExecutablePaths
                        .Append(outer.ExecutablePath)
                        .Select(NormalizePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    bool sharesExecutable = inner.ExecutablePaths
                        .Append(inner.ExecutablePath)
                        .Select(NormalizePath)
                        .Any(outerExecutables.Contains);

                    if (sharesExecutable)
                        inner.InstallDirectory = outer.InstallDirectory;
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child)
            where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match)
                    return match;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }


        // ============================================================
        // FILTERS
        // ============================================================

        private void AllGames_Click(
            object sender,
            RoutedEventArgs e)
        {
            _showFavorites = false;
            _showRecent = false;

            ShowPage(LibraryPage);
            RefreshLibrary();
        }

        private void Favorites_Click(
            object sender,
            RoutedEventArgs e)
        {
            _showFavorites = true;
            _showRecent = false;

            ShowPage(LibraryPage);
            RefreshLibrary();
        }

        private void Recent_Click(
            object sender,
            RoutedEventArgs e)
        {
            _showFavorites = false;
            _showRecent = true;

            ShowPage(LibraryPage);
            RefreshLibrary();
        }

        private void SearchBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            RefreshLibrary();
        }


        // ============================================================
        // EMPTY STATE
        // ============================================================

        private void AddEmptyState()
        {
            var border =
                new Border
                {
                    Width = 238,
                    Height = 282,

                    Margin =
                        new Thickness(
                            0,
                            0,
                            14,
                            14),

                    Background =
                        FindBrush("CardBrush"),

                    BorderBrush =
                        FindBrush("BorderBrush"),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(12),

                    Opacity = 0
                };

            var stack =
                new StackPanel
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Center,

                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            stack.Children.Add(
                new TextBlock
                {
                    Text = "▦",

                    FontSize = 40,

                    Foreground =
                        FindBrush("SecondaryBrush"),

                    HorizontalAlignment =
                        HorizontalAlignment.Center
                });

            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        "No games yet",

                    Margin =
                        new Thickness(
                            0,
                            14,
                            0,
                            0),

                    FontSize = 15,

                    FontWeight =
                        FontWeights.SemiBold,

                    Foreground =
                        FindBrush("SecondaryBrush"),

                    HorizontalAlignment =
                        HorizontalAlignment.Center
                });

            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        "Add a game folder to begin",

                    Margin =
                        new Thickness(
                            0,
                            6,
                            0,
                            0),

                    FontSize = 11,

                    Foreground =
                        FindBrush("SecondaryBrush"),

                    HorizontalAlignment =
                        HorizontalAlignment.Center
                });

            border.Child =
                stack;

            GameLibrary.Children.Add(
                border);

            border.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation
                {
                    From = 0,
                    To = 1,

                    Duration =
                        TimeSpan.FromMilliseconds(250)
                });
        }


        // ============================================================
        // HELPERS
        // ============================================================

        private Brush FindBrush(
            string resource)
        {
            return (Brush)FindResource(resource);
        }

        private void UpdateFolderText()
        {
            if (_library.Folders.Count == 0)
            {
                GameFolderText.Text =
                    "No game folders configured";

                return;
            }

            if (_library.Folders.Count == 1)
            {
                GameFolderText.Text =
                    _library.Folders[0];

                return;
            }

            GameFolderText.Text =
                $"{_library.Folders.Count} game folders configured";
        }
    }
}
