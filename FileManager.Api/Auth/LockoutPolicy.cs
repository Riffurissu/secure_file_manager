namespace FileManager.Api.Auth;

/// <summary>
/// Блокировка по времени: 5 неудач подряд дают блокировку на 1 минуту,
/// затем 5 минут, далее 15 минут. Успешный вход сбрасывает счётчик.
/// </summary>
public static class LockoutPolicy
{
    public const int FailuresBeforeLock = 5;

    public static TimeSpan? For(int failedCount) => failedCount switch
    {
        < FailuresBeforeLock => null,
        FailuresBeforeLock => TimeSpan.FromMinutes(1),
        FailuresBeforeLock + 1 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };
}
