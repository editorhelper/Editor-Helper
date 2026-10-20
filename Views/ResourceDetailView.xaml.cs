using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using EditorHelper.Models;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper.Views;

/// <summary>
/// Страница ресурса внутри основного окна: карточка скрипта или плагина
/// открывается здесь же, а не отдельным окном.
/// </summary>
public partial class ResourceDetailView : UserControl
{
    private readonly Resource _resource;
    private readonly AppSettings _settings;
    private readonly FavoritesService _favoritesService;
    private readonly DownloadService _downloadService;
    private readonly InstallationService _installationService;
    private readonly LoadedResourcesService _loadedService;
    private readonly Action _onClosed;

    private string? _downloadedFilePath;
    private CancellationTokenSource? _downloadCts;

    public ResourceDetailView(
        Resource resource,
        AppSettings settings,
        LoadedResourcesService loadedService,
        FavoritesService? favoritesService,
        Action onClosed)
    {
        InitializeComponent();

        _resource = resource;
        _settings = settings;
        _loadedService = loadedService;
        _favoritesService = favoritesService ?? new FavoritesService();
        _downloadService = new DownloadService(settings.DownloadFolder);
        _installationService = new InstallationService();
        _onClosed = onClosed;

        LoadResourceDetails();
        UpdateFavoriteButton();
        RestoreDownloadState();

        Loaded += (_, _) =>
        {
            if (!Motion.Enabled) return;
            ContentPanel.Opacity = 0;
            ContentPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(300)) { EasingFunction = Motion.EaseOut });
        };

        // Страницу убрали из окна (возврат в каталог или переход в другой
        // раздел) — гасим загрузку, иначе она продолжится в фоне и будет
        // писать прогресс в уже не показанный интерфейс.
        Unloaded += (_, _) => _downloadCts?.Cancel();
    }

    private void LoadResourceDetails()
    {
        TitleText.Text = _resource.Title;
        AuthorText.Text = string.IsNullOrWhiteSpace(_resource.Author)
            ? _resource.Application
            : $"by {_resource.Author}";

        TypeText.Text = _resource.Type.ToString();
        AppText.Text = _resource.Application;
        VersionText.Text = $"v{_resource.Version}";

        DescriptionText.Text = _resource.Description;

        if (!string.IsNullOrWhiteSpace(_resource.LongDescription))
            FeaturesText.Text = _resource.LongDescription;
        else
        {
            FeaturesHeader.Visibility = Visibility.Collapsed;
            FeaturesText.Visibility = Visibility.Collapsed;
        }

        AddDetailRow("File Name", _resource.FileName);
        AddDetailRow("File Size", Format.FileSize(_resource.FileSize));
        AddDetailRow("Category", _resource.Category);

        if (_resource.SupportedVersions.Any())
            AddDetailRow("Supported Versions", string.Join(", ", _resource.SupportedVersions));

        AddDetailRow("Installation", DescribeInstallation(_resource.InstallationType));
        AddDetailRow("Published", Format.Date(_resource.PublishedAt));
        AddDetailRow("Updated", Format.Date(_resource.UpdatedAt));
        AddDetailRow("License", _resource.IsFree ? "Free" : "Paid");
        AddDetailRow("Checksum", _resource.Checksum);

        if (_resource.Tags.Any())
        {
            foreach (var tag in _resource.Tags)
            {
                var tagBorder = new Border
                {
                    Background = Palette.Glass,
                    BorderBrush = Palette.GlassBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 8, 8)
                };

                tagBorder.Child = new TextBlock
                {
                    Text = $"#{tag}",
                    FontSize = 12,
                    Foreground = Palette.TextSecondary
                };

                TagsPanel.Children.Add(tagBorder);
            }
        }
        else
        {
            TagsHeader.Visibility = Visibility.Collapsed;
        }

        FileSizeText.Text = $"Download size: {Format.FileSize(_resource.FileSize)}";

        // Кнопка Install показывается сразу, если способ установки поддерживается,
        // и после скачивания — если требуется сначала скачать файл.
        InstallButton.Visibility = CanInstallWithoutDownload()
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(_resource.DownloadUrl))
            DownloadButton.IsEnabled = false;
    }

    /// <summary>Восстанавливает состояние кнопок, если ресурс уже скачивался ранее.</summary>
    private void RestoreDownloadState()
    {
        var existing = _loadedService.GetFilePath(_resource.Id);
        if (existing == null) return;

        _downloadedFilePath = existing;
        DownloadButton.Content = "Downloaded";
        InstallButton.Visibility = Visibility.Visible;
    }

    private bool CanInstallWithoutDownload() =>
        _resource.InstallationType == InstallationType.Manual;

    private static string DescribeInstallation(InstallationType type) => type switch
    {
        InstallationType.CopyToFolder => "Copy to a folder you choose",
        InstallationType.Script => "Automatic script installation",
        InstallationType.Installer => "Runs a separate installer",
        InstallationType.Extension => "Extension manager",
        _ => "Manual installation"
    };

    private void AddDetailRow(string label, string? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Palette.TextPrimary,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        DetailsPanel.Children.Add(grid);
    }

    /// <summary>Диалог с владельцем — основным окном, а не отдельной страницей.</summary>
    private MessageBoxResult Message(
        string text, string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None) =>
        MessageBox.Show(Window.GetWindow(this), text, title, button, image);

    private void BackButton_Click(object sender, RoutedEventArgs e) => _onClosed();

    // ---------------------------------------------------------------- Загрузка

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // Существующий файл не перезаписываем без подтверждения
        var existing = Path.Combine(
            _downloadService.GetDownloadFolder(),
            string.IsNullOrWhiteSpace(_resource.FileName) ? $"{_resource.Slug}.zip" : _resource.FileName);

        var overwrite = false;
        if (File.Exists(existing))
        {
            var answer = Message(
                $"\"{Path.GetFileName(existing)}\" is already in your download folder.\n\nDownload it again and replace the file?",
                "File Already Exists",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;
            overwrite = true;
        }

        button.IsEnabled = false;
        button.Content = "Downloading...";
        CancelButton.Visibility = Visibility.Visible;
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgress>(p =>
        {
            StatusText.Text = p.TotalBytes > 0
                ? $"Downloading... {p.Percent}%"
                : "Downloading...";
        });

        try
        {
            var result = await _downloadService.DownloadResourceAsync(
                _resource, overwrite, progress, _downloadCts.Token);

            _downloadedFilePath = result.FilePath;
            _loadedService.MarkDownloaded(_resource.Id, result.FilePath);

            StatusText.Text = result.Checksum switch
            {
                ChecksumStatus.Verified => "Downloaded and checksum verified",
                ChecksumStatus.NotProvided => "Downloaded (no checksum provided)",
                _ => "Downloaded"
            };

            var message = $"Download complete.\n\nFile: {Path.GetFileName(result.FilePath)}\nFolder: {Path.GetDirectoryName(result.FilePath)}";

            if (result.Checksum == ChecksumStatus.NotProvided)
                message += "\n\nThis resource does not publish a checksum, so the file could not be verified.";

            Message(message, "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);

            button.Content = "Downloaded";
            InstallButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Download canceled";
            button.Content = "Download";
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Message($"Download failed.\n\n{ex.Message}", "Download Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            StatusText.Text = "Download failed";
            button.Content = "Download";
            button.IsEnabled = true;
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        StatusText.Text = "Canceling...";
    }

    // ---------------------------------------------------------------- Установка

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        if (string.IsNullOrEmpty(_downloadedFilePath) &&
            _resource.InstallationType != InstallationType.Manual)
        {
            Message("Download the resource first, then install it.", "Not Downloaded",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Установка затрагивает файлы Adobe — спрашиваем подтверждение
        var confirmation = Message(
            $"Editor Helper will install \"{_resource.Title}\".\n\n" +
            "Only proceed if you trust this resource and want it placed in your " +
            $"{_resource.Application} folders.",
            "Confirm Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes) return;

        button.IsEnabled = false;
        button.Content = "Installing...";

        try
        {
            var result = await _installationService.InstallResourceAsync(_resource, _downloadedFilePath ?? "");

            if (!result.Success)
            {
                Message(result.Message, "Installation Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                button.Content = "Install";
                button.IsEnabled = true;
                return;
            }

            if (result.RequiresManualSteps)
            {
                ShowInstructions(result.Message, result.Instructions ?? "");
                button.Content = "Install";
                button.IsEnabled = true;
            }
            else
            {
                if (result.InstalledPath != null)
                    _loadedService.MarkInstalled(_resource.Id, result.InstalledPath);

                Message(result.Message, "Installation Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                button.Content = "Installed";
            }

            StatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            Message($"Installation failed.\n\n{ex.Message}", "Installation Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            button.Content = "Install";
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Инструкция ручной установки показывается оверлеем на этой же странице:
    /// текст длинный, его удобнее читать и копировать, чем в MessageBox.
    /// </summary>
    private void ShowInstructions(string summary, string instructions)
    {
        InstructionsSummary.Text = summary;
        InstructionsBody.Text = instructions;
        InstructionsBody.ScrollToHome();
        CopyInstructionsButton.Content = "Copy instructions";
        InstructionsOverlay.Visibility = Visibility.Visible;

        if (Motion.Enabled)
        {
            InstructionsOverlay.Opacity = 0;
            InstructionsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(Motion.Base)) { EasingFunction = Motion.EaseOut });
        }
    }

    private void CloseInstructionsButton_Click(object sender, RoutedEventArgs e) =>
        InstructionsOverlay.Visibility = Visibility.Collapsed;

    private void CopyInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(InstructionsBody.Text);
            // Ненавязчивое подтверждение копирования
            CopyInstructionsButton.Content = "Copied ✓";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.6)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyInstructionsButton.Content = "Copy instructions";
            };
            timer.Start();
        }
        catch { /* буфер обмена занят */ }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) => OpenAdobeFolder();

    private void OpenAdobeFolder()
    {
        try
        {
            var folder = _installationService.GetSuggestedFolder(_resource.Application);
            if (folder == null || !Directory.Exists(folder))
            {
                Message($"Folder not found:\n{folder}\n\nCreate it manually or check that {_resource.Application} is installed.",
                    "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Message($"Could not open the folder.\n\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- Избранное

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        FavoriteButton.IsEnabled = false;
        try
        {
            if (_favoritesService.IsServerBacked)
                await _favoritesService.ToggleFavoriteAsync(_resource.Id);
            else
                _favoritesService.ToggleFavorite(_resource.Id);
            UpdateFavoriteButton();
            Motion.Pulse(FavoriteButton, _favoritesService.IsFavorite(_resource.Id) ? 1.3 : 1.12);
        }
        catch (Exception ex)
        {
            Message(ex.Message, "Favorites", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { FavoriteButton.IsEnabled = true; }
    }

    private void UpdateFavoriteButton()
    {
        var isFavorite = _favoritesService.IsFavorite(_resource.Id);
        FavoriteButton.Content = isFavorite ? "♥" : "♡";
        FavoriteButton.Foreground = isFavorite ? Palette.Accent : Palette.TextSecondary;
    }
}
