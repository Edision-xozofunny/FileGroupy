namespace FileGroupy.Models;

/// <summary>集中维护本地与便携设备扫描共用的文件扩展名分类规则</summary>
public static class FileCategoryCatalog
{
    /// <summary>按扩展名快速查询文件分类，键比较忽略大小写</summary>
    public static IReadOnlyDictionary<string, FileCategory> ExtensionCategories { get; } = CreateExtensionCategories();

    /// <summary>根据扩展名返回内置分类；未知类型归入其他文件</summary>
    public static FileCategory GetCategory(string extension) =>
        ExtensionCategories.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    /// <summary>返回用于界面展示的文件类型文本；原始扩展名不参与转换</summary>
    public static string GetDisplayName(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return TypeDescriptions.TryGetValue(extension, out var description)
            ? $"{extension} ({description})"
            : extension;
    }

    private static readonly IReadOnlyDictionary<string, string> TypeDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".csx"] = "C# Script", [".vb"] = "Visual Basic", [".fs"] = "F#", [".fsx"] = "F# Script",
        [".java"] = "Java", [".kt"] = "Kotlin", [".kts"] = "Kotlin Script", [".scala"] = "Scala", [".groovy"] = "Groovy",
        [".js"] = "JavaScript", [".mjs"] = "JavaScript Module", [".cjs"] = "CommonJS", [".jsx"] = "React JSX", [".ts"] = "TypeScript", [".tsx"] = "React TSX", [".vue"] = "Vue",
        [".py"] = "Python", [".pyw"] = "Python", [".go"] = "Go", [".rs"] = "Rust", [".c"] = "C", [".h"] = "C Header", [".cpp"] = "C++", [".cxx"] = "C++", [".cc"] = "C++", [".hpp"] = "C++ Header", [".hh"] = "C++ Header",
        [".m"] = "Objective-C", [".mm"] = "Objective-C++", [".swift"] = "Swift", [".php"] = "PHP", [".rb"] = "Ruby", [".rake"] = "Ruby Rake", [".pl"] = "Perl", [".pm"] = "Perl Module", [".r"] = "R", [".lua"] = "Lua", [".dart"] = "Dart",
        [".ex"] = "Elixir", [".exs"] = "Elixir Script", [".erl"] = "Erlang", [".hrl"] = "Erlang Header", [".hs"] = "Haskell", [".clj"] = "Clojure", [".cljs"] = "ClojureScript",
        [".sql"] = "SQL", [".sh"] = "Shell", [".bash"] = "Bash", [".zsh"] = "Zsh", [".ps1"] = "PowerShell", [".psm1"] = "PowerShell Module", [".bat"] = "Batch", [".cmd"] = "Command Script", [".txt"] = "文本文件", [".log"] = "日志文件", [".csv"] = "纯文本表格", [".tsv"] = "纯文本表格",
        [".html"] = "HTML", [".htm"] = "HTML", [".css"] = "CSS", [".scss"] = "Sass SCSS", [".sass"] = "Sass", [".less"] = "Less", [".xml"] = "XML", [".xaml"] = "XAML", [".json"] = "JSON", [".yaml"] = "YAML", [".yml"] = "YAML", [".toml"] = "TOML", [".ini"] = "INI", [".config"] = "Configuration", [".md"] = "Markdown"
    };

    private static IReadOnlyDictionary<string, FileCategory> CreateExtensionCategories()
    {
        var categories = new Dictionary<FileCategory, string[]>
        {
            [FileCategory.Images] = [
                ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tiff", ".tif", ".webp", ".heic", ".heif", ".avif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".raf", ".rw2", ".pef", ".srw", ".svg", ".ico", ".icns"
            ],
            [FileCategory.Audio] = [
                ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".aiff", ".aif", ".alac", ".opus", ".amr", ".ape", ".mid", ".midi"
            ],
            [FileCategory.Video] = [
                ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".webm", ".mts", ".m2ts", ".flv", ".wmv", ".mpeg", ".mpg", ".3gp", ".3g2", ".vob", ".ogv", ".rm", ".rmvb"
            ],
            [FileCategory.Office] = [
                ".doc", ".docx", ".docm", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".xlt", ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pdf", ".rtf", ".odt", ".ods", ".odp", ".epub", ".mobi", ".azw", ".azw3"
            ],
            [FileCategory.Archives] = [
                ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".img", ".wim", ".jar", ".war", ".apk"
            ],
            [FileCategory.SourceCode] = [
                ".cs", ".csx", ".vb", ".fs", ".java", ".kt", ".kts", ".scala", ".groovy", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".py", ".pyw", ".go", ".rs", ".c", ".h", ".cpp", ".cxx", ".cc", ".hpp", ".hh", ".m", ".mm", ".swift", ".php", ".rb", ".rake", ".pl", ".pm", ".r", ".lua", ".dart", ".ex", ".exs", ".erl", ".hrl", ".hs", ".fsx", ".clj", ".cljs", ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".txt", ".log", ".csv", ".tsv", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".xml", ".xaml", ".json", ".yaml", ".yml", ".toml", ".ini", ".config", ".md", ".dockerfile", ".makefile"
            ]
        };

        return categories.SelectMany(pair => pair.Value.Select(extension => new KeyValuePair<string, FileCategory>(extension, pair.Key)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
