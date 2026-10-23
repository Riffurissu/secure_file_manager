using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FileManager.Tests;

public class AuthApiTests : IClassFixture<IsolatedAppFixture>
{
    private readonly HttpClient _client;

    public AuthApiTests(IsolatedAppFixture fixture) => _client = fixture.App.CreateClient();

    [Fact]
    public async Task Register_then_login_returns_token()
    {
        var username = ApiClient.NewUsername();
        var token = await ApiClient.RegisterAndLoginAsync(_client, username);

        Assert.False(string.IsNullOrWhiteSpace(token));

        var me = await _client.SendAsync(ApiClient.Authorized(HttpMethod.Get, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Duplicate_username_is_rejected()
    {
        var username = ApiClient.NewUsername();
        await ApiClient.RegisterAndLoginAsync(_client, username);

        var again = await _client.PostAsJsonAsync("/api/auth/register", new { username, password = ApiClient.Password });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("bad name")]
    [InlineData("../evil")]
    public async Task Invalid_username_is_rejected(string username)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new { username, password = ApiClient.Password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_return_same_response()
    {
        var username = ApiClient.NewUsername();
        await ApiClient.RegisterAndLoginAsync(_client, username);

        var wrongPassword = await _client.PostAsJsonAsync("/api/auth/login", new { username, password = "WrongPass1!" });
        var unknownUser = await _client.PostAsJsonAsync("/api/auth/login", new { username = "nobody" + username, password = "WrongPass1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures_even_with_right_password()
    {
        var username = ApiClient.NewUsername();
        await ApiClient.RegisterAndLoginAsync(_client, username);

        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsJsonAsync("/api/auth/login", new { username, password = "WrongPass1!" });
        }

        var locked = await _client.PostAsJsonAsync("/api/auth/login", new { username, password = ApiClient.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_without_token_is_unauthorized()
    {
        var response = await _client.GetAsync("/api/files");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_is_unauthorized()
    {
        using var request = ApiClient.Authorized(HttpMethod.Get, "/api/files", "not-a-real-token");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sql_injection_in_username_does_not_bypass_login()
    {
        var username = ApiClient.NewUsername();
        await ApiClient.RegisterAndLoginAsync(_client, username);

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "' OR '1'='1", password = "' OR '1'='1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
