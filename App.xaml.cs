using System.Windows;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper;

/// <summary>
/// Точка входа приложения. Здесь применяются настройки, влияющие
/// на всё приложение сразу: язык интерфейса и источник каталога.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Состояние из переносимой сборки (рядом с exe) переносится в данные
        // пользователя до первого чтения настроек — иначе оно «пропадёт».
        AppPaths.MigrateLegacyState();

        var settings = AppSettings.Load();

        // Культура задаётся из настроек, а не берётся из системы:
        // интерфейс на английском не должен показывать русские названия месяцев
        Format.Culture = settings.Language switch
        {
            "ru" => new System.Globalization.CultureInfo("ru-RU"),
            "pl" => new System.Globalization.CultureInfo("pl-PL"),
            _ => new System.Globalization.CultureInfo("en-US")
        };

        CatalogService.Instance.ApplySettings(settings);
    }
}