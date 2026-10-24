using System.IO;
using System.IO.Compression;

namespace EditorHelper.Setup.Logic;

/// <summary>
/// Формат упаковки payload: Deflate-сжатый поток записей
/// "путь;размер;байты" подряд. Deflate вместо LZMA SDK: системный
/// System.IO.Compression даёт достаточное сжатие (~150 МБ -> ~60 МБ)
/// без внешних зависимостей и с воспроизводимой упаковкой в сборке.
///
/// Писатель — генератор в сборочном скрипте (Release\pack-installer.ps1),
/// читатель — установщик. Формат внутренний, версий не предполагает:
/// установщик и паковщик собираются из одного репозитория.
/// </summary>
internal static class LzmaPayload
{
    private const string Magic = "EHP1";

    /// <summary>Распаковка payload-архива в папку. Возвращает число файлов.</summary>
    public static int Extract(string archivePath, string destination)
    {
        using var archive = File.OpenRead(archivePath);
        Span<byte> magic = stackalloc byte[4];
        archive.ReadExactly(magic);
        if (!magic.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Magic)))
            throw new InvalidDataException("Файл payload повреждён или собран другой версией упаковщика.");

        using var deflate = new DeflateStream(archive, CompressionMode.Decompress);

        int files = 0;
        while (true)
        {
            // Путь: длина (varint) + байты UTF-8
            var pathLength = ReadVarint(deflate);
            if (pathLength == 0) break; // конец архива

            var pathBytes = new byte[pathLength];
            ReadExactly(deflate, pathBytes);
            var relative = System.Text.Encoding.UTF8.GetString(pathBytes);

            // Размер файла
            var size = ReadVarint(deflate);

            var target = Path.Combine(destination, relative);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var file = File.Create(target);
            CopyExactly(deflate, file, size);
            files++;
        }

        return files;
    }

    /// <summary>Упаковка папки в payload-архив (вызывается сборочным скриптом).</summary>
    public static void Pack(string sourceFolder, string archivePath)
    {
        using var archive = File.Create(archivePath);
        archive.Write(System.Text.Encoding.ASCII.GetBytes(Magic));

        using var deflate = new DeflateStream(archive, CompressionLevel.Optimal, leaveOpen: true);

        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(sourceFolder, file).Replace('\\', '/');
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relative);
            var size = new FileInfo(file).Length;

            WriteVarint(deflate, pathBytes.Length);
            deflate.Write(pathBytes);
            WriteVarint(deflate, size);

            using var input = File.OpenRead(file);
            input.CopyTo(deflate);
        }

        // Конец архива: путь нулевой длины
        WriteVarint(deflate, 0);
    }

    // ------------------------------------------------------------ varint

    private static long ReadVarint(Stream stream)
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException("Payload обрезан.");
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Повреждён varint в payload.");
        }
    }

    private static void WriteVarint(Stream stream, long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80)
        {
            stream.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        stream.WriteByte((byte)v);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0) throw new EndOfStreamException("Payload обрезан.");
            read += chunk;
        }
    }

    private static void CopyExactly(Stream source, Stream target, long bytes)
    {
        var buffer = new byte[81920];
        long remaining = bytes;
        while (remaining > 0)
        {
            var chunk = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (chunk == 0) throw new EndOfStreamException("Payload обрезан.");
            target.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }
}
