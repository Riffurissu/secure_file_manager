namespace FileManager.Tests;

/// <summary>Один изолированный экземпляр приложения на класс тестов.</summary>
public sealed class IsolatedAppFixture : IDisposable
{
    public IsolatedApp App { get; } = new();

    public void Dispose() => App.Dispose();
}
