using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using EditorHelper.Models;

namespace EditorHelper.Services;

/// <summary>
/// Скачивание файлов ресурсов. Скачанный файл — это ещё не установленный
/// ресурс: загрузка и установка намеренно разделены.
/// </summary>
public class DownloadService
{
    private readonly string _downloadFolder;
    private readonly HttpClient _httpClient;

    public DownloadService(string? downloadFolder = null)
    {
        _downloadFolder = string.IsNullOrWhiteSpace(downloadFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "EditorHelper")
            : downloadFolder;

        Directory.CreateDirectory(_downloadFolder);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// Скачивает файл ресурса. Если в описании указана контрольная сумма,
    /// она проверяется, и несовпавший файл удаляется.
    /// Сетевые сбои повторяются автоматически (до трёх попыток), отмена —
    /// через cancellationToken: оборванный файл при этом удаляется.
    /// </summary>
    /// <param name="overwrite">Перезаписывать ли существующий файл.</param>
    /// <param name="progress">Прогресс загрузки в процентах (0–100).</param>
    public async Task<DownloadResult> DownloadResourceAsync(
        Resource resource,
        bool overwrite = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resource.DownloadUrl))
        {
            throw new InvalidOperationException(
                $"\"{resource.Title}\" has no download source configured.");
        }

        var fileName = string.IsNullOrWhiteSpace(resource.FileName)
            ? $"{resource.Slug}.zip"
            : resource.FileName;

        var destinationPath = Path.Combine(_downloadFolder, fileName);

        if (File.Exists(destinationPath) && !overwrite)
        {
            return new DownloadResult
            {
                FilePath = destinationPath,
                WasSkipped = true,
                Message = "The file already exists in the download folder."
            };
        }

        var url = resource.DownloadUrl;
        var isLocal = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        if (isLocal)
        {
            await CopyLocalAsync(url, destinationPath, progress, cancellationToken);
        }
        else
        {
            await WithRetriesAsync(
                () => DownloadHttpAsync(url, destinationPath, resource.FileSize, progress, cancellationToken),
                destinationPath,
                cancellationToken);
        }

        // Проверка целостности — после загрузки и до того, как файл
        // попадёт в список скачанных
        var checksumStatus = await VerifyChecksumAsync(destinationPath, resource.Checksum);

        if (checksumStatus == ChecksumStatus.Mismatch)
        {
            TryDelete(destinationPath);
            throw new InvalidDataException(
                "The downloaded file does not match the expected checksum and was removed. " +
                "The source may have changed or the file is corrupted.");
        }

        return new DownloadResult
        {
            FilePath = destinationPath,
            Checksum = checksumStatus,
            Message = checksumStatus switch
            {
                ChecksumStatus.Verified => "Downloaded and verified.",
                ChecksumStatus.NotProvided => "Downloaded. No checksum was provided for verification.",
                _ => "Downloaded."
            }
        };
    }

    /// <summary>
    /// Повторяет сетевую загрузку при обрыве связи. Отмена и ошибки содержимого
    /// (несовпавшая сумма, неполный файл после последней попытки) не повторяются:
    /// они означают, что источник отдаёт не то, а не что сеть моргнула.
    /// </summary>
    private static async Task WithRetriesAsync(
        Func<Task> download, string destinationPath, CancellationToken cancellationToken)
    {
        const int attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await download();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(destinationPath);
                throw;
            }
            catch (Exception ex) when (attempt < attempts &&
                                       ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // Пауза растёт с номером попытки: 1с, 2с — даём сети прийти в себя
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private async Task CopyLocalAsync(
        string relativePath, string destination, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(AppPaths.Root, relativePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Source file not found:\n{sourcePath}\n\n" +
                "Make sure the Data folder is next to EditorHelper.exe.");
        }

        progress?.Report(new DownloadProgress { Percent = 0 });

        await using (var source = File.OpenRead(sourcePath))
        await using (var target = File.Create(destination))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        progress?.Report(new DownloadProgress { Percent = 100 });
    }

    private async Task DownloadHttpAsync(
        string url, string destination, long expectedSize, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        long received = 0;

        await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(destination))
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                if (totalBytes > 0)
                {
                    progress?.Report(new DownloadProgress
                    {
                        Percent = (int)(received * 100 / totalBytes),
                        ReceivedBytes = received,
                        TotalBytes = totalBytes
                    });
                }
            }
        }

        // Сервер оборвал передачу — файл неполный
        if (totalBytes > 0 && received != totalBytes)
        {
            TryDelete(destination);
            throw new IOException(
                $"The download stopped early: received {received} of {totalBytes} bytes.");
        }
    }

    /// <summary>
    /// Проверяет контрольную сумму. Формат: "sha256:hex" или просто hex.
    /// Если сумма не указана, файл считается принятым без проверки.
    /// </summary>
    private static async Task<ChecksumStatus> VerifyChecksumAsync(string filePath, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return ChecksumStatus.NotProvided;

        var algorithm = "sha256";
        var value = expected.Trim();

        var separator = value.IndexOf(':');
        if (separator > 0)
        {
            algorithm = value[..separator].ToLowerInvariant();
            value = value[(separator + 1)..];
        }

        value = value.Replace(" ", "").ToLowerInvariant();

        await using var stream = File.OpenRead(filePath);

        var actual = algorithm switch
        {
            "sha256" => Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant(),
            "sha1" => Convert.ToHexString(await SHA1.HashDataAsync(stream)).ToLowerInvariant(),
            "md5" => Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant(),
            _ => throw new NotSupportedException(
                $"Checksum algorithm \"{algorithm}\" is not supported. Use sha256, sha1 or md5.")
        };

        return actual == value ? ChecksumStatus.Verified : ChecksumStatus.Mismatch;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Файл занят — ничего не поделать, сообщение об ошибке важнее
        }
    }

    public string GetDownloadFolder() => Path.GetFullPath(_downloadFolder);
}

public enum ChecksumStatus
{
    NotProvided,
    Verified,
    Mismatch
}

public class DownloadResult
{
    public string FilePath { get; set; } = "";
    public ChecksumStatus Checksum { get; set; }
    public bool WasSkipped { get; set; }
    public string Message { get; set; } = "";
}

public class DownloadProgress
{
    public int Percent { get; set; }
    public long ReceivedBytes { get; set; }
    public long TotalBytes { get; set; }
}