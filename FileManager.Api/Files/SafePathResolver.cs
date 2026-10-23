using FileManager.Api.Auth;

namespace FileManager.Api.Files;

/// <summary>
/// Преобразует относительный пользовательский путь в путь внутри storage/&lt;username&gt;/.
/// Любой входной путь считается недоверенным: при сомнении путь отклоняется.
/// </summary>
public sealed class SafePathResolver
{
    private const int MaxRelativePathLength = 1024;
    private const int MaxSegmentLength = 255;

    private static readonly char[] ForbiddenChars = ['<', '>', '"', '|', '?', '*', ':'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _storageRoot;

    public SafePathResolver(string storageRoot)
    {
        _storageRoot = Path.GetFullPath(storageRoot);
    }

    /// <summary>
    /// Разрешает путь относительно домашней папки пользователя.
    /// allowRoot = true разрешает пустой путь (сама домашняя папка), например для листинга каталога.
    /// </summary>
    public ResolvedPath Resolve(string username, string? relativePath, bool allowRoot = false)
    {
        if (!AuthService.IsValidUsername(username))
        {
            throw new PathValidationException("Некорректное имя пользователя.");
        }

        var home = Path.GetFullPath(Path.Combine(_storageRoot, username));

        if (string.IsNullOrEmpty(relativePath))
        {
            if (allowRoot)
            {
                EnsureNoReparsePoints(home, home);
                return new ResolvedPath(home, string.Empty);
            }

            throw new PathValidationException("Укажите путь к файлу или каталогу.");
        }

        if (relativePath.Length > MaxRelativePathLength)
        {
            throw new PathValidationException($"Путь длиннее {MaxRelativePathLength} символов.");
        }

        if (relativePath != relativePath.Trim())
        {
            throw new PathValidationException("Путь не должен начинаться или заканчиваться пробелом.");
        }

        var normalized = relativePath.Replace('\\', '/');

        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized))
        {
            throw new PathValidationException("Абсолютные пути запрещены.");
        }

        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(string.IsNullOrEmpty))
        {
            throw new PathValidationException("Путь содержит пустые сегменты.");
        }

        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }

        var full = Path.GetFullPath(Path.Combine(home, string.Join(Path.DirectorySeparatorChar, segments)));

        // Итоговый путь обязан лежать строго внутри домашней папки.
        var homePrefix = home.EndsWith(Path.DirectorySeparatorChar)
            ? home
            : home + Path.DirectorySeparatorChar;
        if (!full.StartsWith(homePrefix, PathComparison))
        {
            throw new PathValidationException("Путь выходит за пределы домашней папки.");
        }

        EnsureNoReparsePoints(home, full);

        return new ResolvedPath(full, string.Join('/', segments));
    }

    private static void ValidateSegment(string segment)
    {
        if (segment is "." or "..")
        {
            throw new PathValidationException("Сегменты '.' и '..' запрещены.");
        }

        if (segment.Length > MaxSegmentLength)
        {
            throw new PathValidationException($"Имя длиннее {MaxSegmentLength} символов.");
        }

        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            throw new PathValidationException("Имя не может заканчиваться точкой или пробелом.");
        }

        foreach (var ch in segment)
        {
            if (ch < 0x20 || Array.IndexOf(ForbiddenChars, ch) >= 0)
            {
                throw new PathValidationException("Имя содержит недопустимые символы.");
            }
        }

        // CON.txt, nul.tar.gz и т.п. тоже зарезервированы Windows.
        var baseName = segment.Split('.')[0];
        if (ReservedNames.Contains(baseName))
        {
            throw new PathValidationException("Имя зарезервировано системой.");
        }
    }

    /// <summary>
    /// Проверяет каждый существующий сегмент от домашней папки до цели на отсутствие reparse point
    /// (симлинк, junction). Несуществующие сегменты пропускаются: их нельзя подменить reparse point.
    /// </summary>
    private static void EnsureNoReparsePoints(string home, string target)
    {
        var current = home;
        CheckSingle(current);

        if (target.Length <= home.Length)
        {
            return;
        }

        var remainder = target[home.Length..].TrimStart(Path.DirectorySeparatorChar);
        foreach (var part in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!CheckSingle(current))
            {
                return;
            }
        }

        static bool CheckSingle(string path)
        {
            if (IsLinkOrReparsePoint(path))
            {
                throw new PathValidationException("Симлинки и точки повторной обработки запрещены.");
            }

            return File.Exists(path) || Directory.Exists(path);
        }
    }

    /// <summary>
    /// Симлинк или reparse point. Проверка LinkTarget работает и на Linux, и для битых симлинков,
    /// которые File.Exists не видит. Атрибут ReparsePoint нужен для Windows-специфичных точек (junction, dedup).
    /// </summary>
    public static bool IsLinkOrReparsePoint(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
        {
            return true;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
