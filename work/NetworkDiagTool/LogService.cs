using System.Text;

namespace NetworkDiagTool;

public sealed class LogService
{
    private const string ProjectFolderName = "NetworkDiagTool";

    private readonly object _syncRoot = new();

    public string LogDirectory { get; private set; }
    public string? DirectoryWarning { get; private set; }

    public LogService(string logDirectory)
    {
        LogDirectory = EnsureWritableDirectory(logDirectory, allowFallback: true);
    }

    public void UpdateDirectory(string logDirectory)
    {
        LogDirectory = EnsureWritableDirectory(logDirectory, allowFallback: false);
        DirectoryWarning = null;
    }

    public string CreateLogFilePath(string category)
    {
        var now = DateTime.Now;
        var safeCategory = string.Concat(category.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var hourlyDirectory = Path.Combine(LogDirectory, now.ToString("yyyyMMdd"), now.ToString("HH"));
        EnsureDirectoryCanWrite(hourlyDirectory);
        return Path.Combine(hourlyDirectory, $"{now:yyyyMMdd_HHmmss}_{safeCategory}.log");
    }

    public void Append(string filePath, string message)
    {
        lock (_syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.AppendAllText(filePath, message + Environment.NewLine, Encoding.UTF8);
        }
    }

    private string EnsureWritableDirectory(string logDirectory, bool allowFallback)
    {
        try
        {
            return PrepareProjectDirectory(logDirectory);
        }
        catch (Exception ex) when (allowFallback)
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "logs");
            try
            {
                var fallbackProjectDirectory = PrepareProjectDirectory(fallback);
                DirectoryWarning = $"Config LogDirectory 無法使用，已改用 {fallbackProjectDirectory}。原因: {ex.Message}";
                return fallbackProjectDirectory;
            }
            catch
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var localFallback = Path.Combine(localAppData, "NetworkDiagTool", "logs");
                var localFallbackProjectDirectory = PrepareProjectDirectory(localFallback);
                DirectoryWarning = $"Config LogDirectory 無法使用，已改用 {localFallbackProjectDirectory}。原因: {ex.Message}";
                return localFallbackProjectDirectory;
            }
        }
    }

    private static string PrepareProjectDirectory(string configuredLogDirectory)
    {
        var projectDirectory = Path.Combine(configuredLogDirectory, ProjectFolderName);
        EnsureDirectoryCanWrite(projectDirectory);
        return projectDirectory;
    }

    private static void EnsureDirectoryCanWrite(string directory)
    {
        Directory.CreateDirectory(directory);
        var probePath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, string.Empty, Encoding.UTF8);
        File.Delete(probePath);
    }
}
