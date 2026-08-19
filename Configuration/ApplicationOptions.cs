namespace FileGroupy.Configuration;

/// <summary>应用启动、缓存与删除恢复库的可配置参数</summary>
public sealed class ApplicationOptions
{
    /// <summary>启动阶段相关参数</summary>
    public StartupOptions Startup { get; init; } = new();
    /// <summary>SQLite 扫描缓存相关参数</summary>
    public CacheOptions Cache { get; init; } = new();
    /// <summary>删除找回恢复库相关参数</summary>
    public RecoveryOptions Recovery { get; init; } = new();
}

/// <summary>启动阶段参数</summary>
public sealed class StartupOptions
{
    /// <summary>是否在主窗口显示前修复 Pending 删除快照和孤儿目录</summary>
    public bool RecoverPendingDeletionSnapshots { get; init; } = true;
}

/// <summary>SQLite 缓存参数</summary>
public sealed class CacheOptions
{
    /// <summary>SQLite 数据库路径, 支持 Windows 环境变量例如 %LocalAppData%</summary>
    public string DatabasePath { get; init; } = "%LocalAppData%\\FileGroupy\\Cache\\scan-cache.db";
}

/// <summary>恢复库参数</summary>
public sealed class RecoveryOptions
{
    /// <summary>保存可恢复删除文件的目录, 支持 Windows 环境变量</summary>
    public string LibraryPath { get; init; } = "%LocalAppData%\\FileGroupy\\Recovery";
}