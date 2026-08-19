using System.IO;
using System.Text.Json;
using FileGroupy.Configuration;
using Microsoft.Data.Sqlite;

namespace FileGroupy.Cache;

/// <summary>检测缓存数据库配置路径变化并在启动时迁移旧数据库</summary>
public static class CacheDatabaseMigration
{
    private static readonly string LocationRecordPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileGroupy",
        "database-location.json");

    /// <summary>迁移上次运行的数据库到当前配置路径并记录当前路径</summary>
    public static void MigrateIfPathChanged(ApplicationOptions options)
    {
        var configuredPath = Path.GetFullPath(options.Cache.DatabasePath);
        var previousPath = LoadPreviousPath();
        var sourcePath = previousPath;
        var migrationSucceeded = true;

        // 首次启用路径记录时，仍尝试从默认位置接续已有缓存数据库.
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = GetDefaultDatabasePath();
        }

        if (!string.Equals(sourcePath, configuredPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(sourcePath)
            && !File.Exists(configuredPath))
        {
            try
            {
                BackupDatabase(sourcePath, configuredPath);
            }
            catch (SqliteException)
            {
                // 缓存迁移失败时保留旧库, 当前配置路径仍可创建空缓存继续工作.
                migrationSucceeded = false;
            }
            catch (IOException)
            {
                // 路径不可写时不阻断应用启动.
                migrationSucceeded = false;
            }
            catch (UnauthorizedAccessException)
            {
                // 路径权限不足时不阻断应用启动.
                migrationSucceeded = false;
            }
        }

        if (migrationSucceeded)
        {
            SaveCurrentPath(configuredPath);
        }
    }

    private static void BackupDatabase(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + ".migrating-" + Guid.NewGuid().ToString("N");
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        try
        {
            using (var source = new SqliteConnection(sourceConnectionString))
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? LoadPreviousPath()
    {
        try
        {
            if (!File.Exists(LocationRecordPath))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<DatabaseLocationRecord>(File.ReadAllText(LocationRecordPath));
            return string.IsNullOrWhiteSpace(record?.Path) ? null : Path.GetFullPath(record.Path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void SaveCurrentPath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocationRecordPath)!);
            File.WriteAllText(LocationRecordPath, JsonSerializer.Serialize(new DatabaseLocationRecord(path)));
        }
        catch (IOException)
        {
            // 路径记录失败不影响数据库使用.
        }
        catch (UnauthorizedAccessException)
        {
            // 用户目录不可写时不影响数据库使用.
        }
    }

    private static string GetDefaultDatabasePath()
    {
        var defaultPath = Environment.ExpandEnvironmentVariables(new CacheOptions().DatabasePath);
        return Path.GetFullPath(defaultPath);
    }

    private sealed record DatabaseLocationRecord(string Path);
}