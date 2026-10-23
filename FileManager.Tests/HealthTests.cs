using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FileManager.Tests;

public class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body!["status"]);
    }
}
