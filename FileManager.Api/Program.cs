using System.Threading.RateLimiting;
using FileManager.Api.Archive;
using FileManager.Api.Auth;
using FileManager.Api.Data;
using FileManager.Api.Files;
using FileManager.Api.Structured;
using FileManager.Api.System;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Пути берутся из переменных окружения (Storage__Root, Database__Path). В контейнере они указывают на том /data.
var storageRoot = Path.GetFullPath(builder.Configuration["Storage:Root"] ?? "storage");
Directory.CreateDirectory(storageRoot);

var database = new Database(builder.Configuration["Database:Path"] ?? "filemanager.db");
database.Initialize();

builder.Services.AddSingleton(database);
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<FileRepository>();
builder.Services.AddSingleton<LoginAttemptRepository>();
builder.Services.AddSingleton<TokenRepository>();
builder.Services.AddSingleton(sp => new AuthService(
    sp.GetRequiredService<UserRepository>(),
    sp.GetRequiredService<TokenRepository>(),
    sp.GetRequiredService<LoginAttemptRepository>(),
    storageRoot));
builder.Services.AddSingleton(new SafePathResolver(storageRoot));
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<ZipService>();
builder.Services.AddSingleton<DriveService>();

// Ограничение частоты входа по IP: не более 10 попыток в минуту с одного адреса.
// Лимит переопределяется конфигурацией (RateLimiting:LoginPerMinute) — тесты поднимают его,
// чтобы длинная серия запросов из одного процесса не упиралась в ограничение.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var loginPerMinute = builder.Configuration.GetValue("RateLimiting:LoginPerMinute", 10);
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseRateLimiter();

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapPost("/api/auth/register", (RegisterRequest? request, AuthService auth) =>
{
    if (request?.Username is null || request.Password is null)
    {
        return Results.BadRequest(new { error = "Укажите имя пользователя и пароль." });
    }

    try
    {
        auth.Register(request.Username, request.Password);
        return Results.Created($"/api/users/{request.Username}", new { username = request.Username });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "Пользователь с таким именем уже существует." });
    }
});

app.MapPost("/api/auth/login", (LoginRequest? request, AuthService auth, HttpContext http) =>
{
    if (request?.Username is null || request.Password is null)
    {
        return Results.Json(new { error = "Неверный логин или пароль." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = auth.Login(request.Username, request.Password, http.Connection.RemoteIpAddress?.ToString());

    // Все неудачи (неверный пароль, блокировка, неизвестный пользователь) выглядят одинаково.
    return result.Outcome == LoginOutcome.Success
        ? Results.Ok(new { token = result.Token })
        : Results.Json(new { error = "Неверный логин или пароль." }, statusCode: StatusCodes.Status401Unauthorized);
}).RequireRateLimiting("login");

app.MapGet("/api/auth/me", (HttpRequest http, AuthService auth) =>
{
    var user = BearerAuth.TryGetUser(http, auth);
    return user is null
        ? Unauthorized()
        : Results.Ok(new { userId = user.Id, username = user.Username });
});

app.MapGet("/api/files", (string? path, bool? withSize, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user => Results.Ok(files.ListDirectory(user, path, withSize ?? false))));

app.MapGet("/api/files/size", (string? path, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user => Results.Ok(new { path, size = files.CalculateDirectorySize(user, path) })));

app.MapGet("/api/files/content", (string? path, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user => Results.File(files.ReadFile(user, path), "application/octet-stream")));

app.MapPut("/api/files/content", async (string? path, HttpRequest http, AuthService auth, FileService files) =>
{
    var user = BearerAuth.TryGetUser(http, auth);
    if (user is null)
    {
        return Unauthorized();
    }

    var body = await ReadBodyLimitedAsync(http, FileService.MaxFileSize);
    if (body is null)
    {
        return TooLarge();
    }

    return Handle(() => WriteResponse(files.WriteFile(user, path, body)));
});

app.MapPost("/api/structured/validate", async (string? format, HttpRequest http, AuthService auth) =>
{
    var user = BearerAuth.TryGetUser(http, auth);
    if (user is null)
    {
        return Unauthorized();
    }

    var body = await ReadBodyLimitedAsync(http, StructuredData.MaxBytes);
    if (body is null)
    {
        return TooLarge();
    }

    return HandleStructured(() =>
    {
        var normalized = NormalizeByFormat(format, body);
        return Results.Ok(new
        {
            format = format!.ToLowerInvariant(),
            size = normalized.Length,
            content = System.Text.Encoding.UTF8.GetString(normalized),
        });
    });
});

app.MapGet("/api/structured/read", (string? path, string? format, HttpRequest http, AuthService auth, FileService files) =>
{
    var user = BearerAuth.TryGetUser(http, auth);
    if (user is null)
    {
        return Unauthorized();
    }

    return Handle(() =>
    {
        var content = files.ReadFile(user, path);
        var normalized = NormalizeByFormat(format, content);
        return Results.Ok(new
        {
            path,
            format = format!.ToLowerInvariant(),
            size = normalized.Length,
            content = System.Text.Encoding.UTF8.GetString(normalized),
        });
    });
});

app.MapPost("/api/structured/save", async (string? path, string? format, HttpRequest http, AuthService auth, FileService files) =>
{
    var user = BearerAuth.TryGetUser(http, auth);
    if (user is null)
    {
        return Unauthorized();
    }

    var body = await ReadBodyLimitedAsync(http, StructuredData.MaxBytes);
    if (body is null)
    {
        return TooLarge();
    }

    return HandleStructured(() =>
    {
        var normalized = NormalizeByFormat(format, body);
        return Handle(() => WriteResponse(files.WriteFile(user, path, normalized)));
    });
});

app.MapDelete("/api/files", (string? path, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user =>
    {
        files.DeleteFile(user, path);
        return Results.NoContent();
    }));

app.MapPost("/api/folders", (string? path, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user =>
    {
        var created = files.CreateDirectory(user, path);
        return Results.Created($"/api/files?path={Uri.EscapeDataString(created)}", new { path = created });
    }));

app.MapDelete("/api/folders", (string? path, bool recursive, HttpRequest http, AuthService auth, FileService files) =>
    WithUser(http, auth, user =>
    {
        files.DeleteDirectory(user, path, recursive);
        return Results.NoContent();
    }));

app.MapGet("/api/system/drives", (HttpRequest http, AuthService auth, DriveService drives) =>
    WithUser(http, auth, _ => Results.Ok(drives.ListDrives())));

app.MapPost("/api/archive/create", (string? source, string? archive, HttpRequest http, AuthService auth, ZipService zip) =>
    WithUser(http, auth, user => Results.Ok(zip.Create(user, source, archive))));

app.MapPost("/api/archive/extract", (string? archive, string? destination, HttpRequest http, AuthService auth, ZipService zip) =>
    WithUser(http, auth, user => Results.Ok(zip.Extract(user, archive, destination))));

app.MapGet("/api/operations", (HttpRequest http, AuthService auth, FileRepository fileRepository) =>
    WithUser(http, auth, user => Results.Ok(fileRepository.ListOperations(user.Id))));

app.Run();

// Читает тело запроса, не загружая в память больше лимита + 1 байт. Возвращает null при превышении.
static async Task<byte[]?> ReadBodyLimitedAsync(HttpRequest http, long limit)
{
    if (http.ContentLength is { } declared && declared > limit)
    {
        return null;
    }

    using var buffer = new MemoryStream();
    var chunk = new byte[81920];
    int read;
    while ((read = await http.Body.ReadAsync(chunk)) > 0)
    {
        if (buffer.Length + read > limit)
        {
            return null;
        }

        buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
}

static IResult TooLarge() =>
    Results.Json(new { error = "Файл больше допустимого размера." }, statusCode: StatusCodes.Status413PayloadTooLarge);

static IResult WriteResponse(WriteResult result) => result.Created
    ? Results.Created($"/api/files/content?path={Uri.EscapeDataString(result.Path)}", result)
    : Results.Ok(result);

static byte[] NormalizeByFormat(string? format, byte[] body) => format?.ToLowerInvariant() switch
{
    "json" => StructuredData.NormalizeJson(body),
    "xml" => StructuredData.NormalizeXml(body),
    _ => throw new StructuredDataException(400, "Укажите формат: json или xml."),
};

static IResult HandleStructured(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (StructuredDataException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
}

static IResult WithUser(HttpRequest http, AuthService auth, Func<AuthUser, IResult> action)
{
    var user = BearerAuth.TryGetUser(http, auth);
    return user is null ? Unauthorized() : Handle(() => action(user));
}

static IResult Unauthorized() =>
    Results.Json(new { error = "Требуется авторизация." }, statusCode: StatusCodes.Status401Unauthorized);

// Переводит ожидаемые ошибки путей и файлов в HTTP-ответ. Прочие исключения уходят в 500 без деталей.
static IResult Handle(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (PathValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FileServiceException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
    catch (StructuredDataException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
}


public sealed record RegisterRequest(string? Username, string? Password);

public sealed record LoginRequest(string? Username, string? Password);

// Нужно для WebApplicationFactory<Program> в тестах.
public partial class Program;
