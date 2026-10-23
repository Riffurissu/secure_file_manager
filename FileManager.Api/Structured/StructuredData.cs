using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FileManager.Api.Files;

namespace FileManager.Api.Structured;

/// <summary>
/// Разбор и нормализация JSON и XML. Вход считается недоверенным:
/// лимиты размера (общий лимит файла), глубины и числа узлов; JSON без комментариев и trailing comma,
/// без дубликатов полей; XML без DTD и внешних сущностей. Результат пересериализуется стандартными библиотеками.
/// </summary>
public static class StructuredData
{
    public const long MaxBytes = StorageLimits.MaxFileSize;
    public const int MaxDepth = StorageLimits.MaxDirectoryDepth;
    public const int MaxNodes = StorageLimits.MaxStructuredNodes;

    private static readonly JsonSerializerOptions WriteJsonOptions = new() { WriteIndented = true };

    /// <summary>Проверяет JSON и возвращает его в каноническом виде (UTF-8, с отступами).</summary>
    public static byte[] NormalizeJson(byte[] input)
    {
        EnsureSize(input);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            throw new StructuredDataException(400, "Некорректный JSON.");
        }

        using (document)
        {
            var count = 0;
            CheckJsonNodes(document.RootElement, ref count);
            return JsonSerializer.SerializeToUtf8Bytes(document.RootElement, WriteJsonOptions);
        }
    }

    /// <summary>Проверяет XML без DTD и внешних сущностей и возвращает его в каноническом виде.</summary>
    public static byte[] NormalizeXml(byte[] input)
    {
        EnsureSize(input);

        try
        {
            using (var checkStream = new MemoryStream(input))
            using (var checkReader = XmlReader.Create(checkStream, CreateXmlSettings()))
            {
                CheckXmlStructure(checkReader);
            }

            using var loadStream = new MemoryStream(input);
            using var loadReader = XmlReader.Create(loadStream, CreateXmlSettings());
            var document = XDocument.Load(loadReader);

            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
        catch (XmlException)
        {
            throw new StructuredDataException(400, "Некорректный XML.");
        }
    }

    private static void EnsureSize(byte[] input)
    {
        if (input.Length > MaxBytes)
        {
            throw new StructuredDataException(413, "Документ больше допустимого размера.");
        }
    }

    private static void CheckJsonNodes(JsonElement element, ref int count)
    {
        if (++count > MaxNodes)
        {
            throw new StructuredDataException(413, "Слишком много элементов в JSON.");
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CheckJsonNodes(item, ref count);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            // Дубликаты имён полей запрещены: иначе одно и то же поле может читаться по-разному разными потребителями.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new StructuredDataException(400, "Повторяющиеся имена полей в JSON запрещены.");
                }

                CheckJsonNodes(property.Value, ref count);
            }
        }
    }

    private static XmlReaderSettings CreateXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = (int)Math.Min(MaxBytes, int.MaxValue),
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static void CheckXmlStructure(XmlReader reader)
    {
        var nodes = 0;
        var depth = 0;

        while (reader.Read())
        {
            nodes += 1 + reader.AttributeCount;
            if (nodes > MaxNodes)
            {
                throw new StructuredDataException(413, "Слишком много узлов в XML.");
            }

            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
                if (depth > MaxDepth)
                {
                    throw new StructuredDataException(413, "Слишком глубокая вложенность XML.");
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                depth--;
            }
        }
    }
}
