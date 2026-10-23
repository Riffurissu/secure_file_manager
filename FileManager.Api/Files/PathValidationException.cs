namespace FileManager.Api.Files;

/// <summary>Путь не прошёл проверку. Сообщение безопасно для показа клиенту.</summary>
public sealed class PathValidationException(string message) : Exception(message);
