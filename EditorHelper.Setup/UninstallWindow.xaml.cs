using System.Windows.Media;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using EditorHelper.Setup.Logic;
using EditorHelper.Setup.Ui;

namespace EditorHelper.Setup;

/// <summary>
/// Деинсталлятор в том же дизайне, что и установщик. Запускается из
/// «Программы и компоненты» или ярлыка в меню «Пуск» с аргументом --uninstall.
///
/// Удаляет только то, что создал установщик: файлы приложения, ярлыки,
/// запись в реестре. Данные пользователя (%LOCALAPPDATA%\EditorHelper)
/// по умолчанию остаются — это отдельное решение владельца.
/// </summary>
public partial class UninstallWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly UninstallEngine _engine;
    private bool _running;

    public UninstallWindow()
    {
        InitializeComponent();
        _engine = new UninstallEngine();

        SourceInitialized += (_, _) =>
            ApplyRoundedCorners(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Loaded += (_, _) =>
        {
            var install = _engine.FindInstall();
            if (install == null)
            {
                // Приложения нет (файлы уже удалены вручную) — чистим ярлыки и запись
                FolderText.Text = "Установка не найдена: файлы приложения уже отсутствуют.\n" +
                    "Удалятся оставшиеся ярлыки и запись в списке программ.";
            }
            else
            {
                FolderText.Text = $"Папка: {install}";
            }
        };
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;

        ConfirmPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;

        // Плавный переход
        if (Motion.Enabled)
        {
            ProgressPanel.Opacity = 0;
            ProgressPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.EaseOut });
        }

        var progress = new Progress<UninstallProgress>(p =>
        {
            ProgressFill.Width = p.Percent >= 0
                ? ((System.Windows.Controls.Border)ProgressFill.Parent).ActualWidth * p.Percent / 100.0
                : ProgressFill.ActualWidth;
            if (p.StageText != null) ProgressDetail.Text = p.StageText;
        });

        try
        {
            await Task.Run(() => _engine.Uninstall(progress));

            ProgressPanel.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
            DoneNote.Text = "Файлы приложения и ярлыки удалены.\n" +
                "Настройки и данные аккаунта сохранены в %LOCALAPPDATA%\\EditorHelper.";
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    // ---------------------------------------------------------------- Окно

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }
}
