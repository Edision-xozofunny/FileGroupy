using System.IO;
using System.Text.Json;

namespace FileGroupy.Services;

/// <summary>将最近使用目录保存到当前 Windows 用户的本地应用数据目录</summary>
public sealed class JsonPathHistoryStore : IPathHistoryStore
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileGroupy",
        "path-history.json");
    private readonly object _syncRoot = new();

    /// <inheritdoc />
    public string? GetLastPath(PathHistoryKind kind)
    {
        lock (_syncRoot)
        {
            var path = Load().Get(kind);
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
    }

    /// <inheritdoc />
    public void SaveLastPath(PathHistoryKind kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        lock (_syncRoot)
        {
            try
            {
                var history = Load().Set(kind, Path.GetFullPath(path));
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history));
            }
            catch (IOException)
            {
                // 记录失败不应影响用户本次文件操作.
            }
            catch (UnauthorizedAccessException)
            {
                // 受限环境中保持选择器和传输功能可用.
            }
        }
    }

    private static PathHistory Load()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<PathHistory>(File.ReadAllText(HistoryPath)) ?? new PathHistory()
                : new PathHistory();
        }
        catch (IOException)
        {
            return new PathHistory();
        }
        catch (JsonException)
        {
            return new PathHistory();
        }
    }

    private sealed record PathHistory(string? Scan = null, string? CopyDestination = null, string? MoveDestination = null, string? RecoveryDestination = null)
    {
        public string? Get(PathHistoryKind kind) => kind switch
        {
            PathHistoryKind.Scan => Scan,
            PathHistoryKind.CopyDestination => CopyDestination,
            PathHistoryKind.MoveDestination => MoveDestination,
            PathHistoryKind.RecoveryDestination => RecoveryDestination,
            _ => null
        };

        public PathHistory Set(PathHistoryKind kind, string path) => kind switch
        {
            PathHistoryKind.Scan => this with { Scan = path },
            PathHistoryKind.CopyDestination => this with { CopyDestination = path },
            PathHistoryKind.MoveDestination => this with { MoveDestination = path },
            PathHistoryKind.RecoveryDestination => this with { RecoveryDestination = path },
            _ => this
        };
    }
}