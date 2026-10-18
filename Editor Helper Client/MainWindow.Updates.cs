using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper;

/// <summary>
/// Раздел обновлений в Settings: текущая и доступная версии, заметки релиза,
/// проверка, загрузка и запуск установщика из GitHub Releases.
///
/// Проверка и загрузка не блокируют интерфейс: кнопки переводятся в состояние
/// занятости, остальное приложение продолжает работать. Установка — Inno Setup
/// поверх закрытого приложения: установщик сам закрывает его (CloseApplications)
/// и запускает заново. Токен GitHub здесь не нужен и не хранится: чтение
/// публичных релизов анонимно, публикацию делает владелец через Actions.
/// </summary>
public partial class MainWindow
{
    private UpdateService? _updateService;
    private UpdateRelease? _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private bool _updateBusy;

    private UpdateService CreateUpdateService()
    {
        // Сервис пересоздаётся при смене адреса: старый HttpClient ссылается
        // на прежний репозиторий
        return new UpdateService(_settings.UpdateRepository);
    }

    /// <summary>Строит блок обновлений: состояние, заметки и действия.</summary>
    private StackPanel BuildUpdateSection()
    {
        var stack = new StackPanel();

        // Адрес репозитория: без него раздел сообщает, что не настроен
        var repoFrame = new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var repoGrid = new Grid();
        repoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        repoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        repoGrid.Children.Add(new TextBlock
        {
            Text = "github.com/",
            FontSize = 12.5,
            Foreground = Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        });

        var repoBox = new TextBox
        {
            Text = RepositoryShortForm(_settings.UpdateRepository),
            Style = (Style)FindResource("GlassTextBox"),
            Padding = new Thickness(4, 9, 12, 9),
            ToolTip = "Адрес репозитория с релизами, например owner/editorhelper"
        };
        repoBox.TextChanged += (_, _) => repoBox.Tag = repoBox.Text;
        Grid.SetColumn(repoBox, 1);
        repoGrid.Children.Add(repoBox);
        repoFrame.Child = repoGrid;
        stack.Children.Add(repoFrame);

        var betaCheck = new CheckBox
        {
            Content = "Бета-канал: получать предрелизы вместе со стабильными",
            IsChecked = _settings.UpdateIncludePrereleases,
            Margin = new Thickness(0, 0, 0, 14)
        };
        betaCheck.Checked += (_, _) =>
        {
            _settings.UpdateIncludePrereleases = true;
            _settings.Save();
        };
        betaCheck.Unchecked += (_, _) =>
        {
            _settings.UpdateIncludePrereleases = false;
            _settings.Save();
        };
        stack.Children.Add(betaCheck);

        // Статус: версии и результат последней проверки
        var statusCard = new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var statusPanel = new StackPanel();
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal };
        versionRow.Children.Add(new TextBlock
        {
            Text = $"Текущая версия: {UpdateService.CurrentVersion}",
            FontSize = 12.5,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });
        statusPanel.Children.Add(versionRow);
        var statusText = new TextBlock
        {
            Text = "Проверка не выполнялась в этом сеансе.",
            FontSize = 12,
            Foreground = Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        statusPanel.Children.Add(statusText);
        statusCard.Child = statusPanel;
        stack.Children.Add(statusCard);

        // Заметки релиза: показываются, когда найдено обновление
        var notesCard = new Border
        {
            Background = Palette.CardSurface,
            BorderBrush = Palette.CardBorderRest,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed
        };
        var notesPanel = new StackPanel();
        notesPanel.Children.Add(new TextBlock
        {
            Text = "Что нового",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var notesText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220
        };
        notesPanel.Children.Add(notesText);

        var releaseLink = new Button
        {
            Content = "Открыть страницу релиза",
            Style = (Style)FindResource("GlassButtonSmall"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        releaseLink.Click += (_, _) => OpenInBrowser(_pendingUpdate?.HtmlUrl);
        notesPanel.Children.Add(releaseLink);
        notesCard.Child = notesPanel;
        stack.Children.Add(notesCard);

        // Прогресс: честный индикатор по фактическим байтам
        var progressCard = new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed
        };
        var progressPanel = new StackPanel();
        var progressText = new TextBlock
        {
            Text = "Загрузка обновления…",
            FontSize = 12,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 0, 0, 8)
        };
        progressPanel.Children.Add(progressText);

        var progressTrack = new Border
        {
            Height = 4,
            Background = Palette.GlassBorder,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };
        var progressFill = new Border
        {
            Width = 0,
            Background = Palette.AccentGradient(),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        progressTrack.Child = progressFill;
        progressPanel.Children.Add(progressTrack);
        progressCard.Child = progressPanel;
        stack.Children.Add(progressCard);

        // Действия. Кнопки объявлены до обработчиков: локальные функции ниже
        // ссылаются на них, и использование до объявления для локальной
        // переменной — ошибка компиляции
        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var checkButton = new Button
        {
            Content = "Проверить обновления",
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(0, 0, 12, 0)
        };

        var updateButton = new Button
        {
            Content = "Обновить сейчас",
            Style = (Style)FindResource("GlassButton"),
            Visibility = Visibility.Collapsed
        };

        checkButton.Click += async (_, _) => await CheckForUpdatesAsync();
        actions.Children.Add(checkButton);
        updateButton.Click += async (_, _) => await InstallUpdateAsync();
        actions.Children.Add(updateButton);
        stack.Children.Add(actions);

        // Первая проверка — тихая, в фоне: результат появится в статусе
        if (_updateService is { IsConfigured: true })
            _ = QuietCheckAsync();

        return stack;

        // ------------------------------------------------------- локальные функции

        async Task QuietCheckAsync()
        {
            try
            {
                var release = await _updateService!.CheckForUpdateAsync(
                    _settings.UpdateIncludePrereleases, CancellationToken.None);
                ShowUpdateResult(release);
            }
            catch
            {
                // Тихая проверка не должна пугать: ручная покажет причину
            }
        }

        async Task CheckForUpdatesAsync()
        {
            if (_updateBusy) return;

            // Адрес мог измениться: применяем и сохраняем до проверки
            var entered = RepositoryFullForm(repoBox.Text);
            if (entered != _settings.UpdateRepository)
            {
                _settings.UpdateRepository = entered;
                _settings.Save();
            }

            _updateService = CreateUpdateService();

            if (!_updateService.IsConfigured)
            {
                statusText.Text = "Обновления не настроены: укажите адрес репозитория (owner/repo).";
                notesCard.Visibility = Visibility.Collapsed;
                updateButton.Visibility = Visibility.Collapsed;
                return;
            }

            _updateBusy = true;
            checkButton.IsEnabled = false;
            statusText.Text = "Проверяем обновления…";

            try
            {
                var release = await _updateService.CheckForUpdateAsync(
                    _settings.UpdateIncludePrereleases, CancellationToken.None);
                ShowUpdateResult(release);
            }
            catch (Exception ex)
            {
                statusText.Text = "Не удалось проверить обновления: " + ex.Message;
                notesCard.Visibility = Visibility.Collapsed;
                updateButton.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _updateBusy = false;
                checkButton.IsEnabled = true;
            }
        }

        void ShowUpdateResult(UpdateRelease? release)
        {
            _pendingUpdate = release;

            if (release == null)
            {
                statusText.Text = $"Обновлений нет — актуальна версия {UpdateService.CurrentVersion}." +
                    (_settings.UpdateIncludePrereleases ? " (Бета-канал включён.)" : "");
                notesCard.Visibility = Visibility.Collapsed;
                updateButton.Visibility = Visibility.Collapsed;
                return;
            }

            statusText.Text = $"Доступна версия {release.Version}" +
                (release.Prerelease ? " (предрелиз)" : "") +
                $" — установлена {UpdateService.CurrentVersion}.";

            notesText.Text = string.IsNullOrWhiteSpace(release.Notes)
                ? "Описание изменений не приложено к этому релизу."
                : release.Notes;
            notesCard.Visibility = Visibility.Visible;
            updateButton.Visibility = Visibility.Visible;
        }

        async Task InstallUpdateAsync()
        {
            if (_updateBusy || _pendingUpdate == null || _updateService == null) return;

            var release = _pendingUpdate;
            _updateBusy = true;
            updateButton.IsEnabled = false;
            checkButton.IsEnabled = false;
            progressCard.Visibility = Visibility.Visible;
            progressFill.Width = 0;
            progressText.Text = "Загрузка обновления…";

            _updateCts = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                progressFill.Width = p.TotalBytes > 0
                    ? progressTrack.ActualWidth * p.ReceivedBytes / p.TotalBytes
                    : 0;
                progressText.Text = p.TotalBytes > 0
                    ? $"Загрузка… {Format.FileSize(p.ReceivedBytes)} из {Format.FileSize(p.TotalBytes)}"
                    : $"Загрузка… {Format.FileSize(p.ReceivedBytes)}";
            });

            try
            {
                var destination = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EditorHelper", "updates");
                Directory.CreateDirectory(destination);

                var installerPath = await _updateService.DownloadInstallerAsync(
                    release, destination, progress, _updateCts.Token);

                progressText.Text = "Обновление загружено и проверено. Запускаем установку…";
                StatusText.Text = $"Installing update {release.Version}…";

                // Установщик закрывает приложение сам (Inno: CloseApplications).
                // Небольшая пауза даёт установщику подняться раньше, чем мы
                // начнём сворачивать окно.
                await Task.Delay(400);

                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });

                Close();
            }
            catch (OperationCanceledException)
            {
                progressText.Text = "Загрузка отменена.";
                ShowToast("Загрузка обновления отменена", Palette.Secondary);
                ResetUpdateUi();
            }
            catch (InvalidDataException ex)
            {
                progressText.Text = ex.Message;
                ShowToast("Обновление отклонено: целостность файла не подтверждена", Palette.Secondary);
                ResetUpdateUi();
            }
            catch (Exception ex)
            {
                progressText.Text = "Не удалось загрузить обновление: " + ex.Message;
                ShowToast("Ошибка обновления: " + ex.Message, Palette.Secondary);
                ResetUpdateUi();
            }
        }

        void ResetUpdateUi()
        {
            _updateBusy = false;
            _updateCts?.Dispose();
            _updateCts = null;
            updateButton.IsEnabled = true;
            checkButton.IsEnabled = true;
            progressCard.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>owner/repo из полного адреса; пустая строка остаётся пустой.</summary>
    private static string RepositoryShortForm(string url)
    {
        var (owner, repo) = UpdateService.ParseRepository(url);
        return owner.Length > 0 ? $"{owner}/{repo}" : "";
    }

    /// <summary>Полный адрес из введённого owner/repo или полного URL.</summary>
    private static string RepositoryFullForm(string entered)
    {
        var (owner, repo) = UpdateService.ParseRepository(entered);
        if (owner.Length == 0)
            return entered.Trim();
        return $"https://github.com/{owner}/{repo}";
    }

    private static void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Браузер не открылся: адрес виден в подсказке кнопки
        }
    }
}
