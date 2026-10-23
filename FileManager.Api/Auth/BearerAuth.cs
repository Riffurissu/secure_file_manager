namespace FileManager.Api.Auth;

/// <summary>Пользователь, прошедший проверку bearer-токена.</summary>
public sealed record AuthUser(long Id, string Username);

public static class BearerAuth
{
    private const string Prefix = "Bearer ";

    /// <summary>Возвращает пользователя для действующего токена из заголовка Authorization, иначе null.</summary>
    public static AuthUser? TryGetUser(HttpRequest request, AuthService auth)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var userId = auth.ValidateToken(header[Prefix.Length..]);
        if (userId is null)
        {
            return null;
        }

        var username = auth.GetUsername(userId.Value);
        return username is null ? null : new AuthUser(userId.Value, username);
    }
}
