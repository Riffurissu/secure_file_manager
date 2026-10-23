using Microsoft.Data.Sqlite;

namespace FileManager.Api.Data;

public sealed class LoginAttemptRepository(Database db)
{
    public void Add(string? username, string? ip, bool success, DateTimeOffset timestamp)
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO LoginAttempts (username, ip, success, timestamp) VALUES ($username, $ip, $success, $ts);";
        cmd.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$success", success ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", Database.FormatTime(timestamp));
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var connection = db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LoginAttempts;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
