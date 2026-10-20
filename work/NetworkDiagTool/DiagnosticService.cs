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
    private const int NslookupTimeoutSeconds = 12;
    private const string ProcessTreeCanceledMessage = "已取消: 已終止該命令的行程樹。";
    private const string SkippedWindowsDefaultIpv6DnsMessageFormat = "[nslookup] 已略過 {0} 台 Windows 預設 IPv6 佔位位址 (fec0:0:0:ffff::1~3)。";
    internal const string OnlyWindowsDefaultIpv6DnsMessage = "[nslookup] 本機只有 Windows 預設 IPv6 佔位 DNS，沒有可查詢的 DNS 伺服器。";
    private static readonly IPAddress WindowsDefaultIpv6DnsPlaceholder1 = IPAddress.Parse("fec0:0:0:ffff::1");
    private static readonly IPAddress WindowsDefaultIpv6DnsPlaceholder2 = IPAddress.Parse("fec0:0:0:ffff::2");
    private static readonly IPAddress WindowsDefaultIpv6DnsPlaceholder3 = IPAddress.Parse("fec0:0:0:ffff::3");
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

    public async Task RunDnsResolveWithFallbackAsync(string host, Action<string> writeLine, CancellationToken cancellationToken)
    {
        var dnsResolveLines = new List<string>();
        await RunDnsResolveAsync(
            host,
            line =>
            {
                dnsResolveLines.Add(line);
                writeLine(line);
            },
            cancellationToken);

        if (!IPAddress.TryParse(host, out _) && HasDnsResolveFailure(dnsResolveLines))
        {
            await RunNslookupAsync(host, writeLine, cancellationToken);
        }
    }

    public async Task RunNslookupAsync(string host, Action<string> writeLine, CancellationToken cancellationToken, string? server = null)
    {
        var rawLines = new List<string>();
        var arguments = string.IsNullOrWhiteSpace(server) ? host : $"{host} {server}";

        void WriteRawLine(string line)
        {
            lock (rawLines)
            {
                rawLines.Add(line);
            }

            writeLine("[nslookup] " + line);
        }

        writeLine($"[nslookup] 執行: nslookup {arguments}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "nslookup",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = NativeCommandEncoding,
            StandardErrorEncoding = NativeCommandEncoding
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            writeLine($"[nslookup] 失敗: {ex.Message}");
            WriteNslookupSummary(rawLines, writeLine, timedOut: false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(NslookupTimeoutSeconds));
        using var registration = timeoutCts.Token.Register(() => KillProcessTree(process));

        var outputTask = ReadStreamAsync(process.StandardOutput, WriteRawLine, CancellationToken.None);
        var errorTask = ReadStreamAsync(process.StandardError, WriteRawLine, CancellationToken.None);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainProcessOutputAsync(outputTask, errorTask);

            if (cancellationToken.IsCancellationRequested)
            {
                writeLine($"[nslookup] {ProcessTreeCanceledMessage}");
                throw;
            }

            timedOut = true;
            writeLine($"[nslookup] 逾時: 超過 {NslookupTimeoutSeconds} 秒未完成，已中止。");
        }
        catch (Exception ex)
        {
            KillProcessTree(process);
            await DrainProcessOutputAsync(outputTask, errorTask);
            writeLine($"[nslookup] 失敗: {ex.Message}");
        }

        WriteNslookupSummary(rawLines, writeLine, timedOut);
    }

    public async Task RunNslookupAllConfiguredServersAsync(string host, Action<string> writeLine, CancellationToken cancellationToken)
    {
        var selection = GetConfiguredDnsServerSelection();
        var servers = selection.Servers;
        if (servers.Count == 0)
        {
            writeLine(selection.SkippedPlaceholderCount > 0
                ? OnlyWindowsDefaultIpv6DnsMessage
                : "[nslookup] 找不到任何已設定的 DNS 伺服器。");
            return;
        }

        if (selection.SkippedPlaceholderCount > 0)
        {
            writeLine(string.Format(
                CultureInfo.InvariantCulture,
                SkippedWindowsDefaultIpv6DnsMessageFormat,
                selection.SkippedPlaceholderCount));
        }

        var successfulAddressSets = new List<HashSet<string>>();
        var resolvedCount = 0;

        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writeLine($"[nslookup] ===== 查詢 DNS 伺服器: {server} =====");

            var serverLines = new List<string>();
            await RunNslookupAsync(
                host,
                line =>
                {
                    serverLines.Add(line);
                    writeLine(line);
                },
                cancellationToken,
                server);

            var addresses = GetNslookupAddresses(serverLines);
            if (addresses.Count > 0)
            {
                resolvedCount++;
                successfulAddressSets.Add(new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase));
            }
        }

        writeLine($"[nslookup] 比對: 共 {servers.Count} 台，{resolvedCount} 台解析成功。");

        if (successfulAddressSets.Count >= 2
            && successfulAddressSets.Skip(1).Any(addresses => !successfulAddressSets[0].SetEquals(addresses)))
        {
            writeLine("[nslookup] 注意: 不同 DNS 伺服器回傳的位址不一致，請確認是否有 split-DNS 或快取不同步。");
        }
    }

    private static List<string> GetConfiguredDnsServers()
    {
        return GetConfiguredDnsServerSelection().Servers;
    }

    private static (List<string> Servers, int SkippedPlaceholderCount) GetConfiguredDnsServerSelection()
    {
        var configuredAddresses = new List<IPAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var address in networkInterface.GetIPProperties().DnsAddresses)
            {
                configuredAddresses.Add(address);
            }
        }

        var filteredServers = FilterConfiguredDnsServers(configuredAddresses, out var skippedPlaceholderCount);
        return (filteredServers, skippedPlaceholderCount);
    }

    private static List<string> FilterConfiguredDnsServers(
        IEnumerable<IPAddress> addresses,
        out int skippedPlaceholderCount)
    {
        var servers = new List<string>();
        var skippedPlaceholders = new List<IPAddress>();

        foreach (var address in addresses)
        {
            if (IsWindowsDefaultIpv6DnsPlaceholder(address))
            {
                if (!skippedPlaceholders.Any(item => item.Equals(address)))
                {
                    skippedPlaceholders.Add(address);
                }

                continue;
            }

            var value = address.ToString();
            if (!servers.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                servers.Add(value);
            }
        }

        skippedPlaceholderCount = skippedPlaceholders.Count;
        return servers;
    }

    private static bool IsWindowsDefaultIpv6DnsPlaceholder(IPAddress address)
    {
        return address.Equals(WindowsDefaultIpv6DnsPlaceholder1)
            || address.Equals(WindowsDefaultIpv6DnsPlaceholder2)
            || address.Equals(WindowsDefaultIpv6DnsPlaceholder3);
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
            writeLine($"[powershell] {ProcessTreeCanceledMessage}");
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
            writeLine($"[{fileName}] {ProcessTreeCanceledMessage}");
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

    private static bool HasDnsResolveFailure(IEnumerable<string> lines)
    {
        return lines.Any(line =>
            line.Contains("[DNS Resolve]", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("失敗", StringComparison.OrdinalIgnoreCase)
                || line.Contains("FAIL", StringComparison.OrdinalIgnoreCase)));
    }

    private static void WriteNslookupSummary(IReadOnlyList<string> lines, Action<string> writeLine, bool timedOut)
    {
        var server = GetNslookupServer(lines);
        writeLine($"[nslookup] 回答的伺服器: {server}");

        var addresses = GetNslookupAddresses(lines);
        var result = timedOut || ContainsNslookupText(lines, "要求逾時", "timed out", "timed-out")
            ? "逾時"
            : ContainsNslookupText(lines, "Non-existent domain", "找不到")
                ? "NXDOMAIN"
                : ContainsNslookupText(lines, "Server failed", "伺服器失敗")
                    ? "伺服器失敗"
                    : addresses.Count > 0
                        ? string.Join(", ", addresses)
                        : "伺服器失敗";

        writeLine($"[nslookup] 查詢結果: {result}");
    }

    private static string GetNslookupServer(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var normalizedLine = TrimNslookupPrefix(line);
            if (TryGetNslookupValue(normalizedLine, out var value, "伺服器:", "Server:") && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "未知";
    }

    private static List<string> GetNslookupAddresses(IEnumerable<string> lines)
    {
        var addresses = new List<string>();
        var answerStarted = false;
        var addressLineStarted = false;

        foreach (var line in lines)
        {
            var normalizedLine = TrimNslookupPrefix(line);
            if (TryGetNslookupValue(normalizedLine, out _, "名稱:", "Name:"))
            {
                answerStarted = true;
                addressLineStarted = false;
                continue;
            }

            if (!answerStarted)
            {
                continue;
            }

            if (TryGetNslookupValue(normalizedLine, out var addressValue, "Addresses:", "Address:"))
            {
                addressLineStarted = true;
                AddNslookupAddresses(addressValue, addresses);
                continue;
            }

            if (addressLineStarted)
            {
                AddNslookupAddresses(normalizedLine, addresses);
            }
        }

        return addresses;
    }

    private static string TrimNslookupPrefix(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("[nslookup]", StringComparison.OrdinalIgnoreCase)
            ? trimmed["[nslookup]".Length..].TrimStart()
            : trimmed;
    }

    private static void AddNslookupAddresses(string value, ICollection<string> addresses)
    {
        foreach (var token in value.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var address = token.Trim();
            if (IPAddress.TryParse(address, out _)
                && !addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                addresses.Add(address);
            }
        }
    }

    private static bool ContainsNslookupText(IEnumerable<string> lines, params string[] keywords)
    {
        return lines.Any(line => keywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryGetNslookupValue(string line, out string value, params string[] labels)
    {
        var trimmed = line.TrimStart();
        foreach (var label in labels)
        {
            if (trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmed[label.Length..].Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
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
