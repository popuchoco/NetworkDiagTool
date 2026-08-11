using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetworkDiagTool;

public sealed class MainForm : Form
{
    private const int MinTimeoutMs = 500;
    private const int MaxTimeoutMs = 60000;
    private const int MinPingCount = 1;
    private const int MaxPingCount = 86400;
    private const int FullDiagnosticMaxPingCount = 30;

    private sealed record DiagnosticStep(
        string Category,
        string DisplayName,
        string ReportName,
        Func<Action<string>, CancellationToken, Task> Execute);

    private sealed record StepReport(
        string Category,
        string DisplayName,
        string ReportName,
        bool Passed,
        string Severity,
        long ElapsedMs,
        string Detail);

    private sealed record NetworkSnapshot(
        string AdapterName,
        string Ipv4,
        string Gateway,
        string Dns,
        string Proxy);

    private sealed record ArpCorrelation(
        string Status,
        string Detail,
        string MacAddress,
        bool IsApplicable,
        bool TargetFound);

    private enum StatusKind
    {
        Ready,
        Running,
        Success,
        Warning,
        Canceled,
        Error
    }

    private readonly AppConfig _config;
    private readonly DiagnosticService _diagnosticService = new();
    private readonly LogService _logService;

    private TextBox _hostTextBox = null!;
    private NumericUpDown _portInput = null!;
    private NumericUpDown _timeoutInput = null!;
    private NumericUpDown _pingCountInput = null!;
    private TextBox _consoleTextBox = null!;
    private Label _statusLabel = null!;
    private Button _cancelButton = null!;
    private Button _rerunButton = null!;
    private CancellationTokenSource? _cancellation;
    private List<DiagnosticStep> _unfinishedSteps = [];

    public MainForm()
    {
        _config = AppConfig.Load();
        _logService = new LogService(_config.LogDirectory);
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(_logService.DirectoryWarning))
        {
            SetStatus(_logService.DirectoryWarning, StatusKind.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cancellation?.Cancel();
        base.OnFormClosing(e);
    }

    private void InitializeComponent()
    {
        Text = "網路診斷工具";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 860);
        Size = GetPreferredStartupSize();
        Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18, 14, 18, 14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            Text = "網路診斷工具",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(titleLabel, 0, 0);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(0, 6, 0, 6)
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _hostTextBox = new TextBox { Dock = DockStyle.Fill, Text = "8.8.8.8", Margin = new Padding(4, 7, 14, 4) };
        _portInput = CreateNumberInput(1, 65535, 443);
        _timeoutInput = CreateNumberInput(MinTimeoutMs, MaxTimeoutMs, _config.DefaultTimeoutMs);
        _pingCountInput = CreateNumberInput(MinPingCount, MaxPingCount, _config.DefaultPingCount);

        settings.Controls.Add(CreateLabel("IP/Host"), 0, 0);
        settings.Controls.Add(_hostTextBox, 1, 0);
        settings.Controls.Add(CreateLabel("Port"), 2, 0);
        settings.Controls.Add(_portInput, 3, 0);
        settings.Controls.Add(CreateLabel("Timeout"), 4, 0);
        settings.Controls.Add(_timeoutInput, 5, 0);
        settings.Controls.Add(CreateLabel("Ping 次數"), 6, 0);
        settings.Controls.Add(_pingCountInput, 7, 0);

        var contentArea = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };

        var dashboardScroll = new Panel
        {
            Dock = DockStyle.Left,
            AutoScroll = true,
            Padding = new Padding(0, 0, 16, 0),
            Width = 312,
            MinimumSize = new Size(280, 0)
        };

        var dashboardSplitter = new Splitter
        {
            Dock = DockStyle.Left,
            Width = 8,
            MinSize = 280,
            MinExtra = 420,
            BackColor = Color.FromArgb(225, 225, 225)
        };

        var dashboard = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0)
        };

        var diagnosticGroup = CreateDashboardGroup("連線診斷", out var diagnosticButtons);
        var systemGroup = CreateDashboardGroup("系統資訊", out var systemButtons);
        var utilityGroup = CreateDashboardGroup("操作", out var utilityButtons);

        diagnosticButtons.Controls.Add(CreateDashboardButton("Ping", async (_, _) => await RunAsync("ping", "Ping", (log, token) => _diagnosticService.RunPingAsync(Host, PingCount, TimeoutMs, log, token))));
        diagnosticButtons.Controls.Add(CreateDashboardButton("TNC / TCP", async (_, _) => await RunAsync("tnc_tcp", "TNC / TCP", (log, token) => _diagnosticService.RunTcpConnectAsync(Host, Port, TimeoutMs, log, token))));
        diagnosticButtons.Controls.Add(CreateDashboardButton("tracert", async (_, _) => await RunAsync("tracert", "tracert", (log, token) => _diagnosticService.RunTracertAsync(Host, log, token))));
        diagnosticButtons.Controls.Add(CreateDashboardButton("完整診斷", async (_, _) => await RunFullAsync()));

        systemButtons.Controls.Add(CreateDashboardButton("ipconfig", async (_, _) => await RunAsync("ipconfig", "ipconfig", (log, token) => _diagnosticService.RunIpConfigAsync(log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("netstat", async (_, _) => await RunAsync("netstat", "netstat", (log, token) => _diagnosticService.RunNetstatAsync(log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("路由表", async (_, _) => await RunAsync("route_table", "路由表", (log, token) => _diagnosticService.RunRouteTableAsync(log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("網卡狀態", async (_, _) => await RunAsync("get_netadapter", "網卡狀態", (log, token) => _diagnosticService.RunGetNetAdapterAsync(log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("DNS 解析", async (_, _) => await RunAsync("dns_resolve", "DNS Resolve", (log, token) => _diagnosticService.RunDnsResolveAsync(Host, log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("地址解析", async (_, _) => await RunAsync("arp_table", "地址解析", (log, token) => _diagnosticService.RunArpTableAsync(log, token))));
        systemButtons.Controls.Add(CreateDashboardButton("Proxy 偵測", async (_, _) => await RunAsync("proxy_detect", "Proxy 偵測", (log, token) => _diagnosticService.RunWinHttpProxyAsync(log, token))));

        utilityButtons.Controls.Add(CreateDashboardButton("開啟 Log 資料夾", (_, _) => OpenLogDirectory()));
        utilityButtons.Controls.Add(CreateDashboardButton("清除 Console", (_, _) => _consoleTextBox.Clear()));

        _rerunButton = CreateDashboardButton("重跑未完成", async (_, _) => await RerunUnfinishedAsync());
        _rerunButton.Enabled = false;
        utilityButtons.Controls.Add(_rerunButton);

        _cancelButton = CreateDashboardButton("取消執行", (_, _) => RequestCancel());
        _cancelButton.Enabled = false;
        utilityButtons.Controls.Add(_cancelButton);

        _consoleTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            ReadOnly = true,
            BackColor = Color.FromArgb(24, 26, 27),
            ForeColor = Color.FromArgb(235, 235, 235),
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point),
            WordWrap = false
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            ForeColor = Color.FromArgb(70, 70, 70),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "就緒。",
            BackColor = Color.FromArgb(236, 239, 243)
        };

        var consolePanel = new Panel { Dock = DockStyle.Fill };
        consolePanel.Controls.Add(_consoleTextBox);
        consolePanel.Controls.Add(_statusLabel);

        dashboard.Controls.Add(diagnosticGroup);
        dashboard.Controls.Add(systemGroup);
        dashboard.Controls.Add(utilityGroup);
        dashboardScroll.Controls.Add(dashboard);
        contentArea.Controls.Add(consolePanel);
        contentArea.Controls.Add(dashboardSplitter);
        contentArea.Controls.Add(dashboardScroll);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(settings, 0, 1);
        root.Controls.Add(contentArea, 0, 2);
        Controls.Add(root);
    }

    private static Size GetPreferredStartupSize()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1260, 900);
        var width = Math.Min(1320, Math.Max(1080, workingArea.Width - 32));
        var height = Math.Min(1080, Math.Max(860, workingArea.Height - 32));
        return new Size(width, height);
    }

    private string Host => _hostTextBox.Text.Trim();
    private int Port => (int)_portInput.Value;
    private int TimeoutMs => ParseNumberInputText(_timeoutInput, MinTimeoutMs, MaxTimeoutMs, _config.DefaultTimeoutMs);
    private int PingCount => ParseNumberInputText(_pingCountInput, MinPingCount, MaxPingCount, _config.DefaultPingCount);
    private int FullDiagnosticPingCount => Math.Min(PingCount, FullDiagnosticMaxPingCount);

    private static Label CreateLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 7, 6, 4)
        };
    }

    private static NumericUpDown CreateNumberInput(int min, int max, int value)
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Margin = new Padding(4, 7, 14, 4)
        };
    }

    private static GroupBox CreateDashboardGroup(string title, out FlowLayoutPanel buttons)
    {
        buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(14, 12, 14, 12),
            Width = 268
        };

        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            Padding = new Padding(10, 12, 10, 10),
            Margin = new Padding(0, 0, 0, 12),
            Width = 280,
            MinimumSize = new Size(280, 0)
        };
        group.Controls.Add(buttons);
        return group;
    }

    private static Button CreateDashboardButton(string text, EventHandler onClick)
    {
        var button = CreateButton(text, onClick);
        button.AutoSize = false;
        button.Width = 248;
        button.Height = 34;
        button.Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Margin = new Padding(0, 0, 0, 6);
        button.Padding = new Padding(14, 4, 10, 4);
        return button;
    }

    private static Button CreateButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 4, 10, 4)
        };
        button.Click += onClick;
        return button;
    }

    private async Task RunFullAsync()
    {
        await RunStepsAsync("full_diagnostic", CreateFullDiagnosticSteps());
    }

    private Task RunAsync(string category, string displayName, Func<Action<string>, CancellationToken, Task> action)
    {
        return RunStepsAsync(category, [new DiagnosticStep(category, displayName, displayName, action)]);
    }

    private async Task RerunUnfinishedAsync()
    {
        if (_unfinishedSteps.Count == 0)
        {
            SetStatus("沒有未完成的項目。", StatusKind.Ready);
            return;
        }

        await RunStepsAsync("rerun_unfinished", _unfinishedSteps.ToList(), clearUnfinishedOnStart: false);
    }

    private async Task RunStepsAsync(string category, IReadOnlyList<DiagnosticStep> steps, bool clearUnfinishedOnStart = true)
    {
        if (!ValidateTargetInputs())
        {
            return;
        }

        if (steps.Count == 0)
        {
            SetStatus("沒有可執行的項目。", StatusKind.Ready);
            return;
        }

        if (clearUnfinishedOnStart)
        {
            ClearUnfinishedSteps();
        }

        SetBusy(true);
        var logPath = _logService.CreateLogFilePath(category);
        _cancellation = new CancellationTokenSource();
        var activeStepIndex = 0;
        var runLines = new List<string>();
        var reports = new List<StepReport>();
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            WriteLine($"========== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 開始診斷: {category} ==========");
            WriteLine($"Log 檔案: {logPath}");
            if (!string.IsNullOrWhiteSpace(_logService.DirectoryWarning))
            {
                WriteLine("[Log Warning] " + _logService.DirectoryWarning);
                SetStatus(_logService.DirectoryWarning, StatusKind.Warning);
            }
            WriteLine($"目標: {Host}:{Port}");
            WriteSeparator(WriteLine);

            for (activeStepIndex = 0; activeStepIndex < steps.Count; activeStepIndex++)
            {
                var step = steps[activeStepIndex];
                var stepLines = new List<string>();
                Action<string> stepWriteLine = message =>
                {
                    stepLines.Add(message);
                    WriteLine(message);
                };
                var stepStopwatch = Stopwatch.StartNew();
                WriteLine($"[系統] 執行項目: {step.DisplayName}");
                await step.Execute(stepWriteLine, _cancellation.Token);
                stepStopwatch.Stop();

                var report = AnalyzeStepResult(step, stepLines, stepStopwatch.ElapsedMilliseconds, TimeoutMs);
                reports.Add(report);
                WriteStepReport(report, WriteLine);

                if (activeStepIndex < steps.Count - 1)
                {
                    WriteSeparator(WriteLine);
                }
            }

            WriteSeparator(WriteLine);
            if (category == "full_diagnostic")
            {
                WriteDiagnosticSummary(Host, Port, runLines, reports, totalStopwatch.ElapsedMilliseconds, WriteLine);
                WriteSeparator(WriteLine);
            }
            else if (reports.Count == 1)
            {
                WriteSingleDiagnosticConclusion(reports[0], WriteLine);
                WriteSeparator(WriteLine);
            }

            WriteLine($"========== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 診斷完成 ==========");
            ClearUnfinishedSteps();
            SetStatus(
                string.IsNullOrWhiteSpace(_logService.DirectoryWarning)
                    ? "完成。Log 已保存。"
                    : $"完成。Log 已保存，但 {_logService.DirectoryWarning}",
                string.IsNullOrWhiteSpace(_logService.DirectoryWarning) ? StatusKind.Success : StatusKind.Warning);
        }
        catch (OperationCanceledException)
        {
            _unfinishedSteps = steps.Skip(activeStepIndex).ToList();
            _rerunButton.Enabled = _unfinishedSteps.Count > 0;
            WriteLine("[系統] 使用者取消診斷。");
            if (_unfinishedSteps.Count > 0)
            {
                WriteLine("[系統] 可重跑未完成項目: " + string.Join("、", _unfinishedSteps.Select(step => step.DisplayName)));
            }
            SetStatus(_unfinishedSteps.Count > 0 ? $"已取消。尚有 {_unfinishedSteps.Count} 個項目可重跑。" : "已取消。", StatusKind.Canceled);
        }
        catch (Exception ex)
        {
            WriteLine("[系統] 發生錯誤: " + ex);
            SetStatus("發生錯誤，請查看 Console Log。", StatusKind.Error);
        }
        finally
        {
            totalStopwatch.Stop();
            SetBusy(false);
            var cancellation = _cancellation;
            _cancellation = null;
            cancellation?.Dispose();
        }

        void WriteLine(string message)
        {
            runLines.Add(message);
            AppendConsole(message);
            _logService.Append(logPath, message);
        }

    }

    private bool ValidateTargetInputs()
    {
        var host = Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("請輸入 IP 或 Host。", "缺少目標", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _hostTextBox.Focus();
            return false;
        }

        if (!TryValidateHost(host, out var hostError))
        {
            MessageBox.Show(hostError, "IP/Host 格式錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _hostTextBox.Focus();
            _hostTextBox.SelectAll();
            return false;
        }

        if (!TryValidatePort(out var portError))
        {
            MessageBox.Show(portError, "Port 格式錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _portInput.Focus();
            _portInput.Select(0, _portInput.Text.Length);
            return false;
        }

        if (!TryValidateTimeout(out var timeoutError))
        {
            MessageBox.Show(timeoutError, "Timeout 驗證錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _timeoutInput.Focus();
            _timeoutInput.Select(0, _timeoutInput.Text.Length);
            return false;
        }

        if (!TryValidatePingCount(out var pingCountError))
        {
            MessageBox.Show(pingCountError, "Ping 次數驗證錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pingCountInput.Focus();
            _pingCountInput.Select(0, _pingCountInput.Text.Length);
            return false;
        }

        return true;
    }

    private static bool TryValidateHost(string host, out string error)
    {
        error = string.Empty;

        if (host.Any(char.IsWhiteSpace))
        {
            error = "IP/Host 不可包含空白字元。";
            return false;
        }

        if (Regex.IsMatch(host, @"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase))
        {
            error = "IP/Host 請只填主機名稱或 IP，不要包含 http://、https://。";
            return false;
        }

        if (host.Contains('/') || host.Contains('\\') || host.Contains('?') || host.Contains('#') || host.Contains('@'))
        {
            error = "IP/Host 請勿包含路徑、參數或帳號資訊，請只填 IP 或 Host。";
            return false;
        }

        if (Regex.IsMatch(host, @"^\[[^\]]+\]:\d+$") || Regex.IsMatch(host, @"^[^:]+:\d+$"))
        {
            error = "偵測到 IP/Host 內含 Port，請把 Port 填到 Port 欄位。";
            return false;
        }

        if (host.Contains(':'))
        {
            if (IPAddress.TryParse(host, out var ipv6Address) && ipv6Address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return true;
            }

            error = "IPv6 格式不正確；若是 IP:Port，請把 Port 填到 Port 欄位。";
            return false;
        }

        if (Regex.IsMatch(host, @"^[0-9.]+$") || Regex.IsMatch(host, @"^\d+$"))
        {
            return TryValidateStrictIpv4(host, out error);
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host.Length > 253)
        {
            error = "Host 名稱過長，請確認輸入內容。";
            return false;
        }

        var labels = host.Split('.');
        if (labels.Any(label => label.Length == 0 || label.Length > 63))
        {
            error = "Host 名稱格式不正確，請確認網域名稱分段。";
            return false;
        }

        if (!labels.All(label => Regex.IsMatch(label, @"^[A-Za-z0-9_](?:[A-Za-z0-9_-]*[A-Za-z0-9_])?$")))
        {
            error = "Host 名稱只能包含英數字、底線、連字號與點，且分段不可用連字號開頭或結尾。";
            return false;
        }

        return true;
    }

    private static bool TryValidateStrictIpv4(string host, out string error)
    {
        error = string.Empty;
        var parts = host.Split('.');
        if (parts.Length != 4)
        {
            error = "IPv4 格式不正確，必須是四段數字，例如 192.168.1.10。";
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length > 1 && part.StartsWith('0'))
            {
                error = "IPv4 每段不可使用前導 0，請改用 192.168.1.1 這類明確格式。";
                return false;
            }

            if (part.Length == 0 || !int.TryParse(part, out var value) || value is < 0 or > 255)
            {
                error = "IPv4 格式不正確，請確認每段數字是否介於 0 到 255。";
                return false;
            }
        }

        return true;
    }

    private bool TryValidatePort(out string error)
    {
        error = string.Empty;
        var text = _portInput.Text.Trim();
        if (text.Length == 0)
        {
            _portInput.Value = 80;
            _portInput.Text = "80";
            return true;
        }

        if (!int.TryParse(text, out var port))
        {
            error = "Port 必須是數字；若留空會自動視為 80。";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "Port 必須介於 1 到 65535；若留空會自動視為 80。";
            return false;
        }

        _portInput.Value = port;
        return true;
    }

    private bool TryValidateTimeout(out string error)
    {
        error = string.Empty;
        var text = _timeoutInput.Text.Trim();
        if (text.Length == 0)
        {
            error = $"Timeout 不能空白，請輸入 {MinTimeoutMs} 到 {MaxTimeoutMs} ms。";
            return false;
        }

        if (!int.TryParse(text, out var timeoutMs))
        {
            error = "Timeout 必須是數字，單位為 ms。";
            return false;
        }

        if (timeoutMs is < MinTimeoutMs or > MaxTimeoutMs)
        {
            error = $"Timeout 必須介於 {MinTimeoutMs} 到 {MaxTimeoutMs} ms。";
            return false;
        }

        _timeoutInput.Value = timeoutMs;
        _timeoutInput.Text = timeoutMs.ToString();
        return true;
    }

    private bool TryValidatePingCount(out string error)
    {
        error = string.Empty;
        var text = _pingCountInput.Text.Trim();
        if (text.Length == 0)
        {
            error = $"Ping 次數不能空白，請輸入 {MinPingCount} 到 {MaxPingCount}。";
            return false;
        }

        if (!int.TryParse(text, out var pingCount))
        {
            error = "Ping 次數必須是數字。";
            return false;
        }

        if (pingCount is < MinPingCount or > MaxPingCount)
        {
            error = $"Ping 次數必須介於 {MinPingCount} 到 {MaxPingCount}。";
            return false;
        }

        _pingCountInput.Value = pingCount;
        _pingCountInput.Text = pingCount.ToString();
        return true;
    }

    private static int ParseNumberInputText(NumericUpDown input, int min, int max, int fallback)
    {
        return int.TryParse(input.Text.Trim(), out var value)
            ? Math.Clamp(value, min, max)
            : Math.Clamp(fallback, min, max);
    }

    private List<DiagnosticStep> CreateFullDiagnosticSteps()
    {
        return
        [
            new("ping", $"Ping (完整診斷最多 {FullDiagnosticMaxPingCount} 次)", "Ping", (log, token) => _diagnosticService.RunPingAsync(Host, FullDiagnosticPingCount, TimeoutMs, log, token)),
            new("dns_resolve", "DNS Resolve", "DNS", (log, token) => _diagnosticService.RunDnsResolveAsync(Host, log, token)),
            new("tnc_tcp", "TNC / TCP", $"TCP {Port}", (log, token) => _diagnosticService.RunTcpConnectAsync(Host, Port, TimeoutMs, log, token)),
            new("gateway_ping", "自動 Gateway Ping", "Gateway", (log, token) => _diagnosticService.RunGatewayPingAsync(TimeoutMs, log, token)),
            new("proxy_detect", "Proxy 偵測", "Proxy", (log, token) => _diagnosticService.RunWinHttpProxyAsync(log, token)),
            new("arp_table", "地址解析", "ARP", (log, token) => _diagnosticService.RunArpTableAsync(log, token)),
            new("ipconfig", "ipconfig", "ipconfig", (log, token) => _diagnosticService.RunIpConfigAsync(log, token)),
            new("netstat", "netstat", "netstat", (log, token) => _diagnosticService.RunNetstatAsync(log, token)),
            new("get_netadapter", "網卡狀態", "Adapter", (log, token) => _diagnosticService.RunGetNetAdapterAsync(log, token)),
            new("route_trace", "路由追蹤分析", "Route", (log, token) => _diagnosticService.RunRouteTraceCorrelationAsync(Host, log, token))
        ];
    }

    private static StepReport AnalyzeStepResult(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs, int timeoutMs)
    {
        return step.Category switch
        {
            "ping" => AnalyzePing(step, lines, elapsedMs, timeoutMs),
            "dns_resolve" => AnalyzeDnsResolve(step, lines, elapsedMs),
            "tnc_tcp" => AnalyzeTcp(step, lines, elapsedMs),
            "gateway_ping" => AnalyzeGateway(step, lines, elapsedMs),
            "proxy_detect" => AnalyzeCommandLike(step, lines, elapsedMs, "Low", "Proxy command completed."),
            "arp_table" => AnalyzeCommandLike(step, lines, elapsedMs, "Low", "ARP table available."),
            "ipconfig" => AnalyzeCommandLike(step, lines, elapsedMs, "Low", "ipconfig completed."),
            "netstat" => AnalyzeCommandLike(step, lines, elapsedMs, "Low", "netstat completed."),
            "route_table" => AnalyzeCommandLike(step, lines, elapsedMs, "High", "Route table available."),
            "get_netadapter" => AnalyzeCommandLike(step, lines, elapsedMs, "Medium", "Adapter status available."),
            "route_trace" => AnalyzeRouteTrace(step, lines, elapsedMs),
            "tracert" => AnalyzeRouteTrace(step, lines, elapsedMs),
            _ => AnalyzeCommandLike(step, lines, elapsedMs, "Medium", "Command completed.")
        };
    }

    private static StepReport AnalyzePing(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs, int timeoutMs)
    {
        var match = lines.Select(line => Regex.Match(line, @"^\[PING\]\s+完成:\s+成功\s+(?<ok>\d+),\s+失敗\s+(?<fail>\d+)", RegexOptions.IgnoreCase))
            .FirstOrDefault(match => match.Success);
        if (match == null)
        {
            return Fail(step, elapsedMs, "Medium", $"Ping did not complete; Timeout={timeoutMs} ms.");
        }

        var success = int.Parse(match.Groups["ok"].Value);
        var failures = int.Parse(match.Groups["fail"].Value);
        var total = success + failures;
        var timeoutFailures = lines.Count(line =>
            Regex.IsMatch(line, @"^\s*#\d+\s+.*?(?:Timed?Out|timed out)", RegexOptions.IgnoreCase));
        var detail = $"{success}/{total}; Timeout={timeoutMs} ms";

        if (failures == 0 && success > 0)
        {
            return Pass(step, elapsedMs, detail);
        }

        var severity = timeoutMs < 1000 && timeoutFailures > 0 && success > 0
            ? "Warning"
            : "Medium";
        var failureDetail = timeoutFailures > 0
            ? $"{detail}; TimeOut={timeoutFailures}"
            : detail;
        return Fail(step, elapsedMs, severity, failureDetail);
    }

    private static StepReport AnalyzeDnsResolve(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs)
    {
        if (lines.Any(ContainsAny("[DNS Resolve]", "失敗")) || lines.Any(ContainsAny("[DNS Resolve]", "FAIL")))
        {
            return Fail(step, elapsedMs, "High", "DNS Resolve failed.");
        }

        return lines.Any(ContainsAny("[DNS Resolve]", "成功")) || lines.Any(ContainsAny("[DNS Resolve]", "PASS"))
            ? Pass(step, elapsedMs, "Resolved")
            : Fail(step, elapsedMs, "High", "DNS Resolve did not complete.");
    }

    private static StepReport AnalyzeTcp(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs)
    {
        return lines.Any(ContainsAny("[TNC/TCP]", "成功"))
            ? Pass(step, elapsedMs, "Connected")
            : Fail(step, elapsedMs, "Critical", "TCP connection failed.");
    }

    private static StepReport AnalyzeGateway(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs)
    {
        if (lines.Any(ContainsAny("[Gateway Ping]", "找不到 IPv4 Gateway")))
        {
            return Fail(step, elapsedMs, "Critical", "No IPv4 gateway found.");
        }

        return HasGatewayPingFailures(lines)
            ? Fail(step, elapsedMs, "Critical", "Gateway Ping failed.")
            : Pass(step, elapsedMs, "Reachable");
    }

    private static StepReport AnalyzeRouteTrace(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs)
    {
        if (HasCommandFailure(lines) || lines.Any(ContainsAny("Destination host unreachable")) || lines.Any(ContainsAny("目的地主機無法連線")))
        {
            return Fail(step, elapsedMs, "High", "Route or tracert returned an error.");
        }

        if (lines.Any(ContainsAny("Request timed out")) || lines.Any(ContainsAny("要求等候逾時")))
        {
            return new StepReport(step.Category, step.DisplayName, step.ReportName, true, "Warning", elapsedMs, "Route completed; some hops timed out.");
        }

        return Pass(step, elapsedMs, "Normal");
    }

    private static StepReport AnalyzeCommandLike(DiagnosticStep step, IReadOnlyList<string> lines, long elapsedMs, string failSeverity, string passDetail)
    {
        return HasCommandFailure(lines)
            ? Fail(step, elapsedMs, failSeverity, "Command returned an error.")
            : Pass(step, elapsedMs, passDetail);
    }

    private static void WriteStepReport(StepReport report, Action<string> writeLine)
    {
        writeLine($"[{(report.Passed ? "PASS" : "FAIL")}] {report.ReportName}");
        writeLine($"Severity : {report.Severity}");
        writeLine($"Elapsed  : {FormatElapsed(report.ElapsedMs)}");
        writeLine($"Detail   : {report.Detail}");
    }

    private static void WriteSingleDiagnosticConclusion(StepReport report, Action<string> writeLine)
    {
        writeLine("========== 單項診斷結論 ==========");
        writeLine($"Result   : {(report.Passed ? "PASS" : "FAIL")}");
        writeLine($"Item     : {report.ReportName}");
        writeLine($"Severity : {report.Severity}");
        writeLine($"Elapsed  : {FormatElapsed(report.ElapsedMs)}");
        writeLine($"Detail   : {report.Detail}");
    }

    private static void WriteDiagnosticSummary(string host, int port, IReadOnlyList<string> lines, IReadOnlyList<StepReport> reports, long totalElapsedMs, Action<string> writeLine)
    {
        var snapshot = GetNetworkSnapshot(lines);
        var arpCorrelation = AnalyzeArpCorrelation(host, reports, lines);
        var issues = BuildIssueList(reports, lines, arpCorrelation);

        writeLine("========== Result ==========");
        foreach (var report in reports)
        {
            writeLine($"[{(report.Passed ? "PASS" : "FAIL")}] {report.ReportName,-18} Severity={report.Severity,-8} Elapsed={FormatElapsed(report.ElapsedMs),8} Detail={report.Detail}");
        }

        writeLine("");
        writeLine("========== Timing ==========");
        foreach (var report in reports)
        {
            writeLine($"{report.ReportName,-14} {report.ElapsedMs,8} ms");
        }
        writeLine($"Total          : {FormatTotalElapsed(totalElapsedMs)}");

        writeLine("");
        WriteNetworkHealth(reports, writeLine);

        writeLine("");
        WriteStructuredSummary(host, port, snapshot, reports, arpCorrelation, writeLine);

        if (issues.Count > 0)
        {
            writeLine("");
            writeLine("Possible Causes");
            for (var i = 0; i < issues.Count; i++)
            {
                writeLine($"  {i + 1}. {issues[i]}");
            }
        }
    }

    private static StepReport Pass(DiagnosticStep step, long elapsedMs, string detail)
    {
        return new StepReport(step.Category, step.DisplayName, step.ReportName, true, "Info", elapsedMs, detail);
    }

    private static StepReport Fail(DiagnosticStep step, long elapsedMs, string severity, string detail)
    {
        return new StepReport(step.Category, step.DisplayName, step.ReportName, false, severity, elapsedMs, detail);
    }

    private static bool HasCommandFailure(IEnumerable<string> lines)
    {
        return lines.Any(line => line.Contains("[ERR]", StringComparison.OrdinalIgnoreCase))
            || lines.Any(line => Regex.IsMatch(line, @"^\[[^\]]+\]\s.*ExitCode=(?!0\b)\d+", RegexOptions.IgnoreCase));
    }

    private static string FormatElapsed(long elapsedMs)
    {
        return $"{elapsedMs} ms";
    }

    private static string FormatTotalElapsed(long elapsedMs)
    {
        return elapsedMs >= 1000
            ? $"{elapsedMs / 1000.0:0.0} sec"
            : $"{elapsedMs} ms";
    }

    private static void WriteNetworkHealth(IReadOnlyList<StepReport> reports, Action<string> writeLine)
    {
        var healthItems = new (string Name, string Category, int Weight)[]
        {
            ("Gateway", "gateway_ping", 20),
            ("DNS", "dns_resolve", 15),
            ("TCP", "tnc_tcp", 20),
            ("Route", "route_trace", 15),
            ("ARP", "arp_table", 10),
            ("Proxy", "proxy_detect", 5),
            ("Adapter", "get_netadapter", 10),
            ("Ping", "ping", 5)
        };

        var score = 100;
        foreach (var item in healthItems)
        {
            var report = reports.FirstOrDefault(report => report.Category == item.Category);
            if (report is { Passed: false })
            {
                score -= item.Weight;
            }
        }

        score = Math.Clamp(score, 0, 100);

        writeLine("==========================");
        writeLine("");
        writeLine("Network Health");
        writeLine("");
        writeLine($"{score} /100");
        writeLine("");
        foreach (var item in healthItems)
        {
            var report = reports.FirstOrDefault(report => report.Category == item.Category);
            writeLine($"{item.Name,-11} {FormatPassFail(report)}");
        }
        writeLine("");
        writeLine("==========================");
    }

    private static void WriteStructuredSummary(
        string host,
        int port,
        NetworkSnapshot snapshot,
        IReadOnlyList<StepReport> reports,
        ArpCorrelation arpCorrelation,
        Action<string> writeLine)
    {
        var pingReport = reports.FirstOrDefault(report => report.Category == "ping");
        var tcpReport = reports.FirstOrDefault(report => report.Category == "tnc_tcp");
        var dnsReport = reports.FirstOrDefault(report => report.Category == "dns_resolve");
        var gatewayReport = reports.FirstOrDefault(report => report.Category == "gateway_ping");
        var proxyReport = reports.FirstOrDefault(report => report.Category == "proxy_detect");
        var routeReport = reports.FirstOrDefault(report => report.Category == "route_trace");

        writeLine("=================================================");
        writeLine("Network Diagnostic Summary");
        writeLine("=================================================");
        writeLine("");
        writeLine($"Target           : {host}:{port}");
        writeLine("");
        writeLine($"Network Adapter  : {snapshot.AdapterName}");
        writeLine($"IPv4             : {snapshot.Ipv4}");
        writeLine($"Gateway          : {snapshot.Gateway,-18} {StatusMark(gatewayReport)}");
        writeLine($"DNS              : {snapshot.Dns,-18} {StatusMark(dnsReport)}");
        writeLine($"Proxy            : {snapshot.Proxy,-18} {StatusMark(proxyReport)}");
        writeLine($"Ping             : {pingReport?.Detail ?? "N/A",-18} {StatusMark(pingReport)}");
        writeLine($"TCP              : {TcpSummary(tcpReport),-18} {StatusMark(tcpReport)}");
        writeLine($"Route            : {RouteSummary(routeReport),-18} {StatusMark(routeReport)}");
        writeLine($"ARP Target       : {arpCorrelation.Status,-18} {arpCorrelation.MacAddress}");
        writeLine($"ARP Detail       : {arpCorrelation.Detail}");
        writeLine("");
        writeLine("Overall Result");
        writeLine("");
        writeLine(reports.All(report => report.Passed) ? "✓ Network appears healthy." : "✗ Network issue detected. Please review failed items and possible causes.");
        writeLine("");
        writeLine("=================================================");
    }

    private static NetworkSnapshot GetNetworkSnapshot(IReadOnlyList<string> lines)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .Select(networkInterface => new
            {
                Adapter = networkInterface,
                Properties = networkInterface.GetIPProperties(),
                Ipv4 = networkInterface.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A",
                Gateway = networkInterface.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A",
                Dns = networkInterface.GetIPProperties().DnsAddresses
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "N/A"
            })
            .FirstOrDefault(item => item.Gateway != "N/A")
            ?? NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .Select(networkInterface => new
                {
                    Adapter = networkInterface,
                    Properties = networkInterface.GetIPProperties(),
                    Ipv4 = networkInterface.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A",
                    Gateway = networkInterface.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A",
                    Dns = networkInterface.GetIPProperties().DnsAddresses
                        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "N/A"
                })
                .FirstOrDefault();

        return new NetworkSnapshot(
            adapter?.Adapter.Name ?? "N/A",
            adapter?.Ipv4 ?? "N/A",
            adapter?.Gateway ?? "N/A",
            adapter?.Dns ?? "N/A",
            DetectProxySummary(lines));
    }

    private static string DetectProxySummary(IEnumerable<string> lines)
    {
        if (lines.Any(ContainsAny("Direct access", "no proxy server")) || lines.Any(ContainsAny("直接存取", "沒有 Proxy")))
        {
            return "None";
        }

        var proxyLine = lines.FirstOrDefault(line => line.Contains("Proxy Server", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Proxy 伺服器", StringComparison.OrdinalIgnoreCase));
        return proxyLine == null ? "Unknown" : "Configured";
    }

    private static List<string> BuildIssueList(IReadOnlyList<StepReport> reports, IReadOnlyList<string> lines, ArpCorrelation arpCorrelation)
    {
        var issues = new List<string>();
        foreach (var report in reports.Where(report => !report.Passed))
        {
            var cause = report.Category switch
            {
                "ping" => "Ping failed: target may block ICMP, route may be unreachable, or network quality may be unstable.",
                "dns_resolve" => "DNS Resolve failed: DNS server, suffix, proxy, or hostname may be incorrect.",
                "tnc_tcp" => "TCP failed: service may be down, port may be closed, firewall/ACL may block the connection, or target IP may be wrong.",
                "gateway_ping" => "Gateway failed: local network, Wi-Fi/VLAN, IP configuration, or gateway response policy may be abnormal.",
                "route_trace" => "Route failed: default route, gateway, upstream path, or target network may be unreachable.",
                "proxy_detect" => "Proxy check failed: netsh may be unavailable, blocked, or returned an unexpected error.",
                "arp_table" => "ARP check failed: local address resolution command returned an error.",
                "get_netadapter" => "Adapter check failed: PowerShell command or adapter query returned an error.",
                _ => $"{report.DisplayName} failed: review command output for details."
            };
            issues.Add($"[{report.Severity}] {cause}");
        }

        if (lines.Any(ContainsAny("Request timed out")) || lines.Any(ContainsAny("要求等候逾時")))
        {
            issues.Add("[Warning] tracert has timeout hops; this can be normal ICMP filtering, but may also indicate packet loss on the path.");
        }

        if (arpCorrelation.IsApplicable)
        {
            var arpIssue = arpCorrelation.Status switch
            {
                "Found" => $"[Info] Ping failed but ARP found target MAC {arpCorrelation.MacAddress}; target likely exists on the local LAN/VLAN and may block ICMP.",
                "Invalid MAC" => $"[Medium] Ping failed and ARP found an invalid MAC ({arpCorrelation.MacAddress}); treat this as no reliable local device binding.",
                _ => "[Medium] Ping failed and ARP did not find the target IP; the IP may be unused on this LAN/VLAN, or the subnet/VLAN configuration may not match."
            };
            issues.Add(arpIssue);
        }

        return issues;
    }

    private static ArpCorrelation AnalyzeArpCorrelation(string host, IReadOnlyList<StepReport> reports, IReadOnlyList<string> lines)
    {
        var pingReport = reports.FirstOrDefault(report => report.Category == "ping");
        if (pingReport is not { Passed: false })
        {
            return new ArpCorrelation("Not required", "Ping passed; ARP fallback analysis was not required.", "-", false, false);
        }

        if (!IPAddress.TryParse(host, out var targetAddress) || targetAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return new ArpCorrelation("Skipped", "Ping failed, but ARP fallback requires an IPv4 literal target. Resolve the host to IPv4 first if local ARP correlation is needed.", "-", false, false);
        }

        if (IPAddress.IsLoopback(targetAddress))
        {
            return new ArpCorrelation("Skipped", "Ping failed, but loopback targets do not require ARP correlation.", "-", false, false);
        }

        if (!IsOnAnyLocalSubnet(targetAddress))
        {
            return new ArpCorrelation("Not local subnet", "Ping failed, but the target is not in the same IPv4 subnet as any active adapter. ARP only proves local LAN/VLAN neighbors, not routed remote targets.", "-", false, false);
        }

        var macAddress = FindArpMacAddress(lines, targetAddress.ToString());
        if (macAddress.Length == 0)
        {
            return new ArpCorrelation("Not found", "Ping failed and arp -a did not list the target IP. The IP may be unused on this LAN/VLAN, or the host may not be in the same subnet/VLAN.", "-", true, false);
        }

        if (IsInvalidMacAddress(macAddress))
        {
            return new ArpCorrelation("Invalid MAC", "Ping failed and arp -a found the IP, but the MAC is all-zero or broadcast, so it is not a valid device binding.", macAddress, true, false);
        }

        return new ArpCorrelation("Found", "Ping failed, but arp -a found a valid MAC for the target IP. The device likely exists on the local LAN/VLAN; ICMP may be disabled or filtered.", macAddress, true, true);
    }

    private static string FindArpMacAddress(IEnumerable<string> lines, string targetIp)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9a-f]{2}(?:[-:][0-9a-f]{2}){5})\s+",
                RegexOptions.IgnoreCase);
            if (match.Success && string.Equals(match.Groups["ip"].Value, targetIp, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["mac"].Value.ToUpperInvariant().Replace('-', ':');
            }
        }

        return string.Empty;
    }

    private static bool IsInvalidMacAddress(string macAddress)
    {
        var normalized = Regex.Replace(macAddress, "[^0-9A-Fa-f]", string.Empty);
        return normalized.Equals("000000000000", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnAnyLocalSubnet(IPAddress targetAddress)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask != null)
            .Any(address => IsInSameSubnet(address.Address, targetAddress, address.IPv4Mask));
    }

    private static bool IsInSameSubnet(IPAddress localAddress, IPAddress targetAddress, IPAddress subnetMask)
    {
        var localBytes = localAddress.GetAddressBytes();
        var targetBytes = targetAddress.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();

        for (var i = 0; i < localBytes.Length; i++)
        {
            if ((localBytes[i] & maskBytes[i]) != (targetBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatPassFail(StepReport? report)
    {
        return report == null ? "N/A" : report.Passed ? "PASS" : "FAIL";
    }

    private static string StatusMark(StepReport? report)
    {
        return report == null ? "-" : report.Passed ? "✓" : "✗";
    }

    private static string TcpSummary(StepReport? report)
    {
        return report == null ? "N/A" : report.Passed ? "Connected" : "Failed";
    }

    private static string RouteSummary(StepReport? report)
    {
        return report == null ? "N/A" : report.Passed ? "Normal" : "Check";
    }

    private static bool HasPingFailures(IEnumerable<string> lines)
    {
        return lines.Any(line =>
        {
            var match = Regex.Match(line, @"^\[PING\]\s+完成:\s+成功\s+\d+,\s+失敗\s+(?<fail>\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups["fail"].Value, out var failures) && failures > 0;
        });
    }

    private static bool HasGatewayPingFailures(IEnumerable<string> lines)
    {
        return lines.Any(line =>
        {
            if (line.Contains("[Gateway Ping]", StringComparison.OrdinalIgnoreCase)
                && line.Contains("例外", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var match = Regex.Match(line, @"^\[Gateway Ping\].*完成:\s+成功\s+\d+,\s+失敗\s+(?<fail>\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups["fail"].Value, out var failures) && failures > 0;
        });
    }

    private static void WriteSummaryNotes(IReadOnlyList<string> notes, Action<string> writeLine)
    {
        if (notes.Count == 0)
        {
            return;
        }

        writeLine("觀察事項:");
        for (var i = 0; i < notes.Count; i++)
        {
            writeLine($"  {i + 1}. {notes[i]}");
        }
    }

    private static Func<string, bool> ContainsAny(params string[] keywords)
    {
        return line => keywords.All(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void ClearUnfinishedSteps()
    {
        _unfinishedSteps.Clear();
        _rerunButton.Enabled = false;
    }

    private static void WriteSeparator(Action<string> log) => log(new string('-', 86));

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(_logService.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logService.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟 Log 資料夾: {ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        SetInteractiveControlsEnabled(!busy);
        _cancelButton.Enabled = busy;
        _rerunButton.Enabled = !busy && _unfinishedSteps.Count > 0;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (busy)
        {
            SetStatus("診斷執行中...", StatusKind.Running);
        }
    }

    private void SetInteractiveControlsEnabled(bool enabled)
    {
        _hostTextBox.Enabled = enabled;
        _portInput.Enabled = enabled;
        _timeoutInput.Enabled = enabled;
        _pingCountInput.Enabled = enabled;

        foreach (var button in EnumerateControls(this).OfType<Button>())
        {
            if (ReferenceEquals(button, _cancelButton))
            {
                continue;
            }

            button.Enabled = enabled;
        }
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in EnumerateControls(control))
            {
                yield return child;
            }
        }
    }

    private void RequestCancel()
    {
        if (_cancellation == null)
        {
            return;
        }

        SetStatus("正在取消執行...", StatusKind.Canceled);
        _cancelButton.Enabled = false;
        _cancellation.Cancel();
    }

    private void SetStatus(string message, StatusKind kind)
    {
        _statusLabel.Text = message;
        (_statusLabel.BackColor, _statusLabel.ForeColor) = kind switch
        {
            StatusKind.Running => (Color.FromArgb(255, 244, 204), Color.FromArgb(103, 73, 0)),
            StatusKind.Success => (Color.FromArgb(219, 244, 226), Color.FromArgb(22, 93, 45)),
            StatusKind.Warning => (Color.FromArgb(255, 244, 204), Color.FromArgb(103, 73, 0)),
            StatusKind.Canceled => (Color.FromArgb(255, 237, 213), Color.FromArgb(154, 79, 0)),
            StatusKind.Error => (Color.FromArgb(255, 224, 224), Color.FromArgb(142, 32, 32)),
            _ => (Color.FromArgb(236, 239, 243), Color.FromArgb(70, 70, 70))
        };
    }

    private void AppendConsole(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendConsole(message));
            return;
        }

        _consoleTextBox.AppendText(message + Environment.NewLine);
    }
}
