using FileGroupy.Configuration;
using Microsoft.Data.Sqlite;

namespace FileGroupy.Cache;

/// <summary>确保删除找回事务日志表及其新增列在任何恢复查询前已存在</summary>
public static class RecoverySchemaInitializer
{
    /// <summary>幂等创建恢复表并升级旧数据库的事务日志字段</summary>
    public static void EnsureCreated(ApplicationOptions options)
    {
        using var connection = new SqliteConnection(CacheDatabaseOptions.GetConnectionString(options));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS recovery_snapshots(
                snapshot_id TEXT PRIMARY KEY,
                created_at INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                total_size INTEGER NOT NULL,
                state INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS recovery_files(
                item_id TEXT PRIMARY KEY,
                snapshot_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                original_path TEXT NOT NULL,
                recovery_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                last_modified INTEGER NOT NULL,
                is_restored INTEGER NOT NULL,
                is_moved INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY(snapshot_id) REFERENCES recovery_snapshots(snapshot_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_recovery_files_snapshot ON recovery_files(snapshot_id);
            CREATE TABLE IF NOT EXISTS recovery_snapshot_creations(
                creation_id TEXT PRIMARY KEY,
                snapshot_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                total_size INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recovery_creation_snapshot ON recovery_snapshot_creations(snapshot_id);
            CREATE INDEX IF NOT EXISTS ix_recovery_creation_created_at ON recovery_snapshot_creations(created_at);
            CREATE TABLE IF NOT EXISTS recovery_snapshot_restores(
                restore_id TEXT PRIMARY KEY,
                snapshot_id TEXT NOT NULL,
                restored_at INTEGER NOT NULL,
                requested_file_count INTEGER NOT NULL,
                succeeded_file_count INTEGER NOT NULL,
                restored_size INTEGER NOT NULL,
                failure_count INTEGER NOT NULL,
                restore_all INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recovery_restore_snapshot ON recovery_snapshot_restores(snapshot_id);
            CREATE INDEX IF NOT EXISTS ix_recovery_restore_restored_at ON recovery_snapshot_restores(restored_at);
            CREATE TABLE IF NOT EXISTS recovery_objects(
                object_hash TEXT PRIMARY KEY,
                storage_path TEXT NOT NULL,
                compression INTEGER NOT NULL,
                original_size INTEGER NOT NULL,
                stored_size INTEGER NOT NULL,
                ref_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS recovery_item_chunks(
                item_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                object_hash TEXT NOT NULL,
                original_size INTEGER NOT NULL,
                PRIMARY KEY(item_id, ordinal),
                FOREIGN KEY(object_hash) REFERENCES recovery_objects(object_hash) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_recovery_item_chunks_object ON recovery_item_chunks(object_hash);
            """;
        command.ExecuteNonQuery();
        TryAddColumn(connection, "recovery_snapshots", "state INTEGER NOT NULL DEFAULT 1");
        TryAddColumn(connection, "recovery_files", "is_moved INTEGER NOT NULL DEFAULT 1");
    }

    private static void TryAddColumn(SqliteConnection connection, string tableName, string columnDefinition)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnDefinition};";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 列已存在时 SQLite 返回错误, 可安全忽略。
        }
    }
}