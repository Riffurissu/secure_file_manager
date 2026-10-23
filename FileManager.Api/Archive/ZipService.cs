using System.IO.Compression;
using FileManager.Api.Auth;
using FileManager.Api.Data;
using FileManager.Api.Files;

namespace FileManager.Api.Archive;

public sealed record ArchiveCreated(string Path, int Entries, long Size);

public sealed record ArchiveExtracted(int Files, long Bytes);

/// <summary>
/// ZIP-архивы в домашней папке пользователя. Архив и его содержимое считаются недоверенными.
/// Лимиты берутся из <see cref="StorageLimits"/>. Распаковка атомарна: либо все записи извлечены и
/// журнал записан одной транзакцией, либо файловая система и журнал возвращаются к исходному состоянию.
/// </summary>
public sealed class ZipService(SafePathResolver resolver, FileService files)
{
    private const string ArchiveExtension = ".zip";

    private sealed record Item(string Path, bool IsDirectory, byte[] Data);

    private sealed record PlannedEntry(string Path, bool IsDirectory, ZipArchiveEntry Entry);

    /// <summary>
    /// Упаковывает файл или каталог (пустой путь — вся домашняя папка) в ZIP внутри домашней папки.
    /// Сам архив исключается из упаковки. Пустые каталоги сохраняются записями с завершающим "/".
    /// </summary>
    public ArchiveCreated Create(AuthUser user, string? sourcePath, string? archivePath)
    {
        RequireZipName(archivePath);
        var archive = resolver.Resolve(user.Username, archivePath);

        var items = new List<Item>();
        long total = 0;

        if (string.IsNullOrEmpty(sourcePath))
        {
            Collect(user, string.Empty, 0, items, ref total, archive.RelativePath);
        }
        else
        {
            var source = resolver.Resolve(user.Username, sourcePath);
            if (Directory.Exists(source.FullPath))
            {
                Collect(user, source.RelativePath, 0, items, ref total, archive.RelativePath);
            }
            else if (File.Exists(source.FullPath))
            {
                AddFile(user, source.RelativePath, items, ref total);
            }
            else
            {
                throw new FileServiceException(404, "Источник не найден.");
            }
        }

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    zip.CreateEntry(item.Path + "/");
                    continue;
                }

                var entry = zip.CreateEntry(item.Path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(item.Data, 0, item.Data.Length);
            }
        }

        // Архив записывается как обычный файл: лимит StorageLimits.MaxFileSize применяется в WriteFile.
        var written = files.WriteFile(user, archive.RelativePath, memory.ToArray());
        return new ArchiveCreated(written.Path, items.Count, written.Size);
    }

    /// <summary>
    /// Атомарно распаковывает архив в каталог внутри домашней папки. Все записи проверяются до начала записи.
    /// При любой ошибке на диске откатываются уже записанные файлы и созданные каталоги, журнал не меняется.
    /// </summary>
    public ArchiveExtracted Extract(AuthUser user, string? archivePath, string? destination)
    {
        RequireZipName(archivePath);
        var archive = resolver.Resolve(user.Username, archivePath);
        var destinationRelative = resolver.Resolve(user.Username, destination, allowRoot: true).RelativePath;
        var bytes = files.ReadFile(user, archive.RelativePath);

        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var plan = PlanExtraction(user, zip, destinationRelative);
            return ExecuteExtraction(user, plan);
        }
        catch (InvalidDataException)
        {
            throw new FileServiceException(400, "Некорректный ZIP-архив.");
        }
        catch (NotSupportedException)
        {
            throw new FileServiceException(400, "Неподдерживаемый ZIP-архив.");
        }
    }

    private void Collect(AuthUser user, string relativeDir, int depth, List<Item> items, ref long total, string excludeRelative)
    {
        if (depth > StorageLimits.MaxDirectoryDepth)
        {
            throw new FileServiceException(400, "Слишком глубокая вложенность каталогов.");
        }

        foreach (var entry in files.ListDirectory(user, relativeDir.Length == 0 ? null : relativeDir))
        {
            var relative = relativeDir.Length == 0 ? entry.Name : relativeDir + "/" + entry.Name;
            if (string.Equals(relative, excludeRelative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Type == "directory")
            {
                if (items.Count >= StorageLimits.MaxArchiveEntries)
                {
                    throw new FileServiceException(400, "Слишком много записей для архива.");
                }

                items.Add(new Item(relative, true, Array.Empty<byte>()));
                Collect(user, relative, depth + 1, items, ref total, excludeRelative);
            }
            else
            {
                AddFile(user, relative, items, ref total);
            }
        }
    }

    private void AddFile(AuthUser user, string relative, List<Item> items, ref long total)
    {
        if (items.Count >= StorageLimits.MaxArchiveEntries)
        {
            throw new FileServiceException(400, "Слишком много записей для архива.");
        }

        var data = files.ReadFile(user, relative);
        total += data.Length;
        if (total > StorageLimits.MaxArchiveUncompressedTotal)
        {
            throw new FileServiceException(413, "Суммарный размер файлов превышает лимит архива.");
        }

        items.Add(new Item(relative, false, data));
    }

    private List<PlannedEntry> PlanExtraction(AuthUser user, ZipArchive zip, string destinationRelative)
    {
        if (zip.Entries.Count > StorageLimits.MaxArchiveEntries)
        {
            throw new FileServiceException(400, "Слишком много записей в архиве.");
        }

        var plan = new List<PlannedEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;

        foreach (var entry in zip.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var name = NormalizeEntryName(entry.FullName);
            var target = destinationRelative.Length == 0 ? name : destinationRelative + "/" + name;

            // Zip Slip: resolver отклоняет "..", абсолютные пути, диски и reparse points.
            var resolved = resolver.Resolve(user.Username, target);

            if (!seen.Add(resolved.RelativePath))
            {
                throw new FileServiceException(400, "Повторяющиеся пути в архиве.");
            }

            if (!isDirectory)
            {
                // Предварительная проверка по заголовку; фактический размер проверяется потоком при извлечении.
                declaredTotal += entry.Length;
                if (declaredTotal > StorageLimits.MaxArchiveUncompressedTotal)
                {
                    throw new FileServiceException(413, "Суммарный размер распаковки превышает лимит.");
                }

                CheckRatio(entry.Length, entry.CompressedLength);
            }

            plan.Add(new PlannedEntry(resolved.RelativePath, isDirectory, entry));
        }

        return plan;
    }

    /// <summary>
    /// Извлекает записи и откатывает изменения при любой ошибке. Журнал пишется только при полном успехе.
    /// </summary>
    private ArchiveExtracted ExecuteExtraction(AuthUser user, List<PlannedEntry> plan)
    {
        // Порядок отката: в обратном порядке создания. Снимки файлов и список созданных каталогов.
        var undoFiles = new List<FileSnapshot>();
        var createdDirs = new List<string>();
        var changes = new List<BatchChange>();

        try
        {
            long total = 0;

            foreach (var item in plan)
            {
                if (item.IsDirectory)
                {
                    EnsureChain(user, item.Path, createdDirs, changes);
                    continue;
                }

                EnsureChain(user, ParentOf(item.Path), createdDirs, changes);

                // Фактический размер считается потоком, а не по заголовку записи.
                var remaining = StorageLimits.MaxArchiveUncompressedTotal - total;
                var data = ReadEntryLimited(item.Entry, Math.Min(StorageLimits.MaxFileSize, remaining));
                total += data.Length;
                CheckRatio(data.Length, item.Entry.CompressedLength);

                var snapshot = files.WriteFileRaw(user, item.Path, data);
                undoFiles.Add(snapshot);

                changes.Add(snapshot.ExistingFileId is { } existingId
                    ? new BatchChange(BatchChangeKind.UpdateFile, Name: string.Empty, Location: item.Path,
                        Size: data.Length, FileId: existingId, Details: $"{item.Path} size={data.Length}")
                    : new BatchChange(BatchChangeKind.CreateFile, Name: Path.GetFileName(item.Path), Location: item.Path,
                        Size: data.Length, FileId: null, Details: $"{item.Path} size={data.Length}"));
            }

            files.ApplyBatch(user, changes);
            return new ArchiveExtracted(undoFiles.Count, total);
        }
        catch
        {
            Rollback(user, undoFiles, createdDirs);
            throw;
        }
    }

    /// <summary>
    /// Откатывает файлы в обратном порядке и удаляет созданные каталоги, если они пусты.
    /// Ошибка одного шага не прерывает остальные: откат проходит до конца, исключения глотаются.
    /// </summary>
    private void Rollback(AuthUser user, List<FileSnapshot> undoFiles, List<string> createdDirs)
    {
        for (var i = undoFiles.Count - 1; i >= 0; i--)
        {
            try
            {
                files.RestoreFile(undoFiles[i]);
            }
            catch (Exception)
            {
                // Best-effort: ошибка одного файла не должна мешать откату остальных.
            }
        }

        for (var i = createdDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                files.RemoveDirectoryIfEmpty(createdDirs[i]);
            }
            catch (Exception)
            {
                // Каталог не пуст или занят: оставляем как есть.
            }
        }
    }

    private void EnsureChain(AuthUser user, string relative, List<string> createdDirs, List<BatchChange> changes)
    {
        if (relative.Length == 0)
        {
            return;
        }

        var current = string.Empty;
        foreach (var part in relative.Split('/'))
        {
            current = current.Length == 0 ? part : current + "/" + part;
            if (files.CreateDirectoryRaw(user, current))
            {
                createdDirs.Add(files.ResolveFullPath(user, current));
                changes.Add(new BatchChange(BatchChangeKind.CreateDirectory, Name: part, Location: current,
                    Size: null, FileId: null, Details: $"dir:{current}"));
            }
        }
    }

    private static byte[] ReadEntryLimited(ZipArchiveEntry entry, long limit)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new FileServiceException(413, "Распакованные данные превышают допустимый объём.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void CheckRatio(long uncompressed, long compressed)
    {
        if (uncompressed <= StorageLimits.CompressionRatioThreshold)
        {
            return;
        }

        if (compressed <= 0 || uncompressed / compressed > StorageLimits.MaxCompressionRatio)
        {
            throw new FileServiceException(400, "Подозрительно высокий коэффициент сжатия.");
        }
    }

    private static string NormalizeEntryName(string fullName)
    {
        var name = fullName.Replace('\\', '/').TrimEnd('/');
        if (name.Length == 0 || name.StartsWith('/'))
        {
            throw new FileServiceException(400, "Недопустимое имя записи в архиве.");
        }

        return name;
    }

    private static string ParentOf(string relative)
    {
        var index = relative.LastIndexOf('/');
        return index < 0 ? string.Empty : relative[..index];
    }

    private static void RequireZipName(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileServiceException(400, "Укажите путь к архиву с расширением .zip.");
        }
    }
}
