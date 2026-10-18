using System.Globalization;

namespace EditorHelper.Ui;

/// <summary>
/// Форматирование дат и размеров. Культура задаётся явно, иначе формат
/// зависит от системной локали: при английском интерфейсе на русской
/// системе даты выводились бы как "марта 15, 2024".
/// </summary>
public static class Format
{
    /// <summary>Культура интерфейса. Позже будет выбираться из настроек.</summary>
    public static CultureInfo Culture { get; set; } = new("en-US");

    public static string Date(DateTime value) =>
        value == default ? "—" : value.ToString("MMMM d, yyyy", Culture);

    public static string FileSize(long bytes)
    {
        if (bytes <= 0) return "—";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.#";
        return $"{size.ToString(format, Culture)} {units[unit]}";
    }
}