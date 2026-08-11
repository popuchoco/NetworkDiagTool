using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NetworkDiagTool;

public sealed class DiagnosticService
{
    private const int GatewayPingCount = 2;
    private const int PingIntervalMs = 1000;
    private static readonly Encoding NativeCommandEncoding = GetNativeCommandEncoding();

    public async Task RunPingAsync(string host, int count, int timeoutMs, Action<string> writeLine, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var success = 0;
        var failures = 0;
        var totalRoundtrip = 0L;

        writeLine($"[PING] 目標: {host}, 次數: {count}, Timeout: {timeoutMs} ms");

        writeLine($"[PING] Interval: {PingIntervalMs} ms");

        for (var i = 1; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pingStopwatch = Stopwatch.StartNew();

            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs);
                if (reply.Status == IPStatus.Success)
                {
                    success++;
                    totalRoundtrip += reply.RoundtripTime;
                    writeLine($"  #{i} 成功: {reply.Address} time={reply.RoundtripTime}ms TTL={reply.Options?.Ttl}");
                }
                else
                {
                    failures++;
                    writeLine($"  #{i} 失敗: {reply.Status}");
                }
            }
            catch (Exception ex)
            {
                failures++;
                writeLine($"  #{i} 例外: {ex.Message}");
            }

            pingStopwatch.Stop();
            var delayMs = PingIntervalMs - (int)pingStopwatch.ElapsedMilliseconds;
            if (i < count && delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        var average = success > 0 ? $"{totalRoundtrip / success}ms" : "N/A";
        writeLine($"[PING] 完成: 成功 {success}, 失敗 {failures}, 平均延遲 {average}");
    }

    public async Task RunTcpConnectAsync(string host, int port, int timeoutMs, Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine($"[TNC/TCP] 目標: {host}:{port}, Timeout: {timeoutMs} ms");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutCts.Token);
            stopwatch.Stop();
            writeLine($"[TNC/TCP] 成功: TCP {host}:{port} 可連線，耗時 {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            writeLine($"[TNC/TCP] 失敗: 連線逾時 ({timeoutMs} ms)");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            writeLine($"[TNC/TCP] 失敗: {ex.Message}，耗時 {stopwatch.ElapsedMilliseconds} ms");
        }
    }

    public async Task RunDnsResolveAsync(string host, Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine($"[DNS Resolve] 目標: {host}");

        if (IPAddress.TryParse(host, out var ipAddress))
        {
            writeLine($"[DNS Resolve] PASS: 目標已是 IP ({ipAddress})，不需 DNS 解析。");
            return;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
            {
                writeLine("[DNS Resolve] FAIL: DNS 未回傳任何 IP。");
                return;
            }

            writeLine("[DNS Resolve] 成功: " + string.Join(", ", addresses.Select(address => address.ToString())));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            writeLine("[DNS Resolve] 失敗: " + ex.Message);
        }
    }

    public Task RunIpConfigAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        return RunCommandAsync("ipconfig", "/all", writeLine, cancellationToken);
    }

    public Task RunNetstatAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        return RunCommandAsync("netstat", "-ano", writeLine, cancellationToken);
    }

    public Task RunArpTableAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine("[地址解析] 執行 arp -a，顯示本機 ARP 快取與 IP/MAC 對應。");
        return RunCommandAsync("arp", "-a", writeLine, cancellationToken);
    }

    public Task RunWinHttpProxyAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine("[Proxy 偵測] 執行 netsh winhttp show proxy，顯示 WinHTTP Proxy 設定。");
        return RunCommandAsync("netsh", "winhttp show proxy", writeLine, cancellationToken, Encoding.UTF8);
    }

    public Task RunRouteTableAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine("[路由表] 執行 route print，顯示 Windows IP routing table / route map。");
        return RunCommandAsync("route", "print", writeLine, cancellationToken);
    }

    public Task RunGetNetAdapterAsync(Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine("[Get-NetAdapter] 顯示網路介面卡狀態、MAC、連線速度與驅動資訊。");
        return RunPowerShellAsync("Get-NetAdapter | Format-Table -AutoSize Name, InterfaceDescription, Status, MacAddress, LinkSpeed, ifIndex", writeLine, cancellationToken);
    }

    public Task RunTracertAsync(string host, Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine($"[tracert] 追蹤到 {host} 的路由路徑。");
        return RunCommandAsync("tracert", host, writeLine, cancellationToken);
    }

    public async Task RunRouteTraceCorrelationAsync(string host, Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine("[路由追蹤分析] route print + tracert 結合檢查。");
        writeLine("[路由追蹤分析] 先列出本機路由表，再追蹤目標路徑，用於比對 Default Gateway、Interface 與 hop 路徑。");
        await RunCommandAsync("route", "print", writeLine, cancellationToken);
        writeLine("[路由追蹤分析] 開始 tracert。");
        await RunCommandAsync("tracert", host, writeLine, cancellationToken);
        writeLine("[路由追蹤分析] 請比對 tracert 第一跳是否接近路由表中的 Default Gateway；若第一跳逾時，可能是 Gateway 或中間節點禁止 ICMP 回應。");
    }

    public async Task RunGatewayPingAsync(int timeoutMs, Action<string> writeLine, CancellationToken cancellationToken)
    {
        var gateways = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new { AdapterName = adapter.Name, Address = address }))
            .GroupBy(item => item.Address.ToString())
            .Select(group => group.First())
            .ToList();

        writeLine("[Gateway Ping] 自動偵測已啟用網卡的 IPv4 Gateway 並進行 Ping。");

        if (gateways.Count == 0)
        {
            writeLine("[Gateway Ping] 找不到 IPv4 Gateway。可能未連線、只使用 IPv6，或目前網卡沒有預設閘道。");
            return;
        }

        using var ping = new Ping();
        foreach (var gateway in gateways)
        {
            writeLine($"[Gateway Ping] 網卡: {gateway.AdapterName}, Gateway: {gateway.Address}");
            var success = 0;
            for (var i = 1; i <= GatewayPingCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(gateway.Address, timeoutMs);
                    if (reply.Status == IPStatus.Success)
                    {
                        success++;
                        writeLine($"  #{i} 成功: {reply.Address} time={reply.RoundtripTime}ms TTL={reply.Options?.Ttl}");
                    }
                    else
                    {
                        writeLine($"  #{i} 失敗: {reply.Status}");
                    }
                }
                catch (Exception ex)
                {
                    writeLine($"  #{i} 例外: {ex.Message}");
                }
            }

            writeLine($"[Gateway Ping] {gateway.Address} 完成: 成功 {success}, 失敗 {GatewayPingCount - success}");
        }
    }

    private static async Task RunPowerShellAsync(string command, Action<string> writeLine, CancellationToken cancellationToken)
    {
        writeLine($"[powershell] 執行: {command}");

        var escaped = command.Replace("\"", "\\\"");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"$OutputEncoding=[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); {escaped}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        using var registration = cancellationToken.Register(() => KillProcessTree(process));

        var outputTask = ReadStreamAsync(process.StandardOutput, writeLine, CancellationToken.None);
        var errorTask = ReadStreamAsync(process.StandardError, line => writeLine("[ERR] " + line), CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainProcessOutputAsync(outputTask, errorTask);
            writeLine("[powershell] Canceled; process tree was terminated.");
            throw;
        }

        writeLine($"[powershell] 完成，ExitCode={process.ExitCode}");
    }

    private static async Task RunCommandAsync(
        string fileName,
        string arguments,
        Action<string> writeLine,
        CancellationToken cancellationToken,
        Encoding? outputEncoding = null)
    {
        writeLine($"[{fileName}] 執行: {fileName} {arguments}");
        var commandEncoding = outputEncoding ?? NativeCommandEncoding;

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = commandEncoding,
            StandardErrorEncoding = commandEncoding
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        using var registration = cancellationToken.Register(() => KillProcessTree(process));

        var outputTask = ReadStreamAsync(process.StandardOutput, writeLine, CancellationToken.None);
        var errorTask = ReadStreamAsync(process.StandardError, line => writeLine("[ERR] " + line), CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainProcessOutputAsync(outputTask, errorTask);
            writeLine($"[{fileName}] Canceled; process tree was terminated.");
            throw;
        }

        writeLine($"[{fileName}] 完成，ExitCode={process.ExitCode}");
    }

    private static async Task DrainProcessOutputAsync(params Task[] streamTasks)
    {
        try
        {
            await Task.WhenAll(streamTasks);
        }
        catch
        {
            // Cancellation closes redirected streams; cleanup failures are not diagnostic failures.
        }
    }

    private static Encoding GetNativeCommandEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Console.OutputEncoding;
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string> writeLine, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            writeLine(line);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited between the cancellation request and kill attempt.
        }
    }
}
