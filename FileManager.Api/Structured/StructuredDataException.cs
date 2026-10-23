namespace FileManager.Api.Structured;

/// <summary>Ошибка разбора JSON/XML. Сообщение безопасно для клиента и не содержит деталей парсера.</summary>
public sealed class StructuredDataException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
