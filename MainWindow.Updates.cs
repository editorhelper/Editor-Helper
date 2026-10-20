using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper;

/// <summary>
/// Раздел обновлений в Settings.
///
/// Канал обновлений решает сервер, а не клиент: бета-переключатель активен
/// только когда сервер подтвердил право аккаунта (BetaAllowed в ответе
/// /api/v1/updates/check). Пользователь без разрешения не видит ни
/// бета-версий, ни их описаний — и не может включить бету локальной
/// настройкой, потому что сервер просто не отдаст бета-метаданные.
///
/// Проверка и загрузка не блокируют интерфейс. Установка — Inno Setup поверх
/// закрытого приложения: установщик сам закрывает его и запускает заново.
/// </summary>
public partial class MainWindow
{
    private UpdateRelease? _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private bool _updateBusy;
    private bool _betaAllowed;

    private UpdateService? CreateUpdateService() =>
        _sessionService is { IsAuthenticated: true } authed
            ? new UpdateService(authed.BaseUrl, authed)
            : null;

    /// <summary>Строит блок обновлений: состояние, заметки и действия.</summary>
    private StackPanel BuildUpdateSection()
    {
        var stack = new StackPanel();

        // Ссылка на страницу релиза заполняется результатом проверки;
        // объявлена первой, потому что кнопка-ссылка захватывает её замыканием
        string releasePageUrl = "";

        var service = CreateUpdateService();

        if (service == null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Обновления проверяются через сервер каталога. Войдите в аккаунт, " +
                       "чтобы проверять и устанавливать обновления.",
                FontSize = 12.5,
                Foreground = Palette.TextSecondary,
                TextWrapping = TextWrapping.Wrap
            });
            return stack;
        }

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
        statusPanel.Children.Add(new TextBlock
        {
            Text = $"Текущая версия: {UpdateService.CurrentVersion}",
            FontSize = 12.5,
            Foreground = Palette.TextSecondary
        });
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

        // Бета-канал: переключатель появляется только при серверном праве
        var betaCheck = new CheckBox
        {
            Content = "Бета-канал: проверять предрелизные версии",
            IsChecked = _settings.UpdateBetaChannel,
            IsEnabled = false,
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Доступ выдаётся администратором сервера"
        };
        var betaHint = new TextBlock
        {
            Text = "Бета-канал закрыт: доступ выдаёт администратор.",
            FontSize = 11,
            Foreground = Palette.TextMuted,
            Margin = new Thickness(0, 0, 0, 14)
        };
        stack.Children.Add(betaCheck);
        stack.Children.Add(betaHint);

        betaCheck.Checked += (_, _) => { _settings.UpdateBetaChannel = true; _settings.Save(); };
        betaCheck.Unchecked += (_, _) => { _settings.UpdateBetaChannel = false; _settings.Save(); };

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
        releaseLink.Click += (_, _) => OpenInBrowser(releasePageUrl);
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

        // Возврат на Stable: показывается, если установлена бета и права нет
        var downgradeButton = new Button
        {
            Content = "Вернуться на стабильную версию",
            Style = (Style)FindResource("GlassButton"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 0)
        };

        checkButton.Click += async (_, _) => await CheckForUpdatesAsync();
        actions.Children.Add(checkButton);
        updateButton.Click += async (_, _) => await InstallUpdateAsync();
        actions.Children.Add(updateButton);

        downgradeButton.Click += async (_, _) => await InstallUpdateAsync();
        stack.Children.Add(downgradeButton);

        // Первая проверка — тихая, в фоне
        _ = QuietCheckAsync();

        return stack;

        // ------------------------------------------------------- локальные функции

        async Task QuietCheckAsync()
        {
            try
            {
                var result = await service.CheckAsync(_settings.UpdateBetaChannel, CancellationToken.None);
                ShowUpdateResult(result);
            }
            catch
            {
                // Тихая проверка не должна пугать: ручная покажет причину
            }
        }

        async Task CheckForUpdatesAsync()
        {
            if (_updateBusy) return;

            _updateBusy = true;
            checkButton.IsEnabled = false;
            statusText.Text = "Проверяем обновления…";

            try
            {
                var result = await service.CheckAsync(_settings.UpdateBetaChannel, CancellationToken.None);
                ShowUpdateResult(result);
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

        void ShowUpdateResult(UpdateService.UpdateCheck result)
        {
            // Право на бету пришло с сервера: переключатель оживает только здесь
            _betaAllowed = result.BetaAllowed;
            betaCheck.IsEnabled = _betaAllowed;
            betaHint.Text = _betaAllowed
                ? "Доступ к бета-каналу выдан администратором."
                : "Бета-канал закрыт: доступ выдаёт администратор.";

            _pendingUpdate = null;
            releasePageUrl = "";

            // Бета-канал включён и разрешён: обновлением считается бета
            var betaCandidate = _settings.UpdateBetaChannel && _betaAllowed ? result.Beta : null;
            var useBeta = betaCandidate != null;

            var candidate = useBeta ? betaCandidate : result.Stable;

            if (candidate != null && UpdateService.IsNewer(candidate.Version, UpdateService.CurrentVersion))
            {
                _pendingUpdate = candidate;
                releasePageUrl = candidate.HtmlUrl;
                statusText.Text = $"Доступна версия {candidate.Version}" +
                    (candidate.Prerelease ? " (предрелиз)" : "") +
                    $" — установлена {UpdateService.CurrentVersion}.";

                notesText.Text = string.IsNullOrWhiteSpace(candidate.Notes)
                    ? "Описание изменений не приложено к этому релизу."
                    : candidate.Notes;
                notesCard.Visibility = Visibility.Visible;
                updateButton.Visibility = Visibility.Visible;
                updateButton.Content = "Обновить сейчас";
            }
            else
            {
                // Установлена бета, а права уже нет: предлагаем вернуться на
                // стабильную версию, если она новее установленной
                var onBeta = CurrentIsBeta();
                var stableNewer = result.Stable != null &&
                    UpdateService.IsNewer(result.Stable.Version, UpdateService.CurrentVersion);

                if (onBeta && stableNewer && result.Stable != null)
                {
                    _pendingUpdate = result.Stable;
                    releasePageUrl = result.Stable.HtmlUrl;
                    statusText.Text = $"Бета-доступ отозван. Доступна стабильная версия {result.Stable.Version} " +
                        $"— установлена {UpdateService.CurrentVersion} (бета).";
                    notesText.Text = result.Stable.Notes;
                    notesCard.Visibility = Visibility.Visible;
                    updateButton.Visibility = Visibility.Collapsed;
                    downgradeButton.Visibility = Visibility.Visible;
                }
                else if (onBeta && !_betaAllowed)
                {
                    statusText.Text = $"Доступ к бета-каналу отозван. Установлена версия {UpdateService.CurrentVersion} (бета). " +
                        "Когда выйдет более новая стабильная версия, здесь появится предложение обновиться.";
                    notesCard.Visibility = Visibility.Collapsed;
                    updateButton.Visibility = Visibility.Collapsed;
                    downgradeButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    statusText.Text = $"Обновлений нет — актуальна версия {UpdateService.CurrentVersion}." +
                        (_settings.UpdateBetaChannel && _betaAllowed ? " (Бета-канал включён.)" : "");
                    notesCard.Visibility = Visibility.Collapsed;
                    updateButton.Visibility = Visibility.Collapsed;
                    downgradeButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Установленная версия — бета: суффикс -beta.N в номере версии
        /// сборки. Определяется по локальному номеру, страницы GitHub не нужно.
        /// </summary>
        static bool CurrentIsBeta() => UpdateService.CurrentVersion.Contains("-beta", StringComparison.OrdinalIgnoreCase);

        async Task InstallUpdateAsync()
        {
            if (_updateBusy || _pendingUpdate == null) return;

            var release = _pendingUpdate;
            _updateBusy = true;
            updateButton.IsEnabled = false;
            downgradeButton.IsEnabled = false;
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
                var destination = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EditorHelper", "updates");
                Directory.CreateDirectory(destination);

                var installerPath = await service.DownloadInstallerAsync(
                    release, destination, progress, _updateCts.Token);

                progressText.Text = "Обновление загружено и проверено. Запускаем установку…";
                StatusText.Text = $"Installing update {release.Version}…";

                // Установщик закрывает приложение сам (Inno: CloseApplications)
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

            void ResetUpdateUi()
            {
                _updateBusy = false;
                _updateCts?.Dispose();
                _updateCts = null;
                updateButton.IsEnabled = true;
                downgradeButton.IsEnabled = true;
                checkButton.IsEnabled = true;
                progressCard.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static void OpenInBrowser(string url)
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
