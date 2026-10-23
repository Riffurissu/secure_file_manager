namespace FileManager.Api.Files;

/// <summary>
/// Результат разрешения пути.
/// FullPath — абсолютный путь на диске внутри домашней папки пользователя.
/// RelativePath — каноническая форма для хранения в Files.location: разделитель "/", без ведущих разделителей.
/// Для корня домашней папки RelativePath пустая строка.
/// </summary>
public sealed record ResolvedPath(string FullPath, string RelativePath);
