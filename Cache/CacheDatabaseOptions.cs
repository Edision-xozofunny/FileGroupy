using FileGroupy.Configuration;
using Microsoft.Data.Sqlite;
using System.IO;

namespace FileGroupy.Cache;

/// <summary>集中创建扫描缓存数据库的本地路径与连接字符串</summary>
public static class CacheDatabaseOptions
{
    /// <summary>根据配置路径获取 SQLite 连接字符串, 并确保父目录已经存在</summary>
    /// <remarks>数据库路径由 appsettings.json 的 Application:Cache:DatabasePath 控制</remarks>
    public static string GetConnectionString(ApplicationOptions options)
    {
        var databasePath = options.Cache.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }
}