using System.Windows.Media;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using System.Windows.Media.Animation;

using EditorHelper.Setup.Logic;
using EditorHelper.Setup.Ui;

namespace EditorHelper.Setup;

/// <summary>
/// Мастер установки: Welcome -> Настройки -> Установка -> Завершение.
///
/// Интерфейс повторяет приложение Premium Cosmic (безрамочное окно, живой
/// космический фон, стеклянные карточки, градиентные кнопки). Логика —
/// <see cref="InstallEngine"/>: копирование файлов, ярлыки, запись в реестр,
/// обработка уже установленной версии и запуск приложения.
/// </summary>
public partial class SetupWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly InstallEngine _engine;
    private bool _installRunning;
    private bool _installed;
    private string? _failedStage;

    public SetupWindow()
    {
        InitializeComponent();

        _engine = new InstallEngine();

        // Установка по умолчанию: Program Files, если пишется; иначе LocalAppData.
        // Привилегий админа установщик не требует: self-contained приложение
        // кладётся в папку и не пишет системных разделов реестра
        PathBox.Text = _engine.DefaultInstallFolder;
        VersionLabel.Text = $"версия {_engine.ProductVersion}";
        UpgradeNoteText.Text = "";

        SourceInitialized += (_, _) =>
            ApplyRoundedCorners(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Loaded += (_, _) =>
        {
            BuildStepBar();
            DetectExistingInstall();
        };
    }

    // ---------------------------------------------------------------- Навигация

    private enum Stage { Welcome = 0, Options = 1, Progress = 2, Done = 3 }

    private Stage _stage = Stage.Welcome;
    private readonly List<Border> _stepDots = new();

    private void BuildStepBar()
    {
        foreach (var child in StepBar.Children.OfType<Border>().ToList())
            StepBar.Children.Remove(child);
        _stepDots.Clear();

        // Точки этапа: активная ярче, пройденная — заполнена
        for (int i = 0; i < 4; i++)
        {
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = i == 0 ? (Brush)FindResource("GlassBorderBright") : (Brush)FindResource("GlassBorder"),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _stepDots.Add(dot);
            StepBar.Children.Add(dot);
        }
    }

    private void GoTo(Stage stage)
    {
        _stage = stage;

        WelcomeScreen.Visibility = stage == Stage.Welcome ? Visibility.Visible : Visibility.Collapsed;
        OptionsScreen.Visibility = stage == Stage.Options ? Visibility.Visible : Visibility.Collapsed;
        ProgressScreen.Visibility = stage == Stage.Progress ? Visibility.Visible : Visibility.Collapsed;
        DoneScreen.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _stepDots.Count; i++)
        {
            var passed = (int)stage >= i;
            _stepDots[i].Background = i == (int)stage
                ? (Brush)FindResource("GlassBorderBright")
                : passed ? (Brush)FindResource("Accent") : (Brush)FindResource("GlassBorder");
            // Немного увеличиваем активную точку — мягкий акцент без шума
            _stepDots[i].Width = i == (int)stage ? 9 : 7;
            _stepDots[i].Height = i == (int)stage ? 9 : 7;
            _stepDots[i].CornerRadius = new CornerRadius(_stepDots[i].Width / 2);
        }

        if (stage == Stage.Welcome || stage == Stage.Done)
            StepBar.Visibility = Visibility.Collapsed;
        else
            StepBar.Visibility = Visibility.Visible;

        FadeIn(WelcomeScreen, OptionsScreen, ProgressScreen, DoneScreen);
    }

    private static void FadeIn(params FrameworkElement[] screens)
    {
        foreach (var screen in screens)
        {
            if (screen.Visibility != Visibility.Visible) continue;
            if (!Motion.Enabled) { screen.Opacity = 1; continue; }
            screen.Opacity = 0;
            screen.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.EaseOut });
        }
    }

    // ---------------------------------------------------------------- Welcome

    private void WelcomeInstallButton_Click(object sender, RoutedEventArgs e) =>
        GoTo(Stage.Options);

    private void WelcomeOptionsButton_Click(object sender, RoutedEventArgs e) =>
        GoTo(Stage.Options);

    // ---------------------------------------------------------------- Options

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog — нативный диалог папок WPF, добавленный в .NET 8
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для установки Editor Helper",
            InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : null
        };

        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePathHint();
    }

    private void UpdatePathHint()
    {
        var path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            PathHint.Text = "";
            OptionsInstallButton.IsEnabled = false;
            return;
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            var drive = new DriveInfo(root!);
            var freeGb = drive.IsReady ? drive.TotalFreeSpace / 1024.0 / 1024 / 1024 : 0;
            var needMb = _engine.RequiredMegabytes;

            PathHint.Text = drive.IsReady
                ? $"Требуется ~{needMb} МБ · свободно {freeGb:0.0} ГБ на {root}"
                : $"Требуется ~{needMb} МБ";
            OptionsInstallButton.IsEnabled = drive.IsReady && freeGb * 1024 > needMb;
        }
        catch
        {
            PathHint.Text = "";
            OptionsInstallButton.IsEnabled = false;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoTo(Stage.Welcome);

    private void OptionsInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installRunning) return;
        _installRunning = true;
        _failedStage = null;

        // Блокируем повторный запуск и переходы, пока установка идёт
        OptionsInstallButton.IsEnabled = false;
        ErrorPanel.Visibility = Visibility.Collapsed;

        GoTo(Stage.Progress);
        ProgressFill.Width = 0;
        ProgressTitle.Text = "Установка Editor Helper";
        ProgressDetail.Text = "Подготовка…";
        ProgressFile.Text = "";
        ProgressPercent.Text = "0%";

        _ = RunInstallAsync();
    }

    // ---------------------------------------------------------------- Установка

    private async Task RunInstallAsync()
    {
        var options = new InstallOptions
        {
            InstallFolder = PathBox.Text.Trim(),
            CreateDesktopShortcut = DesktopIconCheck.IsChecked == true,
            CreateStartMenuShortcut = StartMenuIconCheck.IsChecked == true
        };

        var progress = new Progress<InstallProgress>(p =>
        {
            // Прогресс честный: по скопированным байтам от общего объёма payload
            ProgressFill.Width = p.Percent >= 0
                ? ((System.Windows.Controls.Border)ProgressFill.Parent).ActualWidth * p.Percent / 100.0
                : ProgressFill.ActualWidth;
            ProgressPercent.Text = p.Percent >= 0 ? $"{p.Percent}%" : "";

            if (p.StageText != null) ProgressDetail.Text = p.StageText;
            if (p.FileText != null) ProgressFile.Text = p.FileText;
        });

        try
        {
            await Task.Run(() => _engine.Install(options, progress));
            _installed = true;

            ProgressDetail.Text = "Готово";
            ProgressFile.Text = "";
            DoneVersionText.Text = $"Установлено в {options.InstallFolder}";
            LaunchCheck.IsChecked = true;

            await Task.Delay(350);
            GoTo(Stage.Done);
        }
        catch (Exception ex)
        {
            _failedStage = ex.Message;
            ShowInstallError(ex.Message);
        }
        finally
        {
            _installRunning = false;
        }
    }

    private void ShowInstallError(string message)
    {
        ProgressDetail.Text = "Установка прервана";
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installRunning) return;
        ErrorPanel.Visibility = Visibility.Collapsed;
        OptionsInstallButton_Click(sender, e);
    }

    // ---------------------------------------------------------------- Завершение

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchCheck.IsChecked == true && _installed)
        {
            try
            {
                var exe = System.IO.Path.Combine(_engine.InstalledExeFolder, "EditorHelper.exe");
                if (!File.Exists(exe))
                {
                    MessageBox.Show(this,
                        $"EditorHelper.exe не найден по пути установки:\n{exe}\n\n" +
                        "Запустите приложение вручную из папки установки.",
                        "Editor Helper", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Не удалось запустить приложение:\n{ex.Message}",
                    "Editor Helper", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        Close();
    }

    // ---------------------------------------------------------------- Существующая установка

    private void DetectExistingInstall()
    {
        var existing = _engine.FindExistingInstall();
        if (existing == null) return;

        UpgradeNote.Visibility = Visibility.Visible;
        UpgradeNoteText.Text = existing.IsNewer
            ? $"Найдена версия {existing.Version}. Файлы будут обновлены до {_engine.ProductVersion}; " +
              "настройки и данные пользователя в %LOCALAPPDATA% не затрагиваются."
            : $"Найдена версия {existing.Version}. Установка продолжит поверх неё; " +
              "настройки и данные пользователя в %LOCALAPPDATA% не затрагиваются.";

        // Уже установлено в эту папку — подставим её
        PathBox.Text = existing.InstallFolder;
    }

    // ---------------------------------------------------------------- Окно

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Закругление углов DWM — как у основного окна приложения.</summary>
    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Украшение: отсутствие закругления не мешает установке
        }
    }
}
