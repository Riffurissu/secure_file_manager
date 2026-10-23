namespace FileManager.Api.Files;

/// <summary>
/// Единые лимиты размера и структуры. Один и тот же лимит применяется во всех операциях,
/// к которым он относится, поэтому поведение не зависит от того, через какой эндпоинт пришли данные.
/// </summary>
public static class StorageLimits
{
    /// <summary>Максимальный размер одного файла: запись, чтение, загрузка, JSON/XML-документ, сам ZIP-архив.</summary>
    public const long MaxFileSize = 10 * 1024 * 1024;

    /// <summary>Суммарный размер распакованных данных за одну операцию с архивом (создание или распаковка).</summary>
    public const long MaxArchiveUncompressedTotal = 50 * 1024 * 1024;

    /// <summary>Максимальное число записей в архиве.</summary>
    public const int MaxArchiveEntries = 1000;

    /// <summary>Максимальная глубина вложенности каталогов (для архивов и JSON/XML).</summary>
    public const int MaxDirectoryDepth = 32;

    /// <summary>Максимальный коэффициент сжатия записи архива.</summary>
    public const long MaxCompressionRatio = 100;

    /// <summary>Коэффициент сжатия проверяется только для записей больше этого размера.</summary>
    public const long CompressionRatioThreshold = 1024 * 1024;

    /// <summary>Максимальное число узлов в JSON/XML-документе.</summary>
    public const int MaxStructuredNodes = 50_000;
}
