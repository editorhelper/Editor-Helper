using System.Windows;

namespace EditorHelper.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Два режима из одного бинарника:
        //   без аргументов    — мастер установки
        //   --uninstall       — деинсталлятор (тот же дизайн, отдельное окно)
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            var uninstaller = new UninstallWindow();
            uninstaller.Show();
        }
        else
        {
            var setup = new SetupWindow();
            setup.Show();
        }
    }
}
