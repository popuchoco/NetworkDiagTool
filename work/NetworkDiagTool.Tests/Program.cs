using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NetworkDiagTool;

var tests = new (string Name, Func<Task> Run)[]
{
    ("IPv4 rejects leading zero", () =>
    {
        AssertHostInvalid("192.168.001.001");
        AssertHostInvalid("010.0.0.1");
        return Task.CompletedTask;
    }),
    ("IPv4 accepts strict dotted quad", () =>
    {
        AssertHostValid("192.168.1.1");
        AssertHostValid("10.0.0.1");
        return Task.CompletedTask;
    }),
    ("Internal host accepts underscore", () =>
    {
        AssertHostValid("SRV_APP01");
        AssertHostValid("SVC_API_01.internal");
        return Task.CompletedTask;
    }),
    ("Host rejects URL and host:port", () =>
    {
        AssertHostInvalid("https://example.local");
        AssertHostInvalid("8.8.8.8:443");
        return Task.CompletedTask;
    }),
    ("Host rejects numeric non-IPv4 literal", () =>
    {
        AssertHostInvalid("12345");
        return Task.CompletedTask;
    }),
    ("Ping timeout count ignores settings line", TestPingTimeoutCountIgnoresSettingsLine),
    ("AnalyzeNslookup succeeds with English address", () =>
    {
        TestAnalyzeNslookupEnglishSuccess();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup detects bilingual NXDOMAIN", () =>
    {
        TestAnalyzeNslookupNxdomain();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup detects bilingual timeout", () =>
    {
        TestAnalyzeNslookupTimeout();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup detects bilingual SERVFAIL", () =>
    {
        TestAnalyzeNslookupServfail();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup ignores non-authoritative answer", () =>
    {
        TestAnalyzeNslookupNonAuthoritativeSuccess();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup accepts consistent multi-server answers", () =>
    {
        TestAnalyzeNslookupMultiServerSuccess();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup treats reordered address sets as consistent", () =>
    {
        TestAnalyzeNslookupMultiServerReorderedAddresses();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup warns on different multi-server answers", () =>
    {
        TestAnalyzeNslookupMultiServerDifferentAnswers();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup fails when all DNS servers fail", () =>
    {
        TestAnalyzeNslookupMultiServerAllFailed();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup fails when no DNS server is configured", () =>
    {
        TestAnalyzeNslookupNoConfiguredServer();
        return Task.CompletedTask;
    }),
    ("DNS filtering removes Windows IPv6 placeholders", () =>
    {
        TestFilterConfiguredDnsServersRemovesPlaceholders();
        return Task.CompletedTask;
    }),
    ("DNS filtering matches expanded IPv6 placeholders", () =>
    {
        TestFilterConfiguredDnsServersMatchesExpandedIpv6();
        return Task.CompletedTask;
    }),
    ("AnalyzeNslookup distinguishes placeholder-only DNS", () =>
    {
        TestAnalyzeNslookupOnlyWindowsIpv6Placeholder();
        return Task.CompletedTask;
    }),
    ("RunNslookup server timeout completes within bound", TestRunNslookupWithServerTimeout),
    ("DNS fallback skips nslookup for IP literal", TestDnsResolveFallbackSkipsNslookupForIpLiteral),
    ("Summary parses DNS responder from existing lines", TestSummaryDnsResponder),
    ("Command failure only matches command exit lines", () =>
    {
        TestCommandFailureOnlyMatchesCommandExitLines();
        return Task.CompletedTask;
    }),
    ("TCP cancellation throws without timeout output", TestTcpCancellationThrowsWithoutTimeoutOutput),
    ("Diagnostic output constants contain no mojibake", () =>
    {
        TestDiagnosticOutputConstantsContainNoMojibake();
        return Task.CompletedTask;
    }),
    ("Canceled command messages use Traditional Chinese", TestCanceledCommandMessagesUseChinese),
    ("KillProcessTree tolerates already exited process", TestKillAlreadyExitedProcess),
    ("Canceled command releases without hanging", TestCanceledCommandCompletesPromptly)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.GetBaseException().Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.GetBaseException()}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failed tests:");
    foreach (var failure in failures)
    {
        Console.WriteLine("- " + failure);
    }

    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"All {tests.Length} tests passed.");

static void AssertHostValid(string host)
{
    if (!TryValidateHost(host, out var error))
    {
        throw new InvalidOperationException($"Expected valid host '{host}', but got: {error}");
    }
}

static void AssertHostInvalid(string host)
{
    if (TryValidateHost(host, out _))
    {
        throw new InvalidOperationException($"Expected invalid host '{host}'.");
    }
}

static bool TryValidateHost(string host, out string error)
{
    var method = typeof(MainForm).GetMethod("TryValidateHost", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainForm), "TryValidateHost");
    var args = new object?[] { host, null };
    var result = (bool)method.Invoke(null, args)!;
    error = (string)(args[1] ?? string.Empty);
    return result;
}

static async Task TestPingTimeoutCountIgnoresSettingsLine()
{
    var stepType = typeof(MainForm).GetNestedType("DiagnosticStep", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(MainForm), "DiagnosticStep");
    var analyzeMethod = typeof(MainForm).GetMethod("AnalyzePing", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainForm), "AnalyzePing");

    var step = Activator.CreateInstance(
        stepType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args:
        [
            "ping",
            "Ping",
            "Ping",
            new Func<Action<string>, CancellationToken, Task>((_, _) => Task.CompletedTask)
        ],
        culture: null)
        ?? throw new InvalidOperationException("Failed to create DiagnosticStep.");

    var generatedLines = new List<string>();
    await new DiagnosticService().RunPingAsync("127.0.0.1", 1, 800, line => generatedLines.Add(line), CancellationToken.None);
    var summaryLine = generatedLines.Last(line => line.StartsWith("[PING]", StringComparison.OrdinalIgnoreCase)
        && Regex.Matches(line, @"\d+").Count >= 2);
    var numberIndex = 0;
    summaryLine = Regex.Replace(summaryLine, @"\d+", match => numberIndex++ switch
    {
        0 => "2",
        1 => "2",
        _ => match.Value
    });

    var lines = new[]
    {
        "[PING] start: 10.0.0.1, Count: 4, Timeout: 800 ms",
        "  #1 ok: 10.0.0.1 time=1ms TTL=64",
        "  #2 failed: TimedOut",
        "  #3 failed: TimedOut",
        summaryLine
    };

    var report = analyzeMethod.Invoke(null, [step, lines, 10L, 800])
        ?? throw new InvalidOperationException("AnalyzePing returned null.");
    var detail = (string)(report.GetType().GetProperty("Detail")?.GetValue(report) ?? string.Empty);

    if (!detail.Contains("TimeOut=2", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Expected TimeOut=2, got: {detail}");
    }

    if (detail.Contains("TimeOut=3", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Settings line was counted as timeout: {detail}");
    }

}

static void TestAnalyzeNslookupEnglishSuccess()
{
    var report = InvokeAnalyzeNslookup(
        "[nslookup] Server: dns.example",
        "[nslookup] Name: example.com",
        "[nslookup] Addresses: 203.0.113.10");

    AssertNslookupPass(report, "Resolved by dns.example");
}

static void TestAnalyzeNslookupNxdomain()
{
    var english = InvokeAnalyzeNslookup(
        "[nslookup] *** dns.example can't find missing.example: Non-existent domain");
    AssertNslookupFailure(english, "NXDOMAIN");

    var chinese = InvokeAnalyzeNslookup(
        "[nslookup] *** dns.example 找不到 missing.example");
    AssertNslookupFailure(chinese, "NXDOMAIN");
}

static void TestAnalyzeNslookupTimeout()
{
    var chinese = InvokeAnalyzeNslookup("[nslookup] *** 對 UnKnown 的要求逾時");
    AssertNslookupFailure(chinese, "DNS server did not respond");

    var english = InvokeAnalyzeNslookup("[nslookup] DNS request timed out.");
    AssertNslookupFailure(english, "DNS server did not respond");

    var hyphenated = InvokeAnalyzeNslookup("[nslookup] Request to dns.example timed-out.");
    AssertNslookupFailure(hyphenated, "DNS server did not respond");

    var cutoff = InvokeAnalyzeNslookup("[nslookup] 逾時: 超過 12 秒未完成，已中止。");
    AssertNslookupFailure(cutoff, "Timed out after 12s");
}

static void TestAnalyzeNslookupServfail()
{
    var english = InvokeAnalyzeNslookup("[nslookup] Server failed.");
    AssertNslookupFailure(english, "SERVFAIL");

    var chinese = InvokeAnalyzeNslookup("[nslookup] 伺服器失敗。");
    AssertNslookupFailure(chinese, "SERVFAIL");
}

static void TestAnalyzeNslookupNonAuthoritativeSuccess()
{
    var report = InvokeAnalyzeNslookup(
        "[nslookup] 伺服器: 8.8.8.8",
        "[nslookup] 未經授權的回答:",
        "[nslookup] Non-authoritative answer:",
        "[nslookup] 名稱: example.com",
        "[nslookup] Addresses: 93.184.216.34");

    AssertNslookupPass(report, "Resolved by 8.8.8.8");
}

static void TestAnalyzeNslookupMultiServerSuccess()
{
    var lines = new List<string>
    {
        "[nslookup] 已略過 3 台 Windows 預設 IPv6 佔位位址 (fec0:0:0:ffff::1~3)。"
    };
    lines.AddRange(BuildMultiNslookupLines(
        "203.0.113.10 203.0.113.11",
        "203.0.113.10 203.0.113.11",
        "203.0.113.10 203.0.113.11"));
    var report = InvokeAnalyzeNslookup(lines.ToArray());

    AssertNslookupPass(report, "3/3 servers resolved");
}

static void TestAnalyzeNslookupMultiServerReorderedAddresses()
{
    var report = InvokeAnalyzeNslookup(BuildMultiNslookupLines(
        "203.0.113.10 203.0.113.11",
        "203.0.113.11 203.0.113.10",
        "203.0.113.10 203.0.113.11"));

    AssertNslookupPass(report, "3/3 servers resolved");
    if (report.Detail.Contains("answers differ", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Reordered but equal address sets were treated as different: {report.Detail}");
    }
}

static void TestAnalyzeNslookupMultiServerDifferentAnswers()
{
    var report = InvokeAnalyzeNslookup(BuildMultiNslookupLines(
        "203.0.113.10 203.0.113.11",
        "203.0.113.10 203.0.113.12",
        "203.0.113.10 203.0.113.11"));

    if (!report.Passed
        || !string.Equals(report.Severity, "Warning", StringComparison.Ordinal)
        || !string.Equals(report.Detail, "3/3 resolved; answers differ", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected multi-server Warning, got Passed={report.Passed}, Severity={report.Severity}, Detail={report.Detail}");
    }
}

static void TestAnalyzeNslookupMultiServerAllFailed()
{
    var report = InvokeAnalyzeNslookup(
        "[nslookup] ===== 查詢 DNS 伺服器: 192.0.2.53 =====",
        "[nslookup] *** 對 192.0.2.53 的要求逾時",
        "[nslookup] ===== 查詢 DNS 伺服器: 1.1.1.1 =====",
        "[nslookup] Server failed.",
        "[nslookup] ===== 查詢 DNS 伺服器: 2001:db8::53 =====",
        "[nslookup] *** 找不到 missing.example",
        "[nslookup] 比對: 共 3 台，0 台解析成功。");

    AssertNslookupFailure(report, "0/3 servers resolved");
}

static void TestAnalyzeNslookupNoConfiguredServer()
{
    var report = InvokeAnalyzeNslookup("[nslookup] 找不到任何已設定的 DNS 伺服器。");
    AssertNslookupFailure(report, "No DNS server configured");
}

static void TestFilterConfiguredDnsServersRemovesPlaceholders()
{
    var result = InvokeFilterConfiguredDnsServers(
        "fec0:0:0:ffff::1",
        "fec0:0:0:ffff::2",
        "fec0:0:0:ffff::3",
        "192.0.2.53",
        "2001:db8::53");

    if (result.FilteredCount != 3
        || result.Servers.Count != 2
        || !result.Servers.Contains("192.0.2.53", StringComparer.Ordinal)
        || !result.Servers.Contains("2001:db8::53", StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expected 2 usable DNS servers and 3 filtered placeholders, got {result.Servers.Count} servers and {result.FilteredCount} filtered.");
    }
}

static void TestFilterConfiguredDnsServersMatchesExpandedIpv6()
{
    var result = InvokeFilterConfiguredDnsServers(
        "fec0:0000:0000:ffff:0000:0000:0000:0001",
        "fec0:0000:0000:ffff:0000:0000:0000:0002",
        "fec0:0000:0000:ffff:0000:0000:0000:0003",
        "192.0.2.53");

    if (result.FilteredCount != 3
        || result.Servers.Count != 1
        || !result.Servers.Contains("192.0.2.53", StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expanded IPv6 placeholders were not all filtered: {result.Servers.Count} servers and {result.FilteredCount} filtered.");
    }
}

static void TestAnalyzeNslookupOnlyWindowsIpv6Placeholder()
{
    var report = InvokeAnalyzeNslookup("[nslookup] 本機只有 Windows 預設 IPv6 佔位 DNS，沒有可查詢的 DNS 伺服器。");
    AssertNslookupFailure(report, "No queryable DNS server configured");
}

static string[] BuildMultiNslookupLines(string firstAddresses, string secondAddresses, string thirdAddresses)
{
    return
    [
        "[nslookup] ===== 查詢 DNS 伺服器: 2001:db8::53 =====",
        "[nslookup] Server: resolver.example.test",
        "[nslookup] Name: www.google.com",
        $"[nslookup] Addresses: {firstAddresses}",
        "[nslookup] ===== 查詢 DNS 伺服器: 192.0.2.53 =====",
        "[nslookup] Server: UnKnown",
        "[nslookup] Name: www.google.com",
        $"[nslookup] Addresses: {secondAddresses}",
        "[nslookup] ===== 查詢 DNS 伺服器: 1.1.1.1 =====",
        "[nslookup] Server: one.one.one.one",
        "[nslookup] Name: www.google.com",
        $"[nslookup] Addresses: {thirdAddresses}",
        "[nslookup] 比對: 共 3 台，3 台解析成功。"
    ];
}

static async Task TestRunNslookupWithServerTimeout()
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(19));
    var lines = new List<string>();
    var stopwatch = Stopwatch.StartNew();

    try
    {
        await new DiagnosticService().RunNslookupAsync(
            "www.google.com",
            line => lines.Add(line),
            cancellation.Token,
            "10.255.255.1");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        throw new TimeoutException("RunNslookupAsync did not return before the 19-second test guard.");
    }
    finally
    {
        stopwatch.Stop();
    }

    if (stopwatch.Elapsed >= TimeSpan.FromSeconds(20))
    {
        throw new TimeoutException($"RunNslookupAsync returned after {stopwatch.Elapsed.TotalSeconds:0.0} seconds.");
    }

    if (!lines.Any(line => line.Contains("nslookup www.google.com 10.255.255.1", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("The configured nslookup server was not passed as a direct argument.");
    }

    if (!lines.Any(line => line.Contains("逾時: 超過 12 秒未完成", StringComparison.Ordinal)
        || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || line.Contains("timed-out", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("The configured unreachable server did not produce a timeout message.");
    }
}

static async Task TestDnsResolveFallbackSkipsNslookupForIpLiteral()
{
    var lines = new List<string>();
    await new DiagnosticService().RunDnsResolveWithFallbackAsync(
        "127.0.0.1",
        line => lines.Add(line),
        CancellationToken.None);

    if (lines.Any(line => line.StartsWith("[nslookup]", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("IP literal DNS fallback unexpectedly ran nslookup.");
    }

    if (!lines.Any(line => line.Contains("[DNS Resolve] PASS", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("IP literal DNS resolve did not report PASS.");
    }
}

static Task TestSummaryDnsResponder()
{
    var withResponder = InvokeWriteDiagnosticSummary(
        true,
        "[nslookup] Server: raw-server.example",
        "[nslookup] 回答的伺服器: X");
    var configuredLine = withResponder.Single(line => line.StartsWith("DNS (設定值)", StringComparison.Ordinal));
    var responderLine = withResponder.Single(line => line.StartsWith("DNS (實際回答)", StringComparison.Ordinal));

    if (!configuredLine.Contains("✓", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"DNS setting line did not contain its status mark: {configuredLine}");
    }

    if (!responderLine.Contains(": X", StringComparison.Ordinal)
        || responderLine.Contains("✓", StringComparison.Ordinal)
        || responderLine.Contains("✗", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"DNS responder line was not parsed or received a status mark: {responderLine}");
    }

    var withoutResponder = InvokeWriteDiagnosticSummary(
        true,
        "[nslookup] Server: raw-server.example");
    var missingResponderLine = withoutResponder.Single(line => line.StartsWith("DNS (實際回答)", StringComparison.Ordinal));
    if (!missingResponderLine.EndsWith(": -", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Missing DNS responder should be shown as '-': {missingResponderLine}");
    }

    return Task.CompletedTask;
}

static List<string> InvokeWriteDiagnosticSummary(bool dnsPassed, params string[] lines)
{
    var stepReportType = typeof(MainForm).GetNestedType("StepReport", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(MainForm), "StepReport");
    var summaryMethod = typeof(MainForm).GetMethod("WriteDiagnosticSummary", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainForm), "WriteDiagnosticSummary");
    var reports = Array.CreateInstance(stepReportType, 1);
    var report = Activator.CreateInstance(
        stepReportType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args:
        [
            "dns_resolve",
            "DNS Resolve",
            "DNS",
            dnsPassed,
            dnsPassed ? "Info" : "High",
            10L,
            dnsPassed ? "Resolved" : "DNS Resolve failed."
        ],
        culture: null)
        ?? throw new InvalidOperationException("Failed to create DNS StepReport.");
    reports.SetValue(report, 0);

    var output = new List<string>();
    summaryMethod.Invoke(null, ["example.com", 443, lines, reports, 10L, new Action<string>(output.Add)]);
    return output;
}

static (bool Passed, string Severity, string Detail) InvokeAnalyzeNslookup(params string[] lines)
{
    var stepType = typeof(MainForm).GetNestedType("DiagnosticStep", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(MainForm), "DiagnosticStep");
    var analyzeMethod = typeof(MainForm).GetMethod("AnalyzeNslookup", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainForm), "AnalyzeNslookup");

    var step = Activator.CreateInstance(
        stepType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args:
        [
            "dns_nslookup",
            "DNS 查詢 (nslookup)",
            "nslookup",
            new Func<Action<string>, CancellationToken, Task>((_, _) => Task.CompletedTask)
        ],
        culture: null)
        ?? throw new InvalidOperationException("Failed to create nslookup DiagnosticStep.");

    var report = analyzeMethod.Invoke(null, [step, lines, 10L])
        ?? throw new InvalidOperationException("AnalyzeNslookup returned null.");
    var reportType = report.GetType();
    return (
        (bool)(reportType.GetProperty("Passed")?.GetValue(report) ?? false),
        (string)(reportType.GetProperty("Severity")?.GetValue(report) ?? string.Empty),
        (string)(reportType.GetProperty("Detail")?.GetValue(report) ?? string.Empty));
}

static (List<string> Servers, int FilteredCount) InvokeFilterConfiguredDnsServers(params string[] addresses)
{
    var filterMethod = typeof(DiagnosticService).GetMethod(
        "FilterConfiguredDnsServers",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(DiagnosticService), "FilterConfiguredDnsServers");
    var arguments = new object?[] { addresses.Select(IPAddress.Parse).ToArray(), 0 };
    var servers = (List<string>)(filterMethod.Invoke(null, arguments)
        ?? throw new InvalidOperationException("FilterConfiguredDnsServers returned null."));
    return (servers, (int)(arguments[1] ?? -1));
}

static void AssertNslookupPass((bool Passed, string Severity, string Detail) report, string expectedDetail)
{
    if (!report.Passed || !string.Equals(report.Severity, "Info", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected nslookup PASS/Info, got Passed={report.Passed}, Severity={report.Severity}, Detail={report.Detail}");
    }

    if (!report.Detail.Contains(expectedDetail, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected detail '{expectedDetail}', got: {report.Detail}");
    }
}

static void AssertNslookupFailure((bool Passed, string Severity, string Detail) report, string expectedDetail)
{
    if (report.Passed || !string.Equals(report.Severity, "High", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected nslookup FAIL/High, got Passed={report.Passed}, Severity={report.Severity}, Detail={report.Detail}");
    }

    if (!string.Equals(report.Detail, expectedDetail, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected detail '{expectedDetail}', got: {report.Detail}");
    }
}

static void TestCommandFailureOnlyMatchesCommandExitLines()
{
    var method = typeof(MainForm).GetMethod("HasCommandFailure", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(MainForm), "HasCommandFailure");

    bool Invoke(params string[] lines) => (bool)method.Invoke(null, [lines])!;

    if (Invoke("[route] 完成，ExitCode=0"))
    {
        throw new InvalidOperationException("ExitCode=0 should not be treated as command failure.");
    }

    if (!Invoke("[route] 完成，ExitCode=1"))
    {
        throw new InvalidOperationException("ExitCode=1 should be treated as command failure.");
    }

    if (!Invoke("[route] 完成，ExitCode=10"))
    {
        throw new InvalidOperationException("ExitCode=10 should be treated as command failure.");
    }

    if (Invoke("工具輸出內文提到 ExitCode=7，但不是命令收尾行"))
    {
        throw new InvalidOperationException("Non-command output line should not be treated as command failure.");
    }
}

static async Task TestTcpCancellationThrowsWithoutTimeoutOutput()
{
    using var cancellation = new CancellationTokenSource();
    var lines = new List<string>();
    var task = new DiagnosticService().RunTcpConnectAsync(
        "203.0.113.1",
        81,
        20000,
        line => lines.Add(line),
        cancellation.Token);

    await Task.Delay(100);
    cancellation.Cancel();

    try
    {
        await task;
        throw new InvalidOperationException("TCP cancellation completed without OperationCanceledException.");
    }
    catch (OperationCanceledException)
    {
        // Expected.
    }

    if (lines.Any(line => line.Contains("連線逾時", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("User cancellation was reported as TCP timeout.");
    }
}

static void TestDiagnosticOutputConstantsContainNoMojibake()
{
    var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NetworkDiagTool", "DiagnosticService.cs"));
    var source = File.ReadAllText(sourcePath, Encoding.UTF8);
    var forbidden = new[] { "憭望?", "????暹?", "嚗?", "?剜??", "蝬脰楝", "閮箸", "銝剜" };
    foreach (var token in forbidden)
    {
        if (source.Contains(token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DiagnosticService.cs contains mojibake token: {token}");
        }
    }

    foreach (var expected in new[]
    {
        "[TNC/TCP] 失敗: 連線逾時 ({timeoutMs} ms)",
        "[TNC/TCP] 失敗: {ex.Message}，耗時 {stopwatch.ElapsedMilliseconds} ms",
        "[DNS Resolve] 失敗: ",
        "[Proxy 偵測] 執行 netsh winhttp show proxy，顯示 WinHTTP Proxy 設定。",
        "[路由表] 執行 route print，顯示 Windows IP routing table / route map。",
        "[nslookup] 本機只有 Windows 預設 IPv6 佔位 DNS，沒有可查詢的 DNS 伺服器。"
    })
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DiagnosticService.cs is missing expected output constant: {expected}");
        }
    }
}

static Task TestCanceledCommandMessagesUseChinese()
{
    var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NetworkDiagTool", "DiagnosticService.cs"));
    var source = File.ReadAllText(sourcePath, Encoding.UTF8);
    const string oldMessage = "Canceled; process tree was terminated.";
    const string newMessage = "已取消: 已終止該命令的行程樹。";

    if (source.Contains(oldMessage, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"DiagnosticService.cs still contains the old English cancellation message: {oldMessage}");
    }

    if (!source.Contains(newMessage, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"DiagnosticService.cs is missing the unified Chinese cancellation message: {newMessage}");
    }

    return Task.CompletedTask;
}

static Task TestKillAlreadyExitedProcess()
{
    // Use a real short-lived OS process to cover Process/OS handle timing, not a mock.
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = "/c exit 0",
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start test process.");

    process.WaitForExit();
    InvokeKillProcessTree(process);
    return Task.CompletedTask;
}

static async Task TestCanceledCommandCompletesPromptly()
{
    var method = typeof(DiagnosticService).GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(DiagnosticService), "RunCommandAsync");

    using var cancellation = new CancellationTokenSource();
    var lines = new List<string>();
    var task = (Task)method.Invoke(
        null,
        [
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"1..1000 | ForEach-Object { Write-Output $_; Start-Sleep -Milliseconds 50 }\"",
            new Action<string>(line => lines.Add(line)),
            cancellation.Token,
            null
        ])!;

    await Task.Delay(250);
    cancellation.Cancel();

    var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
    if (completed != task)
    {
        throw new TimeoutException("Canceled command did not complete within 5 seconds.");
    }

    try
    {
        await task;
        throw new InvalidOperationException("Canceled command completed without OperationCanceledException.");
    }
    catch (OperationCanceledException)
    {
        // Expected.
    }
}

static void InvokeKillProcessTree(Process process)
{
    var method = typeof(DiagnosticService).GetMethod("KillProcessTree", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(DiagnosticService), "KillProcessTree");
    method.Invoke(null, [process]);
}
