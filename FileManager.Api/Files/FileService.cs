using System.Collections.Concurrent;
using FileManager.Api.Auth;
using FileManager.Api.Data;

namespace FileManager.Api.Files;

public sealed record FileEntry(string Name, string Type, long? Size, DateTimeOffset ModifiedAt);

public sealed record WriteResult(string Path, long Size, bool Created);

/// <summary>Состояние файла до записи: нужно для отката пакетной операции.</summary>
public sealed record FileSnapshot(
    string FullPath,
    string RelativePath,
    bool Existed,
    byte[]? PreviousContent,
    long? ExistingFileId,
    long NewSize);

/// <summary>
/// Файловые операции в домашней папке пользователя. Каждая операция над конкретным путём
/// выполняется под блокировкой по полному пути, чтобы параллельные запросы не конфликтовали.
/// Блокировки действуют внутри одного процесса; для нескольких экземпляров нужна внешняя синхронизация.
/// </summary>
public sealed class FileService(SafePathResolver resolver, FileRepository files)
{
    /// <summary>Максимальный размер файла (см. <see cref="StorageLimits.MaxFileSize"/>).</summary>
    public const long MaxFileSize = StorageLimits.MaxFileSize;

    private const string TempPrefix = ".fm-tmp-";

    // Атомарная замена делается несколькими попытками: на Windows MoveFile с перезаписью
    // может кратковременно получить Access denied, пока антивирус или индексатор удерживает файл.
    private const int MoveRetryAttempts = 4;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);

    // Файловая система Windows не различает регистр имён: поиск в БД и блокировки ведём без учёта регистра.
    // В Linux (контейнер) регистр значим, поэтому сравнение точное.
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows();

    private readonly ConcurrentDictionary<string, object> _locks = new(
        IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyList<FileEntry> ListDirectory(AuthUser user, string? path, bool withDirectorySize = false)
    {
        var resolved = resolver.Resolve(user.Username, path, allowRoot: true);
        if (!Directory.Exists(resolved.FullPath))
        {
            throw new FileServiceException(404, "Каталог не найден.");
        }

        var entries = new List<FileEntry>();
        foreach (var info in new DirectoryInfo(resolved.FullPath).EnumerateFileSystemInfos())
        {
            if (info.Name.StartsWith(TempPrefix, StringComparison.Ordinal) || SafePathResolver.IsLinkOrReparsePoint(info.FullName))
            {
                continue;
            }

            entries.Add(info is FileInfo file
                ? new FileEntry(file.Name, "file", file.Length, new DateTimeOffset(file.LastWriteTimeUtc))
                : new FileEntry(
                    info.Name,
                    "directory",
                    withDirectorySize ? SumDirectory(info.FullName, depth: 0) : null,
                    new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Суммарный размер файлов в каталоге и его подкаталогах. Симлинки, reparse points и временные файлы
    /// не учитываются, так же как в <see cref="ListDirectory"/>. Глубина ограничена <see cref="StorageLimits.MaxDirectoryDepth"/>.
    /// </summary>
    public long CalculateDirectorySize(AuthUser user, string? path)
    {
        var resolved = resolver.Resolve(user.Username, path, allowRoot: true);
        if (!Directory.Exists(resolved.FullPath))
        {
            throw new FileServiceException(404, "Каталог не найден.");
        }

        return SumDirectory(resolved.FullPath, depth: 0);
    }

    private static long SumDirectory(string fullPath, int depth)
    {
        if (depth > StorageLimits.MaxDirectoryDepth)
        {
            throw new FileServiceException(413, "Слишком глубокая вложенность каталогов.");
        }

        long total = 0;
        try
        {
            foreach (var info in new DirectoryInfo(fullPath).EnumerateFileSystemInfos())
            {
                if (info.Name.StartsWith(TempPrefix, StringComparison.Ordinal) || SafePathResolver.IsLinkOrReparsePoint(info.FullName))
                {
                    continue;
                }

                total += info is FileInfo file
                    ? file.Length
                    : SumDirectory(info.FullName, depth + 1);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new FileServiceException(403, "Нет доступа к каталогу.");
        }

        return total;
    }

    public byte[] ReadFile(AuthUser user, string? path)
    {
        var resolved = resolver.Resolve(user.Username, path);

        return WithLock(resolved.FullPath, () =>
        {
            if (!File.Exists(resolved.FullPath))
            {
                throw new FileServiceException(404, "Файл не найден.");
            }

            if (new FileInfo(resolved.FullPath).Length > MaxFileSize)
            {
                throw new FileServiceException(413, "Файл больше допустимого размера.");
            }

            return File.ReadAllBytes(resolved.FullPath);
        });
    }

    public WriteResult WriteFile(AuthUser user, string? path, byte[] content)
    {
        if (content.Length > MaxFileSize)
        {
            throw new FileServiceException(413, "Файл больше допустимого размера.");
        }

        var resolved = resolver.Resolve(user.Username, path);
        var parent = Path.GetDirectoryName(resolved.FullPath)!;
        if (!Directory.Exists(parent))
        {
            throw new FileServiceException(404, "Каталог назначения не найден.");
        }

        return WithLock(resolved.FullPath, () =>
        {
            if (Directory.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Путь занят каталогом.");
            }

            var existedOnDisk = File.Exists(resolved.FullPath);
            var existing = files.FindActive(user.Id, resolved.RelativePath, IgnoreCase);
            var details = $"{resolved.RelativePath} size={content.Length}";

            // Запись во временный файл и атомарная замена: читатель не увидит частично записанный файл.
            var temp = Path.Combine(parent, TempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, content);
                ReplaceAtomically(temp, resolved.FullPath);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            if (existing is null)
            {
                try
                {
                    files.InsertWithOperation(user.Id, Path.GetFileName(resolved.FullPath), resolved.RelativePath,
                        content.Length, user.Id, OperationType.Create, details);
                }
                catch
                {
                    // Метаданных нет, поэтому новый файл не должен оставаться на диске.
                    if (!existedOnDisk)
                    {
                        File.Delete(resolved.FullPath);
                    }

                    throw;
                }

                return new WriteResult(resolved.RelativePath, content.Length, Created: true);
            }

            files.UpdateSizeWithOperation(existing.Id, content.Length, user.Id, details);
            return new WriteResult(resolved.RelativePath, content.Length, Created: false);
        });
    }

    public void DeleteFile(AuthUser user, string? path)
    {
        var resolved = resolver.Resolve(user.Username, path);

        WithLock(resolved.FullPath, () =>
        {
            var existing = files.FindActive(user.Id, resolved.RelativePath, IgnoreCase)
                ?? throw new FileServiceException(404, "Файл не найден.");

            // Сначала удаляем файл с диска, затем помечаем запись. При ошибке диска метаданные не меняются.
            if (File.Exists(resolved.FullPath))
            {
                File.Delete(resolved.FullPath);
            }

            files.SoftDeleteWithOperation(existing.Id, user.Id, resolved.RelativePath);
            return 0;
        });
    }

    public string CreateDirectory(AuthUser user, string? path)
    {
        var resolved = resolver.Resolve(user.Username, path);

        WithLock(resolved.FullPath, () =>
        {
            if (File.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Путь занят файлом.");
            }

            if (Directory.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Каталог уже существует.");
            }

            Directory.CreateDirectory(resolved.FullPath);
            files.RecordOperation(OperationType.Create, user.Id, $"dir:{resolved.RelativePath}");
            return 0;
        });

        return resolved.RelativePath;
    }

    /// <summary>
    /// Удаляет каталог. Непустой каталог удаляется только при recursive = true; в этом случае все файлы
    /// внутри помечаются удалёнными в метаданных. Саму домашнюю папку удалить нельзя.
    /// </summary>
    public void DeleteDirectory(AuthUser user, string? path, bool recursive)
    {
        var resolved = resolver.Resolve(user.Username, path);

        WithLock(resolved.FullPath, () =>
        {
            if (File.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Путь занят файлом, а не каталогом.");
            }

            if (!Directory.Exists(resolved.FullPath))
            {
                throw new FileServiceException(404, "Каталог не найден.");
            }

            if (!recursive && Directory.EnumerateFileSystemEntries(resolved.FullPath).Any())
            {
                throw new FileServiceException(409, "Каталог не пуст. Укажите recursive=true для удаления вместе с содержимым.");
            }

            // Сначала диск, затем метаданные: при ошибке диска журнал и записи Files не меняются.
            Directory.Delete(resolved.FullPath, recursive);
            files.SoftDeleteDirectoryWithOperations(user.Id, user.Id, resolved.RelativePath, IgnoreCase,
                $"dir:{resolved.RelativePath} recursive={recursive}");
            return 0;
        });
    }

    /// <summary>
    /// Пишет файл на диск без записи в журнал. Возвращает снимок предыдущего состояния для отката.
    /// Используется пакетными операциями (распаковка), которые пишут журнал одной транзакцией.
    /// </summary>
    public FileSnapshot WriteFileRaw(AuthUser user, string relativePath, byte[] content)
    {
        if (content.Length > MaxFileSize)
        {
            throw new FileServiceException(413, "Файл больше допустимого размера.");
        }

        var resolved = resolver.Resolve(user.Username, relativePath);
        var parent = Path.GetDirectoryName(resolved.FullPath)!;
        if (!Directory.Exists(parent))
        {
            throw new FileServiceException(404, "Каталог назначения не найден.");
        }

        return WithLock(resolved.FullPath, () =>
        {
            if (Directory.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Путь занят каталогом.");
            }

            var existed = File.Exists(resolved.FullPath);
            byte[]? previous = existed ? File.ReadAllBytes(resolved.FullPath) : null;
            var existing = files.FindActive(user.Id, resolved.RelativePath, IgnoreCase);

            var temp = Path.Combine(parent, TempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, content);
                ReplaceAtomically(temp, resolved.FullPath);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            return new FileSnapshot(resolved.FullPath, resolved.RelativePath, existed, previous, existing?.Id, content.Length);
        });
    }

    /// <summary>Возвращает файловую систему к состоянию до <see cref="WriteFileRaw"/>.</summary>
    public void RestoreFile(FileSnapshot snapshot)
    {
        WithLock(snapshot.FullPath, () =>
        {
            if (snapshot.Existed && snapshot.PreviousContent is not null)
            {
                File.WriteAllBytes(snapshot.FullPath, snapshot.PreviousContent);
            }
            else if (File.Exists(snapshot.FullPath))
            {
                File.Delete(snapshot.FullPath);
            }

            return 0;
        });
    }

    /// <summary>
    /// Создаёт каталог без записи в журнал. Возвращает true, если каталог был создан в этом вызове
    /// (для отката нужно удалить только созданные каталоги).
    /// </summary>
    public bool CreateDirectoryRaw(AuthUser user, string relativePath)
    {
        var resolved = resolver.Resolve(user.Username, relativePath);

        return WithLock(resolved.FullPath, () =>
        {
            if (Directory.Exists(resolved.FullPath))
            {
                return false;
            }

            if (File.Exists(resolved.FullPath))
            {
                throw new FileServiceException(409, "Путь занят файлом.");
            }

            Directory.CreateDirectory(resolved.FullPath);
            return true;
        });
    }

    /// <summary>Удаляет каталог, созданный в ходе отката. Непустой каталог не удаляется.</summary>
    public void RemoveDirectoryIfEmpty(string fullPath)
    {
        WithLock(fullPath, () =>
        {
            if (Directory.Exists(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath);
            }

            return 0;
        });
    }

    /// <summary>Журнал пакетной записи: отдельная точка для вызова <see cref="FileRepository.ApplyBatch"/>.</summary>
    public void ApplyBatch(AuthUser user, IReadOnlyList<BatchChange> changes) => files.ApplyBatch(user.Id, changes);

    /// <summary>Полный путь на диске для относительного пути пользователя (проверки выполняются резолвером).</summary>
    public string ResolveFullPath(AuthUser user, string relativePath) =>
        resolver.Resolve(user.Username, relativePath).FullPath;

    private T WithLock<T>(string key, Func<T> action)
    {
        var gate = _locks.GetOrAdd(key, _ => new object());
        lock (gate)
        {
            return action();
        }
    }

    private static void ReplaceAtomically(string temp, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveRetryAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(MoveRetryDelay * attempt);
            }
        }
    }
}
