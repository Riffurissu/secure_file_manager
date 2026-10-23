using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FileManager.Tests;

/// <summary>Хелперы для HTTP-тестов: регистрация, вход и запросы с токеном.</summary>
public static class ApiClient
{
    public const string Password = "Passw0rdlong";

    public static async Task<string> RegisterAndLoginAsync(HttpClient client, string username)
    {
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = Password });
        if (register.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Регистрация не удалась: {register.StatusCode}");
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
        if (login.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Вход не удался: {login.StatusCode}");
        }

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    public static string NewUsername() => "u" + Guid.NewGuid().ToString("N")[..10];

    public static HttpRequestMessage Authorized(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static ByteArrayContent Bytes(byte[] data, string contentType = "application/octet-stream") =>
        new(data) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } };
}
