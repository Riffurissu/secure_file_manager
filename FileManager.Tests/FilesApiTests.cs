using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FileManager.Tests;

public class FilesApiTests : IClassFixture<IsolatedAppFixture>
{
    private readonly HttpClient _client;

    public FilesApiTests(IsolatedAppFixture fixture) => _client = fixture.App.CreateClient();

    [Fact]
    public async Task Write_read_delete_file_roundtrip()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var data = Encoding.UTF8.GetBytes("hello");

        var put = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=docs/hello.txt", token, ApiClient.Bytes(data)));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var get = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=docs/hello.txt", token));
        Assert.Equal(data, await get.Content.ReadAsByteArrayAsync());

        var del = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Delete,
            "/api/files?path=docs/hello.txt", token));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var gone = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=docs/hello.txt", token));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("../other/secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("a/../../other/x.txt")]
    public async Task Path_traversal_is_rejected(string path)
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=" + Uri.EscapeDataString(path), token, ApiClient.Bytes([1, 2, 3])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Users_cannot_read_each_others_files()
    {
        var alice = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var bob = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=private.txt", alice, ApiClient.Bytes([42])));

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get,
            "/api/files/content?path=private.txt", bob));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_upload_is_rejected()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        var big = new byte[10 * 1024 * 1024 + 1];

        var response = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=big.bin", token, ApiClient.Bytes(big)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Directory_listing_shows_created_entries()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post, "/api/folders?path=reports", token));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=reports/q1.txt", token, ApiClient.Bytes([1])));

        var list = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files?path=reports", token));
        var entries = await list.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("q1.txt", entries[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Operations_journal_records_create_modify_delete()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        const string path = "journal.txt";

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=" + path, token, ApiClient.Bytes([1])));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=" + path, token, ApiClient.Bytes([1, 2])));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Delete,
            "/api/files?path=" + path, token));

        var journal = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/operations", token));
        var items = await journal.Content.ReadFromJsonAsync<JsonElement>();
        var types = items.EnumerateArray().Select(i => i.GetProperty("operationType").GetString()).ToList();

        Assert.Equal(new[] { "create", "modify", "delete" }, types);
    }

    [Fact]
    public async Task Deleted_file_can_be_recreated_at_same_path()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        const string url = "/api/files/content?path=again.txt";

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put, url, token, ApiClient.Bytes([1])));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Delete, "/api/files?path=again.txt", token));

        var recreated = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put, url, token, ApiClient.Bytes([2])));
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
    }

    [Fact]
    public async Task Concurrent_writes_to_same_path_do_not_fail()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        const string url = "/api/files/content?path=race.txt";

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var request = ApiClient.Authorized(HttpMethod.Put, url, token, ApiClient.Bytes([(byte)i]));
            return await _client.SendAsync(request);
        });

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Неожиданный статус {r.StatusCode}"));
    }

    [Fact]
    public async Task Directory_size_sums_nested_files()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=dir/a.bin", token, ApiClient.Bytes(new byte[10])));
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=dir/sub/b.bin", token, ApiClient.Bytes(new byte[25])));

        var size = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files/size?path=dir", token));
        var body = await size.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, size.StatusCode);
        Assert.Equal(35, body.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Listing_with_withSize_fills_directory_size()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Put,
            "/api/files/content?path=box/x.bin", token, ApiClient.Bytes(new byte[7])));

        var withSize = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files?withSize=true", token));
        var entries = await withSize.Content.ReadFromJsonAsync<JsonElement>();
        var dir = entries.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "box");

        Assert.Equal(7, dir.GetProperty("size").GetInt64());

        var plain = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files", token));
        var plainEntries = await plain.Content.ReadFromJsonAsync<JsonElement>();
        var plainDir = plainEntries.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "box");

        Assert.Equal(JsonValueKind.Null, plainDir.GetProperty("size").ValueKind);
    }

    [Fact]
    public async Task Directory_size_of_missing_path_is_not_found()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());

        var size = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files/size?path=nope", token));

        Assert.Equal(HttpStatusCode.NotFound, size.StatusCode);
    }

    [Fact]
    public async Task Empty_directory_has_zero_size()
    {
        var token = await ApiClient.RegisterAndLoginAsync(_client, ApiClient.NewUsername());
        await _client.SendAsync(ApiClient.Authorized(HttpMethod.Post, "/api/folders?path=empty", token));

        var size = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/files/size?path=empty", token));
        var body = await size.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("size").GetInt64());
    }
}
