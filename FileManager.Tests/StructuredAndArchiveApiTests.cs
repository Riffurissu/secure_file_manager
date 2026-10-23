using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FileManager.Tests;

public class StructuredApiTests : IClassFixture<IsolatedAppFixture>
{
    private readonly HttpClient _client;

    public StructuredApiTests(IsolatedAppFixture fixture) => _client = fixture.App.CreateClient();

    [Fact]
    public async Task Valid_json_is_normalized()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var input = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":[1,2]}");

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=json", token, ApiClient.Bytes(input)));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"a\"", body.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1 /* comment */}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    public async Task Unsafe_json_is_rejected(string json)
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=json", token, ApiClient.Bytes(Encoding.UTF8.GetBytes(json))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deeply_nested_json_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var deep = new string('[', 100) + new string(']', 100);

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=json", token, ApiClient.Bytes(Encoding.UTF8.GetBytes(deep))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Xml_with_external_entity_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE root [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <root>&xxe;</root>
            """;

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=xml", token, ApiClient.Bytes(Encoding.UTF8.GetBytes(xxe))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Valid_xml_is_normalized()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var input = Encoding.UTF8.GetBytes("<root><item id=\"1\">x</item></root>");

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=xml", token, ApiClient.Bytes(input)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Save_and_read_structured_file()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var input = Encoding.UTF8.GetBytes("{\"name\":\"test\"}");

        var save = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/save?path=data.json&format=json", token, ApiClient.Bytes(input)));
        Assert.Contains(save.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });

        var read = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/structured/read?path=data.json&format=json", token));
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("test", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Unknown_format_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/structured/validate?format=yaml", token, ApiClient.Bytes([1])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public class ArchiveApiTests : IClassFixture<IsolatedAppFixture>
{
    private readonly HttpClient _client;

    public ArchiveApiTests(IsolatedAppFixture fixture) => _client = fixture.App.CreateClient();

    [Fact]
    public async Task Create_then_extract_roundtrip()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=src/a.txt", token, ApiClient.Bytes(Encoding.UTF8.GetBytes("A"))));

        var create = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/create?source=src&archive=backup.zip", token));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var extract = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/extract?archive=backup.zip&destination=restored", token));
        Assert.Equal(HttpStatusCode.OK, extract.StatusCode);

        var read = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=restored/a.txt", token));
        Assert.Equal("A", Encoding.UTF8.GetString(await read.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Zip_slip_entry_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var evil = BuildZip(("../escape.txt", Encoding.UTF8.GetBytes("pwned")));

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=evil.zip", token, ApiClient.Bytes(evil)));

        var extract = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/extract?archive=evil.zip&destination=out", token));

        Assert.Equal(HttpStatusCode.BadRequest, extract.StatusCode);
    }

    [Fact]
    public async Task Zip_bomb_by_declared_size_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        // 60 МБ нулей сжимаются очень сильно, превышая коэффициент и лимит распаковки.
        var bomb = BuildZip(("bomb.bin", new byte[60 * 1024 * 1024]));

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=bomb.zip", token, ApiClient.Bytes(bomb)));

        var extract = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/extract?archive=bomb.zip&destination=bomb", token));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, extract.StatusCode);
    }

    [Fact]
    public async Task Failed_extraction_leaves_no_partial_files_and_no_journal_entries()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        // "blocker" — файл, а архив хочет создать в этом месте каталог: ошибка после записи good.txt.
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=blocker", token, ApiClient.Bytes([1])));

        var archive = BuildZip(
            ("good.txt", Encoding.UTF8.GetBytes("GOOD")),
            ("blocker/inner.txt", Encoding.UTF8.GetBytes("X")));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=conflict.zip", token, ApiClient.Bytes(archive)));

        var extract = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/extract?archive=conflict.zip&destination=", token));
        Assert.Equal(HttpStatusCode.Conflict, extract.StatusCode);

        var goodGone = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=good.txt", token));
        Assert.Equal(HttpStatusCode.NotFound, goodGone.StatusCode);

        var journal = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/operations", token));
        var entries = (await journal.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(i => i.GetProperty("details").GetString() ?? string.Empty).ToList();
        Assert.DoesNotContain(entries, d => d.Contains("good.txt"));
    }

    [Fact]
    public async Task Failed_extraction_restores_overwritten_file_content()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=keep.txt", token, ApiClient.Bytes(Encoding.UTF8.GetBytes("OLD"))));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=blocker", token, ApiClient.Bytes([1])));

        var archive = BuildZip(
            ("keep.txt", Encoding.UTF8.GetBytes("NEW")),
            ("blocker/inner.txt", Encoding.UTF8.GetBytes("X")));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=overwrite.zip", token, ApiClient.Bytes(archive)));

        var extract = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/extract?archive=overwrite.zip&destination=", token));
        Assert.Equal(HttpStatusCode.Conflict, extract.StatusCode);

        var kept = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=keep.txt", token));
        Assert.Equal("OLD", Encoding.UTF8.GetString(await kept.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Archive_without_zip_extension_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post,
            "/api/archive/create?source=&archive=backup.tar", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return memory.ToArray();
    }
}
