using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FileManager.Api.Data;
using FileManager.Api.Security;

namespace FileManager.Api.Auth;

public enum LoginOutcome { Success, Failed }

public sealed record LoginResult(LoginOutcome Outcome, string? Token);

public sealed partial class AuthService(
    UserRepository users,
    TokenRepository tokens,
    LoginAttemptRepository attempts,
    string storageRoot)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("timing-equalizer"));

    private readonly object _loginGate = new();

    [GeneratedRegex("^[a-zA-Z0-9_]{3,32}$")]
    private static partial Regex UsernamePattern();

    public static bool IsValidUsername(string username) => UsernamePattern().IsMatch(username);

    public static bool IsValidPassword(string password) => password.Length is >= 8 and <= 128;

    /// <summary>Создаёт пользователя и его домашнюю папку. Имя проверено регулярным выражением, путь безопасен.</summary>
    public void Register(string username, string password)
    {
        if (!IsValidUsername(username))
        {
            throw new ArgumentException("Имя пользователя: 3-32 символа, латиница, цифры или _.");
        }

        if (!IsValidPassword(password))
        {
            throw new ArgumentException("Пароль: от 8 до 128 символов.");
        }

        users.Create(username, PasswordHasher.Hash(password));
        Directory.CreateDirectory(Path.Combine(storageRoot, username));
    }

    public LoginResult Login(string username, string password, string? ip)
    {
        // Вход сериализован, чтобы параллельные неудачи не теряли увеличение счётчика.
        lock (_loginGate)
        {
            var now = DateTimeOffset.UtcNow;
            var user = users.FindByUsername(username);

            if (user is null)
            {
                // Тот же объём работы, что и для существующего пользователя.
                _ = PasswordHasher.Verify(password, DummyHash.Value);
                attempts.Add(username, ip, false, now);
                return new LoginResult(LoginOutcome.Failed, null);
            }

            if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                attempts.Add(username, ip, false, now);
                return new LoginResult(LoginOutcome.Failed, null);
            }

            if (!PasswordHasher.Verify(password, user.PasswordHash))
            {
                var failed = user.FailedLogins + 1;
                var lockFor = LockoutPolicy.For(failed);
                users.UpdateLoginState(user.Id, failed, lockFor is null ? null : now + lockFor.Value);
                attempts.Add(username, ip, false, now);
                return new LoginResult(LoginOutcome.Failed, null);
            }

            users.UpdateLoginState(user.Id, 0, null);
            attempts.Add(username, ip, true, now);
            return new LoginResult(LoginOutcome.Success, IssueToken(user.Id, now));
        }
    }

    /// <summary>Возвращает id пользователя для действующего токена или null.</summary>
    public long? ValidateToken(string token) =>
        tokens.FindActiveUserId(HashToken(token), DateTimeOffset.UtcNow);

    public string? GetUsername(long userId) => users.FindUsernameById(userId);

    private string IssueToken(long userId, DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        tokens.Add(HashToken(token), userId, now + TokenLifetime);
        return token;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
