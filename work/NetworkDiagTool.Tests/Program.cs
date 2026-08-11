using System.Diagnostics;
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
        AssertHostValid("WMS_API_01.internal");
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
        "[路由表] 執行 route print，顯示 Windows IP routing table / route map。"
    })
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DiagnosticService.cs is missing expected output constant: {expected}");
        }
    }
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
