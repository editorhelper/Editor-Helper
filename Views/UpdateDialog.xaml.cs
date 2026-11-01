using System.IO;
using System.Windows;
using System.Windows.Controls;
using EditorHelper.Services;

namespace EditorHelper.Views;

/// <summary>
/// Предложение обновления в стиле приложения: карточка с версиями, заметками
/// релиза и прогрессом загрузки. «Позже» и закрытие окна — отложить до
/// следующего сеанса или срабатывания таймера; DialogResult true только при
/// явном «Обновить сейчас».
///
/// Диалог сам скачивает и запускает установщик: пользователь остаётся в том
/// же окне до самого запуска установки. Установщик закрывает приложение сам
/// (InstallEngine.TryCloseRunningApp), поэтому запуск дистрибутива —
/// последняя операция, после неё диалог закрывается вместе с приложением.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateRelease _release;
    private readonly UpdateService _service;
    private CancellationTokenSource? _downloadCts;
    private bool _busy;

    public UpdateDialog(UpdateRelease release, UpdateService service)
    {
        InitializeComponent();

        _release = release;
        _service = service;

        CurrentVersionText.Text = UpdateService.CurrentVersion;
        NewVersionText.Text = release.Version + (release.Prerelease ? " (предрелиз)" : "");
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "Описание изменений не приложено к этому релизу."
            : release.Notes;

        Loaded += (_, _) => UpdateButton.Focus();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => _ = DownloadAndRunAsync();

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _downloadCts?.Cancel();
            return;
        }
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _downloadCts?.Cancel();
        base.OnClosing(e);
    }

    /// <summary>
    /// Загрузка и запуск: приложение остаётся работоспособным при любой ошибке —
    /// диалог возвращается к кнопкам, установленная версия не трогается.
    /// </summary>
    private async Task DownloadAndRunAsync()
    {
        if (_busy) return;
        _busy = true;

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Отмена";
        LaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressFill.Width = 0;
        ProgressText.Text = "Скачивание…";

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<UpdateProgress>(p =>
        {
            ProgressFill.Width = p.TotalBytes > 0
                ? ((Border)ProgressFill.Parent).ActualWidth * p.ReceivedBytes / p.TotalBytes
                : 0;
            ProgressText.Text = p.TotalBytes > 0
                ? $"Скачивание… {Ui.Format.FileSize(p.ReceivedBytes)} из {Ui.Format.FileSize(p.TotalBytes)}"
                : $"Скачивание… {Ui.Format.FileSize(p.ReceivedBytes)}";
        });

        try
        {
            var destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EditorHelper", "updates");
            Directory.CreateDirectory(destination);

            var installerPath = await _service.DownloadInstallerAsync(
                _release, destination, progress, _downloadCts.Token);

            ProgressText.Text = "Проверка… целостность подтверждена";
            ProgressFill.Width = ((Border)ProgressFill.Parent).ActualWidth;
            await Task.Delay(300);
            ProgressText.Text = "Установка… приложение закроется";

            await Task.Delay(400);

            DialogResult = true;

            // Установщик (SFX) закрывает EditorHelper сам и запускает его после
            // установки: это и есть отдельный updater-процесс, он уже оттестирован.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            Close();
        }
        catch (OperationCanceledException)
        {
            ResetToIdle("Загрузка отменена. Приложение продолжает работу на текущей версии.");
        }
        catch (InvalidDataException ex)
        {
            ResetToIdle("Обновление отклонено: " + ex.Message);
        }
        catch (Exception ex)
        {
            ResetToIdle("Не удалось загрузить обновление: " + ex.Message +
                        " Приложение продолжает работу на текущей версии.");
        }
    }

    private void ResetToIdle(string message)
    {
        _busy = false;
        _downloadCts?.Dispose();
        _downloadCts = null;

        UpdateButton.IsEnabled = true;
        UpdateButton.Content = "Обновить сейчас";
        LaterButton.IsEnabled = true;
        ProgressText.Text = message;
    }
}
