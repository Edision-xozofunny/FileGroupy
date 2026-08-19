using System.Text.Json;
using System.IO;

namespace FileGroupy.Configuration;

/// <summary>读取并验证本地 JSON 应用配置</summary>
public static class ApplicationOptionsLoader
{
    private const string ConfigurationFileName = "appsettings.json";

    /// <summary>从应用程序目录加载配置, 缺失或损坏时回退到安全默认值</summary>
    public static ApplicationOptions Load()
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
        try
        {
            if (!File.Exists(configurationPath))
            {
                return new ApplicationOptions();
            }

            var json = File.ReadAllText(configurationPath);
            var root = JsonSerializer.Deserialize<ConfigurationRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return Normalize(root?.Application ?? new ApplicationOptions());
        }
        catch (IOException)
        {
            return new ApplicationOptions();
        }
        catch (JsonException)
        {
            return new ApplicationOptions();
        }
    }

    /// <summary>展开环境变量并拒绝空路径, 保证后续服务获得可用配置</summary>
    private static ApplicationOptions Normalize(ApplicationOptions options)
    {
        var defaultOptions = new ApplicationOptions();
        return new ApplicationOptions
        {
            Startup = options.Startup,
            Cache = new CacheOptions
            {
                DatabasePath = NormalizePath(options.Cache.DatabasePath, defaultOptions.Cache.DatabasePath)
            },
            Recovery = new RecoveryOptions
            {
                LibraryPath = NormalizePath(options.Recovery.LibraryPath, defaultOptions.Recovery.LibraryPath)
            }
        };
    }

    private static string NormalizePath(string? configuredPath, string fallbackPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(configuredPath) ? fallbackPath : configuredPath);
        return Path.GetFullPath(expandedPath);
    }

    private sealed class ConfigurationRoot
    {
        public ApplicationOptions? Application { get; init; }
    }
}