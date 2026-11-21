using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FileManager.Api.Data;

/// <summary>
/// Доступ к SQLite. Схема создаётся при старте, все запросы параметризованы.
/// </summary>
public sealed class Database
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Users (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            failed_logins INTEGER NOT NULL DEFAULT 0,
            locked_until  TEXT
        );

        CREATE TABLE IF NOT EXISTS Files (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            filename   TEXT NOT NULL,
            created_at TEXT NOT NULL,
            size       INTEGER,
            location   TEXT NOT NULL,
            owner_id   INTEGER NOT NULL REFERENCES Users(id),
            is_deleted INTEGER NOT NULL DEFAULT 0
        );

        -- Один активный файл на путь у одного владельца; удалённые записи не мешают.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_files_active
            ON Files(owner_id, location) WHERE is_deleted = 0;

        CREATE TABLE IF NOT EXISTS Operations (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp      TEXT NOT NULL,
            operation_type TEXT NOT NULL CHECK (operation_type IN ('create', 'modify', 'delete')),
            file_id        INTEGER REFERENCES Files(id),
            user_id        INTEGER NOT NULL REFERENCES Users(id),
            details        TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_operations_user ON Operations(user_id);

        CREATE TABLE IF NOT EXISTS AuthTokens (
            token_hash TEXT PRIMARY KEY,
            user_id    INTEGER NOT NULL REFERENCES Users(id),
            expires_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS LoginAttempts (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            username  TEXT,
            ip        TEXT,
            success   INTEGER NOT NULL,
            timestamp TEXT NOT NULL
        );
        """;

    private readonly string _connectionString;

    public Database(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
    }

    /// <summary>Открывает соединение с включёнными внешними ключами и таймаутом блокировки.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Включает WAL и создаёт таблицы, если их нет.</summary>
    public void Initialize()
    {
        using var connection = Open();

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        using var schema = connection.CreateCommand();
        // SchemaSql — константа без интерполяции, значений из пользовательского ввода нет.
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();
    }

    public static string FormatTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
