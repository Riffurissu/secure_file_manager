namespace FileManager.Api.Files;

/// <summary>Ошибка файловой операции с HTTP-статусом. Сообщение безопасно для клиента.</summary>
public sealed class FileServiceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
