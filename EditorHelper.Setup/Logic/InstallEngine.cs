using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace EditorHelper.Setup.Logic;

/// <summary>Параметры установки, выбранные на экране настроек.</summary>
public sealed class InstallOptions
{
    public string InstallFolder { get; init; } = "";
    public bool CreateDesktopShortcut { get; init; }
    public bool CreateStartMenuShortcut { get; init; }
}

/// <summary>Один шаг реального прогресса: байты и текущее действие.</summary>
public sealed class InstallProgress
{
    /// <summary>0–100; -1 = неопределённый этап (подготовка/ярлыки).</summary>
    public int Percent { get; init; } = -1;
    public string? StageText { get; init; }
    public string? FileText { get; init; }
}

/// <summary>Обнаруженная предыдущая установка.</summary>
public sealed record ExistingInstall(string InstallFolder, string Version, bool IsNewer);

/// <summary>
/// Механизм установки: распаковка payload, копирование файлов, ярлыки,
/// запись в «Программы и компоненты», удаление.
///
/// Payload (сборка self-contained приложения) упакован внутрь установщика
/// как LZMA-архив payload.lzma и распаковывается во временную папку.
///
/// Права администратора не обязательны: установка в пользовательскую папку
/// и запись в HKCU\...\Uninstall. В Program Files UAC поднимет систему сама.
/// </summary>
public sealed class InstallEngine
{
    private const string AppName = "Editor Helper";
    private const string Publisher = "Editor Helper";
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EditorHelper";

    /// <summary>Версия устанавливаемого продукта — из версии сборки установщика.</summary>
    public string ProductVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Папка по умолчанию: Program Files, если доступна на запись, иначе LocalAppData.</summary>
    public string DefaultInstallFolder
    {
        get
        {
            var programFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Editor Helper");
            if (IsWritable(programFiles)) return programFiles;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Editor Helper");
        }
    }

    /// <summary>Папка, куда установлено приложение сейчас (после Install).</summary>
    public string InstalledExeFolder { get; private set; } = "";

    /// <summary>Приблизительный объём необходимых файлов, МБ.</summary>
    public int RequiredMegabytes { get; private set; } = 145;

    /// <summary>
    /// Полная установка. Выполняется в фоновом потоке; прогресс — по факту
    /// скопированных байтов и файлам. Любая ошибка — исключение наружу
    /// (интерфейс покажет её и предложит повторить).
    /// </summary>
    public void Install(InstallOptions options, IProgress<InstallProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(options.InstallFolder))
            throw new InvalidOperationException("Не указана папка установки.");

        var target = Path.GetFullPath(options.InstallFolder);

        // --- 1. Проверки целевой папки
        Report(progress, -1, "Проверка папки установки…", null);
        if (!Directory.Exists(target))
        {
            try { Directory.CreateDirectory(target); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось создать папку:\n{target}\n\n{ex.Message}", ex);
            }
        }

        if (!IsWritable(target))
            throw new InvalidOperationException(
                $"Нет прав на запись в папку:\n{target}\n\n" +
                "Выберите другую папку или запустите установщик от имени администратора.");

        // --- 2. Приложение не должно работать с заменяемыми файлами
        Report(progress, -1, "Завершение запущенного приложения…", null);
        TryCloseRunningApp(target);

        // --- 3. Распаковка payload во временную папку
        Report(progress, -1, "Распаковка файлов установки…", null);
        var tempPayload = Path.Combine(Path.GetTempPath(), "EditorHelper-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractPayload(tempPayload);

            // --- 4. Копирование с честным прогрессом по байтам
            CopyPayload(tempPayload, target, progress);

            // --- 5. Деинсталлятор: копия установщика в подпапку uninstall\.
            // Host exe ищет EditorHelperSetup.dll рядом с собой, поэтому одного
            // exe мало: копируем весь набор (exe + dll + рантайм), кроме payload
            Report(progress, -1, "Установка деинсталлятора…", null);
            var uninstallFolder = Path.Combine(target, "uninstall");
            Directory.CreateDirectory(uninstallFolder);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("payload.lzma", StringComparison.OrdinalIgnoreCase)) continue;
                CopyFileWithRetry(file, Path.Combine(uninstallFolder, name));
            }

            InstalledExeFolder = target;

            // --- 6. Ярлыки
            if (options.CreateStartMenuShortcut || options.CreateDesktopShortcut)
                Report(progress, -1, "Создание ярлыков…", null);
            CreateShortcuts(target, options);

            // --- 7. Реестр: «Программы и компоненты»
            Report(progress, -1, "Регистрация в системе…", null);
            RegisterUninstall(target, options);

            Report(progress, 100, "Готово", null);
        }
        finally
        {
            // Временные файлы не должны оставаться ни при успехе, ни при ошибке
            TryDeleteDirectory(tempPayload);
        }
    }

    // ------------------------------------------------------------ Payload

    /// <summary>
    /// Распаковка payload.lzma, встроенного в установщик сборочным скриптом.
    /// Пока scripting-вариант LZMA не подключён, payload прилетает как
    /// распакованная папка рядом с установщиком (Release\payload) —
    /// это запасной источник для локальной разработки.
    /// </summary>
    private static void ExtractPayload(string destination)
    {
        var embedded = Path.Combine(AppContext.BaseDirectory, "payload.lzma");
        if (File.Exists(embedded))
        {
            // Формат: LZMA-сжатый TAR-подобный поток имён и байтов.
            // Читается своим лёгким ридером — внешние зависимости не нужны.
            LzmaPayload.Extract(embedded, destination);
            return;
        }

        // Запасной путь: папка payload рядом (запуск из Release без упаковки)
        var folder = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(folder))
        {
            CopyDirectory(folder, destination);
            return;
        }

        throw new InvalidOperationException(
            "Не найдены файлы приложения (payload). Установщик собран без данных " +
            "приложения — пересоберите его командой Release\\pack-installer.cmd.");
    }

    private static void CopyPayload(string source, string target, IProgress<InstallProgress> progress)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var totalBytes = files.Sum(f => new FileInfo(f).Length);

        long copied = 0;
        var totalMb = totalBytes / 1024.0 / 1024;

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Report(progress,
                (int)(copied * 100 / Math.Max(1, totalBytes)),
                $"Копирование файлов… {copied / 1024.0 / 1024:0.0} из {totalMb:0.0} МБ",
                relative);

            CopyFileWithRetry(file, destination);
            copied += new FileInfo(file).Length;
        }
    }

    /// <summary>
    /// Копирование с одной повторной попыткой: антивирус или файловый кэш
    /// Windows иногда держат файл долю секунды после закрытия приложения.
    /// </summary>
    private static void CopyFileWithRetry(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // Сначала во временное имя, затем замена: частично
                // скопированный файл никогда не станет рабочим exe
                var temp = destination + ".new";
                File.Copy(source, temp, overwrite: true);
                if (File.Exists(destination))
                    File.Replace(temp, destination, null);
                else
                    File.Move(temp, destination);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось скопировать файл:\n{Path.GetFileName(destination)}\n\n{ex.Message}", ex);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    // ------------------------------------------------------------ Ярлыки

    private void CreateShortcuts(string target, InstallOptions options)
    {
        var exe = Path.Combine(target, "EditorHelper.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException("EditorHelper.exe не найден в payload — сборка установщика повреждена.");

        if (options.CreateDesktopShortcut)
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Editor Helper.lnk"),
                exe, target);

        if (options.CreateStartMenuShortcut)
        {
            var menuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Editor Helper");
            Directory.CreateDirectory(menuFolder);
            CreateShortcut(Path.Combine(menuFolder, "Editor Helper.lnk"), exe, target);
            // Удаление — тоже пункт меню, как у классических установщиков
            CreateShortcut(
                Path.Combine(menuFolder, "Удалить Editor Helper.lnk"),
                Path.Combine(target, "uninstall", "EditorHelperSetup.exe"), target, arguments: "--uninstall");
        }
    }

    /// <summary>
    /// Ярлык через COM-обёртку IShellLink (тип Interop.Shell262 не подключён —
    /// используем позднее связывание).
    /// </summary>
    private static void CreateShortcut(string path, string targetExe, string workingDir, string arguments = "")
    {
        try
        {
            var shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"))!;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            var shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Arguments = arguments;
                shortcut.IconLocation = $"{targetExe},0";
                shortcut.Description = "Editor Helper — creative workspace";
                shortcut.Save();
            }
            finally
            {
                shortcut.Close();
            }
        }
        catch
        {
            // Ярлык — не критический сбой: приложение остаётся установленным,
            // пользователь сможет запустить его из папки. Тихо пропускаем.
        }
    }

    // ------------------------------------------------------------ Реестр

    /// <summary>
    /// Запись «Программы и компоненты». HKCU: работает без прав администратора;
    /// установка «для всех» (в Program Files) требует UAC, и запись всё равно
    /// достаточна — Windows показывает её текущему пользователю, а на общий
    /// список влияет запись HKLM, которую оставляем повышенному режиму.
    /// </summary>
    private void RegisterUninstall(string target, InstallOptions options)
    {
        var sizeKb = 0L;
        try
        {
            sizeKb = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length) / 1024;
        }
        catch { /* размер некритичен */ }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey)
                        ?? throw new InvalidOperationException("Не удалось создать запись в реестре.");

        key.SetValue("DisplayName", $"{AppName} {ProductVersion}");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("DisplayIcon", Path.Combine(target, "EditorHelper.exe") + ",0");
        key.SetValue("InstallLocation", target);
        key.SetValue("Publisher", Publisher);
        key.SetValue("UninstallString", $"\"{Path.Combine(target, "uninstall", "EditorHelperSetup.exe")}\" --uninstall");
        key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Поиск существующей установки по записи в реестре.</summary>
    public ExistingInstall? FindExistingInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
            if (key?.GetValue("InstallLocation") is not string folder || !Directory.Exists(folder))
                return null;

            var version = key.GetValue("DisplayVersion") as string ?? "";
            return new ExistingInstall(folder, version, !string.IsNullOrEmpty(version) && version != ProductVersion);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ Утилиты

    private static void Report(IProgress<InstallProgress> progress, int percent, string? stage, string? file) =>
        progress.Report(new InstallProgress { Percent = percent, StageText = stage, FileText = file });

    private static bool IsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Мягкое завершение запущенного приложения перед заменой его файлов:
    /// сначала WM_CLOSE (приложение сохраняет состояние), затем, если окно
    /// осталось, — принудительно. Убивать сразу нельзя: потерялись бы
    /// незаписанные данные.
    /// </summary>
    private static void TryCloseRunningApp(string targetFolder)
    {
        var exe = Path.Combine(targetFolder, "EditorHelper.exe");
        if (!File.Exists(exe)) return;

        foreach (var process in System.Diagnostics.Process.GetProcessesByName("EditorHelper"))
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch
            {
                continue;
            }

            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(4000))
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Процесс мог завершиться между вызовами — не ошибка
            }
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Временную папку чистит Windows; установка это не ломает
        }
    }
}
