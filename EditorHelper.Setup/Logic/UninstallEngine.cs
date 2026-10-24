using System.IO;
using Microsoft.Win32;

namespace EditorHelper.Setup.Logic;

/// <summary>Шаг удаления.</summary>
public sealed class UninstallProgress
{
    public int Percent { get; init; } = -1;
    public string? StageText { get; init; }
}

/// <summary>
/// Логика удаления: только файлы и ярлыки, созданные установщиком.
/// Папка данных пользователя не трогается.
/// </summary>
public sealed class UninstallEngine
{
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EditorHelper";

    /// <summary>Папка установленного приложения или null, если не найдена.</summary>
    public string? FindInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
            if (key?.GetValue("InstallLocation") is string folder &&
                Directory.Exists(folder) &&
                File.Exists(Path.Combine(folder, "EditorHelper.exe")))
                return folder;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Uninstall(IProgress<UninstallProgress> progress)
    {
        var folder = FindInstall();

        // 1. Приложение не должно держать удаляемые файлы
        Report(progress, -1, "Завершение приложения…");
        if (folder != null) KillAppProcesses(folder);

        // 2. Файлы приложения: только содержимое папки установки.
        //    Посторонние файлы (если пользователь положил свои) не трогаем:
        //    перечисляем только те, что узнаём по расширениям сборки.
        if (folder != null)
        {
            Report(progress, -1, "Удаление файлов…");
            DeleteAppFiles(folder, progress);
        }

        // 3. Ярлыки
        Report(progress, -1, "Удаление ярлыков…");
        DeleteShortcuts();

        // 4. Запись в реестре
        Report(progress, 100, "Удаление записи из списка программ…");
        try { Registry.CurrentUser.DeleteSubKey(UninstallRegistryKey, throwOnMissingSubKey: false); }
        catch { /* уже нет — нормально */ }

        // 5. Папка деинсталлятора: наши собственные файлы удаляет cmd-скрипт
        //    после завершения процесса — удалить запущенный exe сам себе не может
        if (folder != null)
        {
            var uninstallFolder = Path.Combine(folder, "uninstall");
            if (Directory.Exists(uninstallFolder))
                ScheduleSelfDelete(uninstallFolder, folder);
        }

        // 6. Папка приложения — если опустела
        if (folder != null) TryDeleteEmptyFolder(folder);
    }

    /// <summary>
    /// Отложенное самоудаление: cmd-процесс ждёт выхода деинсталлятора,
    /// затем удаляет папку uninstall и пустой корень. Запущенный из
    /// uninstall-папки cmd уже не зависит от её файлов.
    /// </summary>
    private static void ScheduleSelfDelete(string uninstallFolder, string rootFolder)
    {
        var script = Path.Combine(Path.GetTempPath(), "eh-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            $"rd /s /q \"{uninstallFolder}\"\r\n" +
            $"dir /b \"{rootFolder}\" 2>nul | findstr /r \".*\" >nul || rd /q \"{rootFolder}\"\r\n" +
            "del \"%~f0\"\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Удаление файлов сборки. Payload self-contained имеет известный набор
    /// расширений; ярлыков и документов пользователя в папке установки
    /// не бывает, но перечисление по расширениям — защита от удаления
    /// случайно положенных пользователем файлов.
    /// </summary>
    private static void DeleteAppFiles(string folder, IProgress<UninstallProgress> progress)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        int removed = 0;

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".exe" or ".dll" or ".json" or ".deps" or ".config" or ".txt"))
                continue;

            try
            {
                File.Delete(file);
                removed++;
                Report(progress, (int)(removed * 100 / Math.Max(1, files.Count)), "Удаление файлов…");
            }
            catch
            {
                // Занятый файл пропускается: удаление не прерывается,
                // итоговая папка останется в списке до перезагрузки
            }
        }
    }

    private static void DeleteShortcuts()
    {
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Editor Helper.lnk"));

        var menuFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Editor Helper");
        TryDelete(Path.Combine(menuFolder, "Editor Helper.lnk"));
        TryDelete(Path.Combine(menuFolder, "Удалить Editor Helper.lnk"));
        TryDeleteDirectory(menuFolder);
    }

    private static void KillAppProcesses(string folder)
    {
        var exe = Path.Combine(folder, "EditorHelper.exe");
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("EditorHelper"))
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    continue;
                process.CloseMainWindow();
                if (!process.WaitForExit(4000))
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            // Удаляем только пустые подкаталоги и пустой корень: чужие файлы
            if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any()) return;
            Directory.Delete(folder, recursive: true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { }
    }

    private static void Report(IProgress<UninstallProgress> progress, int percent, string? stage) =>
        progress.Report(new UninstallProgress { Percent = percent, StageText = stage });
}
