using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FileManager.Tests;

/// <summary>
/// Приложение с изолированными хранилищем и базой на время одного теста.
/// Переменные задаются через конфигурацию, поэтому Program читает их как при обычном запуске.
/// </summary>
public sealed class IsolatedApp : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fm-it-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Root);
        builder.UseSetting("Storage:Root", Path.Combine(Root, "storage"));
        builder.UseSetting("Database:Path", Path.Combine(Root, "test.db"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TryDeleteDirectory(Root);
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Файл БД может быть ещё открыт: временный каталог очистит ОС.
        }
    }
}
