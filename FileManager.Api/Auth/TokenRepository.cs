using FileManager.Api.Data;

namespace FileManager.Api.Auth;

/// <summary>Хранение SHA-256 от токенов. Сами токены в БД не попадают.</summary>
public sealed class TokenRepository(Database db)
{
    public void Add(string tokenHash, long userId, DateTimeOffset expiresAt)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO AuthTokens (token_hash, user_id, expires_at) VALUES ($hash, $user, $expires);";
        cmd.Parameters.AddWithValue("$hash", tokenHash);
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$expires", Database.FormatTime(expiresAt));
        cmd.ExecuteNonQuery();
    }

    public long? FindActiveUserId(string tokenHash, DateTimeOffset now)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id, expires_at FROM AuthTokens WHERE token_hash = $hash;";
        cmd.Parameters.AddWithValue("$hash", tokenHash);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var expires = Database.ParseTime(reader.GetString(1));
        return expires > now ? reader.GetInt64(0) : null;
    }
}
