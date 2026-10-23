using Microsoft.Data.Sqlite;

namespace FileManager.Api.Data;

public enum OperationType { Create, Modify, Delete }

public enum BatchChangeKind { CreateFile, UpdateFile, CreateDirectory }

/// <summary>Одно изменение для пакетной записи в журнал. Заполняются поля, нужные конкретному виду изменения.</summary>
public sealed record BatchChange(
    BatchChangeKind Kind,
    string Name,
    string Location,
    long? Size,
    long? FileId,
    string Details);


public sealed record FileRecord(
    long Id,
    long OwnerId,
    string FileName,
    string Location,
    long? Size,
    DateTimeOffset CreatedAt,
    bool IsDeleted);

public sealed record OperationRecord(
    long Id,
    DateTimeOffset Timestamp,
    string OperationType,
    long? FileId,
    long UserId,
    string? Details);

/// <summary>
/// Метаданные файлов и журнал операций. Каждое изменение файла и запись в Operations
/// выполняются в одной транзакции: либо оба действия, либо ни одного.
/// </summary>
public sealed class FileRepository(Database db)
{
    private const string FileColumns = "id, owner_id, filename, location, size, created_at, is_deleted";

    public long InsertWithOperation(long ownerId, string fileName, string location, long? size,
        long userId, OperationType type, string? details = null)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        long fileId;
        using (var cmd = Command(connection, tx, """
            INSERT INTO Files (filename, created_at, size, location, owner_id)
            VALUES ($filename, $createdAt, $size, $location, $owner)
            RETURNING id;
            """))
        {
            cmd.Parameters.AddWithValue("$filename", fileName);
            cmd.Parameters.AddWithValue("$createdAt", Database.FormatTime(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$location", location);
            cmd.Parameters.AddWithValue("$owner", ownerId);
            fileId = (long)cmd.ExecuteScalar()!;
        }

        InsertOperation(connection, tx, type, fileId, userId, details);
        tx.Commit();
        return fileId;
    }

    /// <summary>Обновляет размер активного файла и пишет операцию modify в одной транзакции.</summary>
    public void UpdateSizeWithOperation(long fileId, long size, long userId, string? details = null)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        int changed;
        using (var cmd = Command(connection, tx, "UPDATE Files SET size = $size WHERE id = $id AND is_deleted = 0;"))
        {
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$id", fileId);
            changed = cmd.ExecuteNonQuery();
        }

        if (changed == 0)
        {
            throw new InvalidOperationException($"Файл {fileId} не найден или уже удалён.");
        }

        InsertOperation(connection, tx, OperationType.Modify, fileId, userId, details);
        tx.Commit();
    }

    public void SoftDeleteWithOperation(long fileId, long userId, string? details = null)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        int changed;
        using (var cmd = Command(connection, tx, "UPDATE Files SET is_deleted = 1 WHERE id = $id AND is_deleted = 0;"))
        {
            cmd.Parameters.AddWithValue("$id", fileId);
            changed = cmd.ExecuteNonQuery();
        }

        if (changed == 0)
        {
            throw new InvalidOperationException($"Файл {fileId} не найден или уже удалён.");
        }

        InsertOperation(connection, tx, OperationType.Delete, fileId, userId, details);
        tx.Commit();
    }

    /// <summary>
    /// Помечает удалёнными все активные файлы внутри каталога (включая сам путь, если это файл) и пишет
    /// операции delete в одной транзакции. Возвращает количество помеченных файлов.
    /// </summary>
    public int SoftDeleteDirectoryWithOperations(long ownerId, long userId, string location, bool ignoreCase, string details)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();
        var collation = ignoreCase ? " COLLATE NOCASE" : string.Empty;
        var prefix = location + "/";

        var ids = new List<long>();
        using (var select = Command(connection, tx,
            $"SELECT id FROM Files WHERE owner_id = $owner AND is_deleted = 0 AND (location{collation} = $location OR substr(location, 1, length($prefix)){collation} = $prefix);"))
        {
            select.Parameters.AddWithValue("$owner", ownerId);
            select.Parameters.AddWithValue("$location", location);
            select.Parameters.AddWithValue("$prefix", prefix);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            using var update = Command(connection, tx, "UPDATE Files SET is_deleted = 1 WHERE id = $id AND is_deleted = 0;");
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            InsertOperation(connection, tx, OperationType.Delete, id, userId, null);
        }

        InsertOperation(connection, tx, OperationType.Delete, null, userId, details);
        tx.Commit();
        return ids.Count;
    }

    /// <summary>Записывает операцию, не связанную с записью Files (например, создание каталога).</summary>
    public void RecordOperation(OperationType type, long userId, string details)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();
        InsertOperation(connection, tx, type, null, userId, details);
        tx.Commit();
    }

    /// <summary>
    /// Пакетно записывает изменения файлов и каталогов в одной транзакции: либо все записи метаданных,
    /// либо ни одной. Используется распаковкой архива, которая уже откатила файлы на диске при ошибке.
    /// </summary>
    public void ApplyBatch(long userId, IReadOnlyList<BatchChange> changes)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case BatchChangeKind.CreateFile:
                {
                    using var insert = Command(connection, tx, """
                        INSERT INTO Files (filename, created_at, size, location, owner_id)
                        VALUES ($filename, $createdAt, $size, $location, $owner)
                        RETURNING id;
                        """);
                    insert.Parameters.AddWithValue("$filename", change.Name);
                    insert.Parameters.AddWithValue("$createdAt", Database.FormatTime(DateTimeOffset.UtcNow));
                    insert.Parameters.AddWithValue("$size", change.Size!.Value);
                    insert.Parameters.AddWithValue("$location", change.Location);
                    insert.Parameters.AddWithValue("$owner", userId);
                    var fileId = (long)insert.ExecuteScalar()!;

                    InsertOperation(connection, tx, OperationType.Create, fileId, userId, change.Details);
                    break;
                }

                case BatchChangeKind.UpdateFile:
                {
                    using var update = Command(connection, tx, "UPDATE Files SET size = $size WHERE id = $id AND is_deleted = 0;");
                    update.Parameters.AddWithValue("$size", change.Size!.Value);
                    update.Parameters.AddWithValue("$id", change.FileId!.Value);
                    if (update.ExecuteNonQuery() == 0)
                    {
                        throw new InvalidOperationException($"Файл {change.FileId} не найден или уже удалён.");
                    }

                    InsertOperation(connection, tx, OperationType.Modify, change.FileId.Value, userId, change.Details);
                    break;
                }

                case BatchChangeKind.CreateDirectory:
                    InsertOperation(connection, tx, OperationType.Create, null, userId, change.Details);
                    break;
            }
        }

        tx.Commit();
    }

    /// <param name="ignoreCase">Для Windows: файловая система не различает регистр, поэтому и поиск в БД тоже.</param>
    public FileRecord? FindActive(long ownerId, string location, bool ignoreCase = false)

    {
        using var connection = db.Open();
        var collation = ignoreCase ? " COLLATE NOCASE" : string.Empty;
        using var cmd = Command(connection, null,
            $"SELECT {FileColumns} FROM Files WHERE owner_id = $owner AND location = $location{collation} AND is_deleted = 0;");
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$location", location);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public IReadOnlyList<OperationRecord> ListOperations(long userId)
    {
        using var connection = db.Open();
        using var cmd = Command(connection, null,
            "SELECT id, timestamp, operation_type, file_id, user_id, details FROM Operations WHERE user_id = $user ORDER BY id;");
        cmd.Parameters.AddWithValue("$user", userId);

        var result = new List<OperationRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new OperationRecord(
                reader.GetInt64(0),
                Database.ParseTime(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    private static void InsertOperation(SqliteConnection connection, SqliteTransaction? tx,
        OperationType type, long? fileId, long userId, string? details)
    {
        using var cmd = Command(connection, tx,
            "INSERT INTO Operations (timestamp, operation_type, file_id, user_id, details) VALUES ($ts, $type, $file, $user, $details);");
        cmd.Parameters.AddWithValue("$ts", Database.FormatTime(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$type", type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$file", (object?)fileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$user", userId);
        cmd.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static FileRecord ReadFile(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
        Database.ParseTime(reader.GetString(5)),
        reader.GetInt64(6) == 1);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }
}
