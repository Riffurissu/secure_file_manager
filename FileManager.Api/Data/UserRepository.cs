using Microsoft.Data.Sqlite;

namespace FileManager.Api.Data;

public sealed record UserRecord(
    long Id,
    string Username,
    string PasswordHash,
    int FailedLogins,
    DateTimeOffset? LockedUntil);

public sealed class UserRepository(Database db)
{
    public long Create(string username, string passwordHash)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (username, password_hash) VALUES ($username, $hash) RETURNING id;";
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        return (long)cmd.ExecuteScalar()!;
    }

    public UserRecord? FindByUsername(string username)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, failed_logins, locked_until FROM Users WHERE username = $username;";
        cmd.Parameters.AddWithValue("$username", username);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new UserRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? (DateTimeOffset?)null : Database.ParseTime(reader.GetString(4)));
    }

    public string? FindUsernameById(long id)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT username FROM Users WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public void UpdateLoginState(long userId, int failedLogins, DateTimeOffset? lockedUntil)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Users SET failed_logins = $failed, locked_until = $locked WHERE id = $id;";
        cmd.Parameters.AddWithValue("$failed", failedLogins);
        cmd.Parameters.AddWithValue("$locked", lockedUntil is null ? DBNull.Value : Database.FormatTime(lockedUntil.Value));
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }
}
