namespace NetworkDiagTool;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowStartupError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                LogStartupError(exception);
            }
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (AppConfigException ex)
        {
            ShowConfigError(ex);
        }
        catch (Exception ex)
        {
            ShowStartupError(ex);
        }
    }

    private static void ShowConfigError(AppConfigException ex)
    {
        MessageBox.Show(
            ex.Message,
            "缺少或無效的 config.json",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static void ShowStartupError(Exception ex)
    {
        LogStartupError(ex);
        MessageBox.Show(
            $"程式發生錯誤：{ex.Message}\n\n詳細資訊已寫入 startup-error.log",
            "網路診斷工具",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static void LogStartupError(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // Avoid recursive failures while reporting startup errors.
        }
    }
}
