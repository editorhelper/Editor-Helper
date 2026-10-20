using System.Diagnostics;
using System.IO;
using EditorHelper.Models;

namespace EditorHelper.Services;

/// <summary>
/// Установка ресурсов. Приложение никогда не запускает скачанные
/// EXE, BAT или PowerShell скрипты — для таких ресурсов показывается
/// инструкция, а не кнопка автоматической установки.
/// </summary>
public class InstallationService
{
    public async Task<InstallationResult> InstallResourceAsync(Resource resource, string downloadedFilePath)
    {
        try
        {
            return resource.InstallationType switch
            {
                InstallationType.CopyToFolder => await InstallByCopyAsync(resource, downloadedFilePath),
                InstallationType.Script => await InstallScriptAsync(resource, downloadedFilePath),

                // Установщик запускается пользователем вручную — это осознанное ограничение
                InstallationType.Installer or InstallationType.Extension => new InstallationResult
                {
                    Success = true,
                    RequiresManualSteps = true,
                    Message = "This resource ships with its own installer, which Editor Helper will not run for you.",
                    Instructions = BuildInstallerInstructions(resource, downloadedFilePath)
                },

                _ => new InstallationResult
                {
                    Success = true,
                    RequiresManualSteps = true,
                    Message = "This resource is installed manually.",
                    Instructions = GetManualInstructions(resource)
                }
            };
        }
        catch (Exception ex)
        {
            return new InstallationResult
            {
                Success = false,
                Message = $"Installation failed: {ex.Message}"
            };
        }
    }

    /// <summary>Копирование файла в папку ресурсов приложения Adobe.</summary>
    public Task<InstallationResult> InstallToFolderAsync(
        Resource resource, string sourceFile, string targetFolder)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
                {
                    return new InstallationResult
                    {
                        Success = false,
                        Message = $"Folder does not exist:\n{targetFolder}"
                    };
                }

                Directory.CreateDirectory(targetFolder);

                var fileName = Path.GetFileName(sourceFile);
                var targetPath = Path.Combine(targetFolder, fileName);

                if (File.Exists(targetPath))
                {
                    return new InstallationResult
                    {
                        Success = false,
                        Message = $"A file named \"{fileName}\" already exists in the target folder.\n\n" +
                                  "Remove or rename it first, so an existing file is never overwritten silently."
                    };
                }

                File.Copy(sourceFile, targetPath);

                return new InstallationResult
                {
                    Success = true,
                    InstalledPath = targetPath,
                    Message = $"Installed:\n{targetPath}\n\nRestart {resource.Application} to apply the changes."
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new InstallationResult
                {
                    Success = false,
                    Message = "Access denied. Try running Editor Helper as administrator, " +
                              "or install the resource manually."
                };
            }
            catch (Exception ex)
            {
                return new InstallationResult
                {
                    Success = false,
                    Message = $"Could not copy the file: {ex.Message}"
                };
            }
        });
    }

    private async Task<InstallationResult> InstallByCopyAsync(Resource resource, string sourceFile)
    {
        var targetFolder = GetSuggestedFolder(resource.Application);

        if (string.IsNullOrEmpty(targetFolder))
        {
            return new InstallationResult
            {
                Success = true,
                RequiresManualSteps = true,
                Message = "The installation folder could not be determined automatically.",
                Instructions = GetManualInstructions(resource)
            };
        }

        return await InstallToFolderAsync(resource, sourceFile, targetFolder);
    }

    private Task<InstallationResult> InstallScriptAsync(Resource resource, string sourceFile)
    {
        var scriptsFolder = GetScriptsFolder(resource.Application);

        if (string.IsNullOrEmpty(scriptsFolder))
        {
            return Task.FromResult(new InstallationResult
            {
                Success = true,
                RequiresManualSteps = true,
                Message = "The scripts folder could not be located.",
                Instructions = GetManualInstructions(resource)
            });
        }

        return InstallToFolderAsync(resource, sourceFile, scriptsFolder);
    }

    /// <summary>Папка, которую стоит показать пользователю для ручной установки.</summary>
    public string? GetSuggestedFolder(string application) => application switch
    {
        "After Effects" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Adobe", "Adobe After Effects", "Support Files", "Plugins"),

        "Premiere Pro" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Adobe", "Adobe Premiere Pro", "Plugins"),

        "Photoshop" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Adobe", "Adobe Photoshop", "Plug-ins"),

        _ => null
    };

    private string? GetScriptsFolder(string application) => application switch
    {
        "After Effects" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Adobe", "Adobe After Effects", "Support Files", "Scripts", "ScriptUI Panels"),

        "Premiere Pro" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Adobe", "Adobe Premiere Pro", "Scripts"),

        _ => null
    };

    public string GetManualInstructions(Resource resource) => resource.Application switch
    {
        "After Effects" => resource.Type == ResourceType.Script
            ? "After Effects script\n\n" +
              "1. Close After Effects.\n" +
              "2. Copy the .jsx file to:\n" +
              "   C:\\Program Files\\Adobe\\Adobe After Effects\\Support Files\\Scripts\\ScriptUI Panels\n" +
              "3. Start After Effects.\n" +
              "4. Open it from the Window menu.\n\n" +
              "Tip: if the folder is protected, copy the script to\n" +
              "   Documents\\Adobe\\After Effects\\[version]\\Scripts\\ScriptUI Panels instead."
            : "After Effects plugin\n\n" +
              "1. Close After Effects.\n" +
              "2. Extract the archive to a shared plug-ins folder, for example:\n" +
              "   C:\\Program Files\\Adobe\\Common\\Plug-ins\\[version]\\MediaCore\n" +
              "3. Start After Effects.\n" +
              "4. The effect appears in the Effects menu.\n\n" +
              "Read the archive's own readme first — folder layout differs between plugins.",

        "Premiere Pro" => resource.Type == ResourceType.Preset
            ? "Premiere Pro preset\n\n" +
              "1. Close Premiere Pro.\n" +
              "2. Copy the preset files to:\n" +
              "   Documents\\Adobe\\Premiere Pro\\[version]\\Presets\n" +
              "3. Start Premiere Pro.\n" +
              "4. Presets appear in the Effects panel."
            : "Premiere Pro plugin\n\n" +
              "1. Close Premiere Pro.\n" +
              "2. Extract the archive to:\n" +
              "   C:\\Program Files\\Adobe\\Common\\Plug-ins\\[version]\\MediaCore\n" +
              "3. Start Premiere Pro.",

        "Photoshop" => "Photoshop resource\n\n" +
                       "1. Close Photoshop.\n" +
                       "2. Extract the archive to the matching folder:\n" +
                       "   Actions  -> Presets\\Actions\n" +
                       "   Brushes  -> Presets\\Brushes\n" +
                       "   Filters  -> Plug-ins\n" +
                       "3. Start Photoshop and enable the resource from the relevant panel.",

        _ => "Follow the instructions included with the resource."
    };

    private static string BuildInstallerInstructions(Resource resource, string downloadedFilePath)
    {
        var folder = Path.GetDirectoryName(downloadedFilePath);

        return "This resource is installed with its own installer.\n\n" +
               "Editor Helper does not run downloaded executables automatically — " +
               "you start the installer yourself so you stay in control.\n\n" +
               "1. Check the file against the source you downloaded it from.\n" +
               $"2. The file is here:\n   {downloadedFilePath}\n" +
               "3. Run it manually and follow its instructions.\n" +
               $"4. Restart {resource.Application} afterwards.\n\n" +
               (folder != null ? $"You can open the folder from the button below.\n" : "");
    }

    /// <summary>Открывает папку с загруженным файлом в проводнике.</summary>
    public void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Проводник недоступен — не критично
        }
    }
}

public class InstallationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Instructions { get; set; }
    public string? InstalledPath { get; set; }
    public bool RequiresManualSteps { get; set; }
}