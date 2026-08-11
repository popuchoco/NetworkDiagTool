using System.Text.Json;

namespace NetworkDiagTool;

public sealed class AppConfig
{
    private const int MinTimeoutMs = 500;
    private const int MaxTimeoutMs = 60000;
    private const int MinPingCount = 1;
    private const int MaxPingCount = 86400;

    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NetworkDiagLogs");

    public int DefaultTimeoutMs { get; set; } = 3000;

    public int DefaultPingCount { get; set; } = 4;

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string TemplatePath => Path.Combine(AppContext.BaseDirectory, "config.template.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                throw new AppConfigException(
                    "找不到 config.json。\n\n" +
                    "請先將程式同層的 config.template.json 複製一份並命名為 config.json，" +
                    "確認 LogDirectory、DefaultTimeoutMs、DefaultPingCount 設定後再重新啟動程式。\n\n" +
                    $"設定檔路徑：{ConfigPath}\n" +
                    $"範本路徑：{TemplatePath}");
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions())
                ?? throw new AppConfigException($"config.json 內容為空或格式不正確：{ConfigPath}");
            config.Normalize();
            return config;
        }
        catch (AppConfigException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppConfigException(
                "無法讀取 config.json。\n\n" +
                "請確認 config.json 是合法 JSON，並包含 LogDirectory、DefaultTimeoutMs、DefaultPingCount 欄位。\n\n" +
                $"設定檔路徑：{ConfigPath}",
                ex);
        }
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, JsonOptions());
        File.WriteAllText(ConfigPath, json);
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(LogDirectory))
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NetworkDiagLogs");
        }

        DefaultTimeoutMs = Math.Clamp(DefaultTimeoutMs, MinTimeoutMs, MaxTimeoutMs);
        DefaultPingCount = Math.Clamp(DefaultPingCount, MinPingCount, MaxPingCount);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
    }
}

public sealed class AppConfigException : Exception
{
    public AppConfigException(string message)
        : base(message)
    {
    }

    public AppConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
