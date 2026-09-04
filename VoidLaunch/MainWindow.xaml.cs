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
using System.Windows.Media.Effects;
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
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace VoidLaunch
{
    public partial class MainWindow : Window
    {
        private readonly LibraryService _libraryService;
        private readonly GameScanner _scanner;
        private readonly UpdateService _updateService;
        private readonly Dictionary<string, TextBox> _themeColorInputs =
            new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> _themeColorSwatches =
            new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyDictionary<string, string> ThemeColorDescriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Background"] = "Main window and page background",
                ["Sidebar"] = "Navigation and title bars",
                ["Card"] = "Panels, fields, and secondary buttons",
                ["CardHover"] = "Cards and buttons under the pointer",
                ["Border"] = "Outlines, separators, and scroll tracks",
                ["Text"] = "Headings and primary text",
                ["Secondary"] = "Descriptions and quieter text",
                ["Accent"] = "Main buttons, highlights, and branding",
                ["Error"] = "Errors, warnings, and crash messages"
            };

        private LibraryData _library =
            new LibraryData();

        private bool _showFavorites;
        private bool _showRecent;

        private bool _isRefreshing;
        private bool _isLoadingVersions;
        private bool _isDownloadingVersion;
        private bool _isInstallingUpdate;
        private bool _releaseHistoryLoaded;
        private GameEntry? _selectedGame;
        private UpdateCheckResult? _latestUpdate;
        private bool _isUpdatingThemeEditor;
        private string? _selectedSavedThemeName;
        private string _themeEditorBaselineCode = string.Empty;
        private string _themeEditorBaselineName = string.Empty;
        private bool _themeEditorBaselineIsBuiltIn;

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
            Task updateCheck = CheckForUpdatesAsync();
            await LoadLibraryAsync();
            await updateCheck;
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
            BuildThemeColorEditors();
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
                        FindBrush("AccentTextBrush"),

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
            BuildThemeColorEditors();
            BuildThemeButtons();
            string code = GetSelectedThemeCode();
            LoadThemeIntoEditor(
                _library.Settings.ThemeName,
                code,
                ThemeService.IsBuiltIn(_library.Settings.ThemeName));
            ThemeStatusText.Text = "Pick a preset or change any color to begin.";
            ThemeStatusText.Foreground = FindBrush("SecondaryBrush");
            ShowPage(SettingsPage);
        }

        private void BuildThemeButtons()
        {
            if (BuiltInThemeButtons is null || SavedThemeButtons is null)
                return;

            BuiltInThemeButtons.Children.Clear();
            SavedThemeButtons.Children.Clear();

            foreach ((string themeName, IReadOnlyDictionary<string, string> colors) in ThemeService.Themes)
                AddThemeChoiceButton(BuiltInThemeButtons, themeName, ThemeService.GetCode(themeName), colors, true);

            foreach (SavedTheme theme in _library.Settings.SavedThemes
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ThemeService.TryGetColors(theme.Code, out IReadOnlyDictionary<string, string> colors, out _))
                    AddThemeChoiceButton(SavedThemeButtons, theme.Name, theme.Code, colors, false);
            }

            SavedThemesEmptyText.Visibility = _library.Settings.SavedThemes.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddThemeChoiceButton(
            Panel destination,
            string themeName,
            string code,
            IReadOnlyDictionary<string, string> colors,
            bool isBuiltIn)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = themeName,
                MaxWidth = 145,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var palette = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                Orientation = Orientation.Horizontal
            };

            foreach (string colorName in new[] { "Background", "Card", "Border", "Accent", "Text" })
            {
                palette.Children.Add(new Border
                {
                    Width = 23,
                    Height = 9,
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = BrushFromHex(colors[colorName]),
                    BorderBrush = BrushFromHex(colors["Border"]),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3)
                });
            }

            content.Children.Add(palette);
            content.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 7, 0, 0),
                FontSize = 10,
                Opacity = 0.72,
                Text = isBuiltIn ? "BUILT IN" : "SAVED"
            });

            var button = new Button
            {
                Width = 174,
                MinHeight = 78,
                Content = content,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 10, 12, 10),
                ToolTip = $"Use {themeName}",
                Style = (Style)FindResource(
                    string.Equals(themeName, _library.Settings.ThemeName, StringComparison.OrdinalIgnoreCase)
                        ? "PrimaryButton"
                        : "SecondaryButton")
            };

            button.Click += async (_, _) => await SelectThemeAsync(themeName, code, isBuiltIn);
            destination.Children.Add(button);
        }

        private async Task SelectThemeAsync(string themeName, string code, bool isBuiltIn)
        {
            if (!ThemeService.TryNormalizeCode(code, out string normalizedCode, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            _library.Settings.ThemeName = themeName;
            _library.Settings.ThemeCode = normalizedCode;
            ApplyThemeCode(normalizedCode, $"Applied {themeName}. Your choice was saved.");
            LoadThemeIntoEditor(themeName, normalizedCode, isBuiltIn);
            BuildThemeButtons();
            await _libraryService.SaveAsync(_library);
        }

        private void BuildThemeColorEditors()
        {
            if (ThemeColorEditors is null || _themeColorInputs.Count > 0)
                return;

            foreach (string colorName in ThemeService.ColorKeys)
            {
                var row = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12, 10, 12, 10),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9)
                };
                row.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
                row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

                var layout = new Grid();
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var colorNameText = new TextBlock
                {
                    Text = colorName,
                    FontWeight = FontWeights.SemiBold
                };
                colorNameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                label.Children.Add(colorNameText);

                var descriptionText = new TextBlock
                {
                    Margin = new Thickness(0, 3, 12, 0),
                    Text = ThemeColorDescriptions[colorName],
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11
                };
                descriptionText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
                label.Children.Add(descriptionText);
                layout.Children.Add(label);

                var input = new TextBox
                {
                    Tag = colorName,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = "Enter a color like #F472B6",
                    Style = (Style)FindResource("FieldTextBox")
                };
                input.TextChanged += ThemeColorInput_TextChanged;
                Grid.SetColumn(input, 1);
                layout.Children.Add(input);
                _themeColorInputs[colorName] = input;

                var swatch = new Button
                {
                    Tag = colorName,
                    Width = 42,
                    Height = 38,
                    Padding = new Thickness(0),
                    Content = "■",
                    FontSize = 22,
                    ToolTip = $"Choose the {colorName.ToLowerInvariant()} color",
                    Style = (Style)FindResource("SecondaryButton")
                };
                swatch.Click += ChooseThemeColor_Click;
                Grid.SetColumn(swatch, 2);
                layout.Children.Add(swatch);
                _themeColorSwatches[colorName] = swatch;

                row.Child = layout;
                ThemeColorEditors.Children.Add(row);
            }
        }

        private void LoadThemeIntoEditor(string themeName, string code, bool isBuiltIn)
        {
            BuildThemeColorEditors();

            if (!ThemeService.TryNormalizeCode(code, out string normalizedCode, out _))
                normalizedCode = ThemeService.GetCode("Void Purple");

            if (!ThemeService.TryGetColors(
                    normalizedCode,
                    out IReadOnlyDictionary<string, string> colors,
                    out _))
            {
                return;
            }

            _isUpdatingThemeEditor = true;
            try
            {
                foreach (string colorName in ThemeService.ColorKeys)
                {
                    _themeColorInputs[colorName].Text = colors[colorName];
                    _themeColorSwatches[colorName].Foreground = BrushFromHex(colors[colorName]);
                }

                ThemeCodeBox.Text = normalizedCode;
                ThemeNameBox.Text = isBuiltIn ? $"{themeName} Custom" : themeName;
            }
            finally
            {
                _isUpdatingThemeEditor = false;
            }

            _selectedSavedThemeName = isBuiltIn ? null : themeName;
            _themeEditorBaselineCode = normalizedCode;
            _themeEditorBaselineName = themeName;
            _themeEditorBaselineIsBuiltIn = isBuiltIn;
            DeleteThemeButton.IsEnabled = !isBuiltIn;
            SaveThemeButton.Content = isBuiltIn ? "Save as my theme" : "Update my theme";
            CurrentThemeText.Text = $"Current theme: {_library.Settings.ThemeName}";
            UpdateThemeAccessibility(colors);
        }

        private void ThemeColorInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingThemeEditor || sender is not TextBox input || input.Tag is not string colorName)
                return;

            try
            {
                var color = (MediaColor)MediaColorConverter.ConvertFromString(input.Text.Trim());
                _themeColorSwatches[colorName].Foreground = new SolidColorBrush(color);
            }
            catch
            {
                // Keep the last valid swatch while the user is still typing.
            }

            if (!TryBuildThemeCode(out string code, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            _isUpdatingThemeEditor = true;
            ThemeCodeBox.Text = code;
            _isUpdatingThemeEditor = false;

            if (!ApplyThemeResources(code, out error))
            {
                SetThemeStatus(error, true);
                return;
            }

            CurrentThemeText.Text = $"Previewing unsaved changes to {_library.Settings.ThemeName}";
            SetThemeStatus("Live preview is on. Save the theme when it looks right.");
            if (ThemeService.TryGetColors(code, out IReadOnlyDictionary<string, string> colors, out _))
                UpdateThemeAccessibility(colors);
            RefreshLibrary();
        }

        private void ChooseThemeColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string colorName)
                return;

            MediaColor current;
            try
            {
                current = (MediaColor)MediaColorConverter.ConvertFromString(
                    _themeColorInputs[colorName].Text.Trim());
            }
            catch
            {
                current = (MediaColor)MediaColorConverter.ConvertFromString("#8B5CF6");
            }

            using var dialog = new System.Windows.Forms.ColorDialog
            {
                AnyColor = true,
                FullOpen = true,
                SolidColorOnly = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            _themeColorInputs[colorName].Text =
                $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }

        private async void SaveTheme_Click(object sender, RoutedEventArgs e)
        {
            string themeName = string.Join(
                " ",
                ThemeNameBox.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (string.IsNullOrWhiteSpace(themeName))
            {
                SetThemeStatus("Enter a name before saving your theme.", true);
                ThemeNameBox.Focus();
                return;
            }

            if (ThemeService.IsBuiltIn(themeName))
            {
                SetThemeStatus("That name belongs to a built-in theme. Choose a different name.", true);
                return;
            }

            if (!TryBuildThemeCode(out string code, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            SavedTheme? selectedTheme = _library.Settings.SavedThemes.FirstOrDefault(theme =>
                string.Equals(theme.Name, _selectedSavedThemeName, StringComparison.OrdinalIgnoreCase));
            SavedTheme? nameCollision = _library.Settings.SavedThemes.FirstOrDefault(theme =>
                string.Equals(theme.Name, themeName, StringComparison.OrdinalIgnoreCase));

            if (nameCollision is not null && !ReferenceEquals(nameCollision, selectedTheme))
            {
                SetThemeStatus("A saved theme already has that name. Select it first to update it.", true);
                return;
            }

            if (selectedTheme is null)
            {
                selectedTheme = new SavedTheme();
                _library.Settings.SavedThemes.Add(selectedTheme);
            }

            selectedTheme.Name = themeName;
            selectedTheme.Code = code;
            _library.Settings.ThemeName = themeName;
            _library.Settings.ThemeCode = code;
            ApplyThemeCode(code, $"Saved and applied {themeName}.");
            LoadThemeIntoEditor(themeName, code, false);
            BuildThemeButtons();
            await _libraryService.SaveAsync(_library);
            SetThemeStatus($"Saved and applied {themeName}.");
        }

        private async void DeleteTheme_Click(object sender, RoutedEventArgs e)
        {
            SavedTheme? theme = _library.Settings.SavedThemes.FirstOrDefault(item =>
                string.Equals(item.Name, _selectedSavedThemeName, StringComparison.OrdinalIgnoreCase));
            if (theme is null)
            {
                SetThemeStatus("Select one of your saved themes before deleting it.", true);
                return;
            }

            MessageBoxResult choice = System.Windows.MessageBox.Show(
                this,
                $"Delete the saved theme ‘{theme.Name}’?",
                "Delete theme",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
                return;

            _library.Settings.SavedThemes.Remove(theme);
            await SelectThemeAsync("Void Purple", ThemeService.GetCode("Void Purple"), true);
            SetThemeStatus($"Deleted {theme.Name} and restored Void Purple.");
        }

        private void DuplicateTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildThemeCode(out _, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            string sourceName = _selectedSavedThemeName ?? _library.Settings.ThemeName;
            ThemeNameBox.Text = GetUniqueThemeName($"{sourceName} Copy");
            _selectedSavedThemeName = null;
            DeleteThemeButton.IsEnabled = false;
            SaveThemeButton.Content = "Save as my theme";
            SetThemeStatus("A copy is ready. Rename it if you want, then choose Save as my theme.");
            ThemeNameBox.Focus();
            ThemeNameBox.SelectAll();
        }

        private void ResetEditor_Click(object sender, RoutedEventArgs e)
        {
            LoadThemeIntoEditor(
                _themeEditorBaselineName,
                _themeEditorBaselineCode,
                _themeEditorBaselineIsBuiltIn);
            ApplyThemeCode(_themeEditorBaselineCode, "Undid the unsaved color changes.");
        }

        private async void ImportTheme_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import a VoidLaunch theme",
                Filter = "VoidLaunch theme (*.voidtheme)|*.voidtheme|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                string imported = await File.ReadAllTextAsync(dialog.FileName);
                if (!ThemeService.TryNormalizeCode(imported, out string code, out string error))
                {
                    SetThemeStatus($"Could not import theme: {error}", true);
                    return;
                }

                string? declaredName = imported
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("# Name:", StringComparison.OrdinalIgnoreCase));
                string suggestedName = declaredName is null
                    ? Path.GetFileNameWithoutExtension(dialog.FileName)
                    : declaredName[7..].Trim();
                string themeName = GetUniqueThemeName(suggestedName);

                _library.Settings.SavedThemes.Add(new SavedTheme { Name = themeName, Code = code });
                _library.Settings.ThemeName = themeName;
                _library.Settings.ThemeCode = code;
                ApplyThemeCode(code, $"Imported and applied {themeName}.");
                LoadThemeIntoEditor(themeName, code, false);
                BuildThemeButtons();
                await _libraryService.SaveAsync(_library);
                SetThemeStatus($"Imported and saved {themeName}.");
            }
            catch (Exception ex)
            {
                SetThemeStatus($"Could not import theme: {ex.Message}", true);
            }
        }

        private async void ExportTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildThemeCode(out string code, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            string themeName = string.IsNullOrWhiteSpace(ThemeNameBox.Text)
                ? "VoidLaunch Theme"
                : ThemeNameBox.Text.Trim();
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            string safeFileName = new string(themeName
                .Select(character => invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());

            var dialog = new SaveFileDialog
            {
                Title = "Export VoidLaunch theme",
                Filter = "VoidLaunch theme (*.voidtheme)|*.voidtheme",
                DefaultExt = ".voidtheme",
                AddExtension = true,
                FileName = safeFileName
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                string exported = $"# VoidLaunch theme{Environment.NewLine}# Name: {themeName}{Environment.NewLine}{code}{Environment.NewLine}";
                await File.WriteAllTextAsync(dialog.FileName, exported);
                SetThemeStatus($"Exported {themeName} to {dialog.FileName}");
            }
            catch (Exception ex)
            {
                SetThemeStatus($"Could not export theme: {ex.Message}", true);
            }
        }

        private void ToggleAdvancedTheme_Click(object sender, RoutedEventArgs e)
        {
            AdvancedThemePanel.Visibility = AdvancedThemePanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (AdvancedThemePanel.Visibility == Visibility.Visible &&
                TryBuildThemeCode(out string code, out _))
            {
                ThemeCodeBox.Text = code;
            }
        }

        private void ApplyTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!ThemeService.TryNormalizeCode(ThemeCodeBox.Text, out string code, out string error))
            {
                SetThemeStatus(error, true);
                return;
            }

            if (!ThemeService.TryGetColors(code, out IReadOnlyDictionary<string, string> colors, out error))
            {
                SetThemeStatus(error, true);
                return;
            }

            _isUpdatingThemeEditor = true;
            try
            {
                foreach (string colorName in ThemeService.ColorKeys)
                {
                    _themeColorInputs[colorName].Text = colors[colorName];
                    _themeColorSwatches[colorName].Foreground = BrushFromHex(colors[colorName]);
                }

                ThemeCodeBox.Text = code;
            }
            finally
            {
                _isUpdatingThemeEditor = false;
            }

            ApplyThemeCode(code, "Theme code loaded. Save the theme if you want to keep it.");
            CurrentThemeText.Text = "Previewing advanced theme code";
            UpdateThemeAccessibility(colors);
        }

        private async void ResetTheme_Click(object sender, RoutedEventArgs e)
        {
            const string themeName = "Void Purple";
            await SelectThemeAsync(themeName, ThemeService.GetCode(themeName), true);
            SetThemeStatus("Void Purple restored and saved.");
        }

        private bool TryBuildThemeCode(out string code, out string error)
        {
            if (_themeColorInputs.Count != ThemeService.ColorKeys.Count)
            {
                code = string.Empty;
                error = "The color editor is not ready yet.";
                return false;
            }

            string rawCode = string.Join(
                Environment.NewLine,
                ThemeService.ColorKeys.Select(colorName =>
                    $"{colorName} = {_themeColorInputs[colorName].Text.Trim()}"));
            return ThemeService.TryNormalizeCode(rawCode, out code, out error);
        }

        private string GetSelectedThemeCode()
        {
            if (ThemeService.IsBuiltIn(_library.Settings.ThemeName))
                return ThemeService.GetCode(_library.Settings.ThemeName);

            SavedTheme? savedTheme = _library.Settings.SavedThemes.FirstOrDefault(theme =>
                string.Equals(theme.Name, _library.Settings.ThemeName, StringComparison.OrdinalIgnoreCase));
            return savedTheme?.Code ?? _library.Settings.ThemeCode;
        }

        private string GetUniqueThemeName(string suggestedName)
        {
            string baseName = string.Join(
                " ",
                (suggestedName ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported Theme";
            if (baseName.Length > 40)
                baseName = baseName[..40].Trim();

            string candidate = baseName;
            int suffix = 2;
            while (ThemeService.IsBuiltIn(candidate) ||
                   _library.Settings.SavedThemes.Any(theme =>
                       string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                string ending = $" ({suffix++})";
                int availableLength = Math.Max(1, 40 - ending.Length);
                candidate = baseName[..Math.Min(baseName.Length, availableLength)].TrimEnd() + ending;
            }

            return candidate;
        }

        private void UpdateThemeAccessibility(IReadOnlyDictionary<string, string> colors)
        {
            double contrast = ThemeService.GetContrastRatio(colors["Background"], colors["Text"]);
            string rating = contrast >= 7 ? "Excellent" : contrast >= 4.5 ? "Good" : contrast >= 3 ? "Fair" : "Low";
            ThemeAccessibilityText.Text =
                $"Readability: {rating} ({contrast:0.0}:1 background/text contrast). " +
                "Accent-button text is chosen automatically for readability.";
            ThemeAccessibilityText.Foreground = contrast >= 4.5
                ? FindBrush("SecondaryBrush")
                : FindBrush("ErrorBrush");
        }

        private bool ApplyThemeCode(string code, string successMessage)
        {
            if (!ApplyThemeResources(code, out string error))
            {
                SetThemeStatus(error, true);
                return false;
            }

            SetThemeStatus(successMessage);
            RefreshLibrary();
            return true;
        }

        private bool ApplyThemeResources(string code, out string error)
        {
            if (!ThemeService.TryApply(Resources, code, out error))
                return false;

            if (System.Windows.Application.Current != null)
            {
                foreach (Window window in System.Windows.Application.Current.Windows)
                    ThemeService.ApplyTo(window.Resources);
            }

            return true;
        }

        private void SetThemeStatus(string message, bool isError = false)
        {
            ThemeStatusText.Text = message;
            ThemeStatusText.Foreground = FindBrush(isError ? "ErrorBrush" : "AccentBrush");
        }

        private static SolidColorBrush BrushFromHex(string color) =>
            new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));

        private void ApplySavedTheme()
        {
            string code = GetSelectedThemeCode();

            if (!ThemeService.TryNormalizeCode(code, out string normalizedCode, out _))
            {
                _library.Settings.ThemeName = "Void Purple";
                code = ThemeService.GetCode("Void Purple");
                ThemeService.TryNormalizeCode(code, out normalizedCode, out _);
            }

            ThemeService.TryApply(Resources, normalizedCode, out _);
            _library.Settings.ThemeCode = normalizedCode;
        }

        private void ShowPage(UIElement page)
        {
            LibraryPage.Visibility = page == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
            GameDetailsPage.Visibility = page == GameDetailsPage ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
            DeveloperPage.Visibility = page == DeveloperPage ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = page == AboutPage ? Visibility.Visible : Visibility.Collapsed;
            VersionsPage.Visibility = page == VersionsPage ? Visibility.Visible : Visibility.Collapsed;
            ComingSoonPage.Visibility = page == ComingSoonPage ? Visibility.Visible : Visibility.Collapsed;
            PrivacyPage.Visibility = page == PrivacyPage ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackToLibrary_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(LibraryPage);
            RefreshLibrary();
        }


        // ============================================================
        // DEVELOPER / ABOUT / PRIVACY / UPDATES / COMING SOON
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

        private void ComingSoon_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(ComingSoonPage);
        }

        private async void Versions_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(VersionsPage);
            await LoadReleaseHistoryAsync(false);
        }

        private async void RefreshVersions_Click(object sender, RoutedEventArgs e)
        {
            await LoadReleaseHistoryAsync(true);
        }

        private async Task LoadReleaseHistoryAsync(bool forceRefresh)
        {
            if (_isLoadingVersions || _isDownloadingVersion || (_releaseHistoryLoaded && !forceRefresh))
                return;

            _isLoadingVersions = true;
            RefreshVersionsButton.IsEnabled = false;
            VersionsProgressBar.IsIndeterminate = true;
            VersionsProgressBar.Visibility = Visibility.Visible;
            VersionsStatusText.Foreground = FindBrush("SecondaryBrush");
            VersionsStatusText.Text = "Loading releases from GitHub…";

            try
            {
                ReleaseHistoryResult result = await _updateService.GetReleaseHistoryAsync();
                VersionsList.Children.Clear();

                if (!result.Succeeded)
                {
                    _releaseHistoryLoaded = false;
                    VersionsStatusText.Foreground = FindBrush("ErrorBrush");
                    VersionsStatusText.Text = result.ErrorMessage;
                    return;
                }

                _releaseHistoryLoaded = true;
                VersionsStatusText.Text = result.Releases.Count == 0
                    ? "GitHub does not have any published VoidLaunch releases yet."
                    : $"{result.Releases.Count} version{(result.Releases.Count == 1 ? string.Empty : "s")} found · installed {AppInfo.DisplayVersion}";

                ReleaseHistoryItem? latestStable = result.Releases
                    .FirstOrDefault(release => !release.IsPrerelease);

                foreach (ReleaseHistoryItem release in result.Releases)
                {
                    bool isLatest = latestStable != null &&
                        string.Equals(
                            release.TagName,
                            latestStable.TagName,
                            StringComparison.OrdinalIgnoreCase);

                    VersionsList.Children.Add(BuildVersionCard(release, isLatest));
                }
            }
            finally
            {
                _isLoadingVersions = false;
                RefreshVersionsButton.IsEnabled = true;
                VersionsProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private Border BuildVersionCard(ReleaseHistoryItem release, bool isLatest)
        {
            var card = new Border
            {
                Style = (Style)FindResource("InfoCard"),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var content = new StackPanel();
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = release.Name,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleRow.Children.Add(title);

            var badges = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0)
            };

            if (IsInstalledVersion(release.Version))
                badges.Children.Add(CreateVersionBadge(
                    "INSTALLED",
                    FindBrush("CardHoverBrush"),
                    FindBrush("TextBrush")));

            if (isLatest)
                badges.Children.Add(CreateVersionBadge(
                    "LATEST",
                    FindBrush("AccentBrush"),
                    FindBrush("AccentTextBrush")));

            if (release.IsPrerelease)
                badges.Children.Add(CreateVersionBadge(
                    "PRERELEASE",
                    FindBrush("ErrorBrush"),
                    FindBrush("ErrorTextBrush")));

            Grid.SetColumn(badges, 1);
            titleRow.Children.Add(badges);
            content.Children.Add(titleRow);

            string published = release.PublishedAt?.ToLocalTime().ToString("MMM d, yyyy · h:mm tt")
                ?? "Unknown publish date";
            string assetDetails = release.Asset is null
                ? "No VoidLaunch.exe attached"
                : $"{FormatBytes(release.Asset.Size)} · {release.DownloadCount:N0} download{(release.DownloadCount == 1 ? string.Empty : "s")}";

            content.Children.Add(new TextBlock
            {
                Text = $"{release.TagName} · {published} · {assetDetails}",
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = FindBrush("SecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });

            string notes = FormatReleaseNotes(release.Notes);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                content.Children.Add(new TextBlock
                {
                    Text = notes,
                    Margin = new Thickness(0, 12, 0, 0),
                    MaxHeight = 95,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = FindBrush("SecondaryBrush")
                });
            }

            var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            var downloadButton = new Button
            {
                Content = release.Asset is null ? "EXE unavailable" : "Download EXE",
                Tag = release,
                IsEnabled = release.Asset != null,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)FindResource("PrimaryButton")
            };
            downloadButton.Click += DownloadRelease_Click;
            actions.Children.Add(downloadButton);

            var releaseButton = new Button
            {
                Content = "Open release on GitHub",
                Tag = release,
                Style = (Style)FindResource("SecondaryButton")
            };
            releaseButton.Click += OpenRelease_Click;
            actions.Children.Add(releaseButton);

            content.Children.Add(actions);
            card.Child = content;
            return card;
        }

        private static Border CreateVersionBadge(string text, Brush background, Brush foreground)
        {
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(5, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground
                }
            };
        }

        private async void DownloadRelease_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloadingVersion ||
                sender is not Button button ||
                button.Tag is not ReleaseHistoryItem release ||
                release.Asset is not UpdateAsset asset)
            {
                return;
            }

            string versionName = release.TagName.Trim().TrimStart('v', 'V');
            var dialog = new SaveFileDialog
            {
                Title = $"Download VoidLaunch {versionName}",
                FileName = $"VoidLaunch-{versionName}.exe",
                DefaultExt = ".exe",
                AddExtension = true,
                Filter = "Windows executable (*.exe)|*.exe",
                OverwritePrompt = true
            };

            string downloadsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloadsFolder))
                dialog.InitialDirectory = downloadsFolder;

            if (dialog.ShowDialog(this) != true)
                return;

            string destination = Path.GetFullPath(dialog.FileName);
            string? currentExecutable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExecutable) &&
                string.Equals(
                    destination,
                    Path.GetFullPath(currentExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                VersionsStatusText.Foreground = FindBrush("ErrorBrush");
                VersionsStatusText.Text = "Choose a different file name so the running launcher is not overwritten.";
                return;
            }

            _isDownloadingVersion = true;
            button.IsEnabled = false;
            RefreshVersionsButton.IsEnabled = false;
            VersionsProgressBar.IsIndeterminate = false;
            VersionsProgressBar.Value = 0;
            VersionsProgressBar.Visibility = Visibility.Visible;
            VersionsStatusText.Foreground = FindBrush("SecondaryBrush");
            VersionsStatusText.Text = $"Downloading and verifying VoidLaunch {versionName}… 0%";
            string? temporaryPath = null;

            try
            {
                var progress = new Progress<int>(value =>
                {
                    VersionsProgressBar.Value = value;
                    VersionsStatusText.Text =
                        $"Downloading and verifying VoidLaunch {versionName}… {value}%";
                });

                temporaryPath = await _updateService.DownloadAsync(asset, progress);
                File.Copy(temporaryPath, destination, true);
                VersionsStatusText.Text = $"Downloaded VoidLaunch {versionName} to {destination}";
            }
            catch (Exception ex)
            {
                VersionsStatusText.Foreground = FindBrush("ErrorBrush");
                VersionsStatusText.Text = $"Download failed: {ex.Message}";
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try
                    {
                        string? temporaryDirectory = Path.GetDirectoryName(temporaryPath);
                        if (!string.IsNullOrWhiteSpace(temporaryDirectory))
                            Directory.Delete(temporaryDirectory, true);
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }
                }

                _isDownloadingVersion = false;
                button.IsEnabled = true;
                RefreshVersionsButton.IsEnabled = true;
                VersionsProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ReleaseHistoryItem release })
                OpenWebPage(release.ReleaseUrl);
        }

        private static bool IsInstalledVersion(Version? version)
        {
            if (version is null)
                return false;

            Version installed = AppInfo.CurrentVersion;
            return version.Major == installed.Major &&
                version.Minor == installed.Minor &&
                Math.Max(version.Build, 0) == Math.Max(installed.Build, 0);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "Unknown size";

            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }

        private static string FormatReleaseNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return string.Empty;

            string cleaned = string.Join(
                Environment.NewLine,
                notes.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Split('\n')
                    .Select(line => line.Trim().TrimStart('#').Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line)));

            const int maximumLength = 700;
            return cleaned.Length <= maximumLength
                ? cleaned
                : $"{cleaned[..maximumLength].TrimEnd()}…";
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
            AboutUpdateStatusText.Foreground = FindBrush("SecondaryBrush");
            AboutUpdateStatusText.Text = "Checking GitHub Releases…";

            try
            {
                _latestUpdate = await _updateService.CheckAsync();
                AboutUpdateStatusText.Text = _latestUpdate.Message;
                UpdateNowButton.Visibility =
                    _latestUpdate.UpdateAvailable && _latestUpdate.Asset != null
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                if (_latestUpdate.UpdateAvailable && _latestUpdate.Asset != null)
                    ShowUpdateNotification(_latestUpdate);
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
            await InstallLatestUpdateAsync();
        }

        private async void UpdateNotificationNow_Click(object sender, RoutedEventArgs e)
        {
            await InstallLatestUpdateAsync();
        }

        private void UpdateNotificationLater_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstallingUpdate)
                return;

            HideUpdateNotification();

            if (_latestUpdate != null)
            {
                AboutUpdateStatusText.Foreground = FindBrush("SecondaryBrush");
                AboutUpdateStatusText.Text =
                    $"{_latestUpdate.Message} You can install it later from About & Health.";
            }
        }

        private void ShowUpdateNotification(UpdateCheckResult update)
        {
            if (!update.UpdateAvailable || update.Asset is null || update.LatestVersion is null)
                return;

            UpdateNotificationVersionText.Text =
                $"Version {update.LatestVersion.ToString(3)} is available · installed {AppInfo.DisplayVersion}";
            UpdateNotificationStatusText.Foreground = FindBrush("SecondaryBrush");
            UpdateNotificationStatusText.Text = "Choose when you want to install it.";
            UpdateNotificationProgressBar.Value = 0;
            UpdateNotificationProgressBar.Visibility = Visibility.Collapsed;
            UpdateNotificationLaterButton.IsEnabled = true;
            UpdateNotificationNowButton.IsEnabled = true;

            var blur = new BlurEffect { Radius = 0 };
            MainWindowShell.Effect = blur;
            blur.BeginAnimation(
                BlurEffect.RadiusProperty,
                new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(180)));

            UpdateNotificationOverlay.Visibility = Visibility.Visible;
            UpdateNotificationOverlay.Opacity = 1;
            UpdateNotificationOverlay.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

            if (UpdateNotificationCard.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)));
                scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)));
            }

            UpdateNotificationNowButton.Focus();
        }

        private void HideUpdateNotification()
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) =>
            {
                UpdateNotificationOverlay.BeginAnimation(OpacityProperty, null);
                UpdateNotificationOverlay.Opacity = 0;
                UpdateNotificationOverlay.Visibility = Visibility.Collapsed;
                MainWindowShell.Effect = null;
            };

            UpdateNotificationOverlay.BeginAnimation(OpacityProperty, fade);

            if (MainWindowShell.Effect is BlurEffect blur)
            {
                blur.BeginAnimation(
                    BlurEffect.RadiusProperty,
                    new DoubleAnimation(blur.Radius, 0, TimeSpan.FromMilliseconds(150)));
            }
        }

        private async Task InstallLatestUpdateAsync()
        {
            if (_isInstallingUpdate || _latestUpdate?.Asset is not UpdateAsset asset)
                return;

            _isInstallingUpdate = true;

            CheckUpdatesButton.IsEnabled = false;
            UpdateNowButton.IsEnabled = false;
            UpdateNotificationLaterButton.IsEnabled = false;
            UpdateNotificationNowButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateNotificationProgressBar.Value = 0;
            UpdateNotificationProgressBar.Visibility = Visibility.Visible;
            AboutUpdateStatusText.Foreground = FindBrush("SecondaryBrush");
            AboutUpdateStatusText.Text = "Downloading verified update…";
            UpdateNotificationStatusText.Foreground = FindBrush("SecondaryBrush");
            UpdateNotificationStatusText.Text = "Downloading verified update… 0%";

            bool restartScheduled = false;

            try
            {
                var progress = new Progress<int>(value =>
                {
                    UpdateProgressBar.Value = value;
                    UpdateNotificationProgressBar.Value = value;
                    AboutUpdateStatusText.Text = $"Downloading update… {value}%";
                    UpdateNotificationStatusText.Text = $"Downloading and verifying… {value}%";
                });

                string downloadedExecutable = await _updateService.DownloadAsync(asset, progress);
                AboutUpdateStatusText.Text = "Update verified. Restarting VoidLaunch…";
                UpdateNotificationStatusText.Text = "Update verified. Restarting VoidLaunch…";
                _updateService.ScheduleReplacementAndRestart(downloadedExecutable);
                restartScheduled = true;
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AboutUpdateStatusText.Text = $"Update failed: {ex.Message}";
                AboutUpdateStatusText.Foreground = FindBrush("ErrorBrush");
                UpdateNotificationStatusText.Text = $"Update failed: {ex.Message}";
                UpdateNotificationStatusText.Foreground = FindBrush("ErrorBrush");
            }
            finally
            {
                if (!restartScheduled)
                {
                    _isInstallingUpdate = false;
                    CheckUpdatesButton.IsEnabled = true;
                    UpdateNowButton.IsEnabled = true;
                    UpdateNotificationLaterButton.IsEnabled = true;
                    UpdateNotificationNowButton.IsEnabled = true;
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    UpdateNotificationProgressBar.Visibility = Visibility.Collapsed;
                }
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
            _library.Settings.SavedThemes ??= new List<SavedTheme>();

            var normalizedThemes = new List<SavedTheme>();
            foreach (SavedTheme theme in _library.Settings.SavedThemes)
            {
                string name = string.Join(
                    " ",
                    (theme.Name ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrWhiteSpace(name) || ThemeService.IsBuiltIn(name) ||
                    !ThemeService.TryNormalizeCode(theme.Code, out string normalizedCode, out _))
                {
                    continue;
                }

                if (name.Length > 40)
                    name = name[..40].Trim();

                SavedTheme? duplicate = normalizedThemes.FirstOrDefault(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (duplicate is null)
                    normalizedThemes.Add(new SavedTheme { Name = name, Code = normalizedCode });
                else
                    duplicate.Code = normalizedCode;
            }

            _library.Settings.SavedThemes = normalizedThemes;

            if (!ThemeService.IsBuiltIn(_library.Settings.ThemeName))
            {
                SavedTheme? selectedTheme = normalizedThemes.FirstOrDefault(theme =>
                    string.Equals(theme.Name, _library.Settings.ThemeName, StringComparison.OrdinalIgnoreCase));

                if (selectedTheme is null &&
                    ThemeService.TryNormalizeCode(_library.Settings.ThemeCode, out string legacyCode, out _))
                {
                    string suggestedName = string.Equals(
                        _library.Settings.ThemeName,
                        "Custom",
                        StringComparison.OrdinalIgnoreCase)
                        ? "My Custom Theme"
                        : _library.Settings.ThemeName;
                    string migratedName = GetUniqueThemeName(suggestedName);
                    selectedTheme = new SavedTheme { Name = migratedName, Code = legacyCode };
                    normalizedThemes.Add(selectedTheme);
                    _library.Settings.ThemeName = migratedName;
                }

                if (selectedTheme is null)
                {
                    _library.Settings.ThemeName = "Void Purple";
                    _library.Settings.ThemeCode = ThemeService.GetCode("Void Purple");
                }
                else
                {
                    _library.Settings.ThemeName = selectedTheme.Name;
                    _library.Settings.ThemeCode = selectedTheme.Code;
                }
            }

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
