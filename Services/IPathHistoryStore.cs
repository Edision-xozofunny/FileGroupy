namespace FileGroupy.Services;

/// <summary>保存各类本地文件夹选择器最近使用的目录</summary>
public interface IPathHistoryStore
{
    /// <summary>获取仍存在的最近目录, 未记录或已失效时返回空值</summary>
    string? GetLastPath(PathHistoryKind kind);

    /// <summary>记录用户最近确认的目录</summary>
    void SaveLastPath(PathHistoryKind kind, string path);
}

/// <summary>最近目录记录的用途</summary>
public enum PathHistoryKind
{
    Scan,
    CopyDestination,
    MoveDestination,
    RecoveryDestination
}