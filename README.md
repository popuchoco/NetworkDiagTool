# NetworkDiagTool

NetworkDiagTool is a Windows Forms network diagnostic tool for checking IP/Host, ports, gateways, DNS, proxies, ARP, routes, network adapters, and other local network information. It provides a Traditional Chinese user interface and writes diagnostic output to the console and local log files.

[繁體中文 README](README_TC.md)

## Features

- Ping: supports long-running packet-loss observation, up to 86,400 requests at approximately one request per second.
- TNC / TCP: checks whether the specified IP/Host and port are reachable.
- tracert: traces route hops to the target.
- Full diagnostic: combines Ping, TCP, tracert, gateway Ping, DNS, Proxy, ARP, `route print`, `ipconfig`, `netstat`, and `Get-NetAdapter`.
- System information: displays `ipconfig`, `netstat`, the routing table, and network adapter status.
- DNS resolution: checks whether a host can be resolved to an IP address.
- ARP lookup: runs `arp -a` to help determine whether a target remains present on the local network when Ping fails.
- Proxy detection: runs `netsh winhttp show proxy` to display the WinHTTP Proxy configuration.
- Diagnostic summary: full diagnostic output includes PASS/FAIL, Severity, elapsed-time statistics, Network Health score, and a summary.
- Log storage: diagnostic records are organized by project name, date, and hour.

## Limitations

- This tool does not include `iperf` / `iperf3` upload, download, throughput, jitter, or performance stress testing. Use iperf/iperf3 separately for bandwidth testing.
- This tool cannot use a **HAL** (Hardware Abstraction Layer) API to obtain the current Link state, negotiated speed, or packet statistics of each LAN port on a switch/router. Use the device SNMP, CLI, Controller API, or management interface for port-level information.
- The tool can run offline in an isolated LAN, but DNS, Proxy, tracert, and TCP results still depend on the services and routes available in that environment.

## Project Structure

```text
.
├─ README.md
├─ README_TC.md
├─ LICENSE
├─ .gitignore
├─ .editorconfig
└─ work/
   ├─ NetworkDiagTool.slnx
   ├─ README.md
   ├─ publish-self-contained-win-x64.ps1
   ├─ NetworkDiagTool/
   │  ├─ AppConfig.cs
   │  ├─ DiagnosticService.cs
   │  ├─ LogService.cs
   │  ├─ MainForm.cs
   │  ├─ Program.cs
   │  ├─ NetworkDiagTool.csproj
   │  └─ config.template.json
   └─ NetworkDiagTool.Tests/
      ├─ NetworkDiagTool.Tests.csproj
      └─ Program.cs
```

`outputs/`, `tmp/`, `bin/`, `obj/`, and `work/package_*/` are generated, temporary, or historical iteration artifacts and are not part of the public source tree.

Diagnostic logs may contain company, device, IP, host-name, and network-topology information. Logs are for troubleshooting in the user's own environment, are not test evidence for this open-source project, and must not be committed to a public repository.

## Runtime Requirements

- Windows 10/11.
- .NET SDK 10.0.302 or later for building the `.slnx` solution.
- The framework-dependent portable package requires the .NET 10 Desktop Runtime on the target computer.
- If the target environment cannot install the runtime, use the self-contained publishing option described below.

## Configuration

The program does not create `config.json` automatically. Before the first launch, copy `config.template.json` and rename the copy to `config.json`.

```json
{
  "LogDirectory": "C:\\Temp\\NetworkDiagLogs",
  "DefaultTimeoutMs": 3000,
  "DefaultPingCount": 4
}
```

Field descriptions:

- `LogDirectory`: root directory for logs. The actual path is organized as `LogDirectory\NetworkDiagTool\yyyyMMdd\HH\yyyyMMdd_HHmmss_<diagnostic-item>.log`.
- `DefaultTimeoutMs`: default timeout in milliseconds; allowed range is 500 to 60,000.
- `DefaultPingCount`: default Ping count; a single Ping operation allows 1 to 86,400 requests.

If the configured log directory is not writable, the program falls back to a writable path and shows a warning in both the console and the status bar. When troubleshooting log storage, use the actual path displayed in the console.

## Input Validation

- `IP/Host` accepts IPv4, IPv6, domains, `localhost`, and common internal host names.
- IPv4 must use four complete octets, such as `192.168.1.10`; shortened forms such as `192.168.207` are rejected.
- IPv4 fields with leading zeroes are rejected, such as `192.168.001.001`.
- Internal host names may contain letters, digits, underscores, hyphens, and dots, such as `SRV_APP01`.
- URLs, URL paths, and host:port values are rejected in the `IP/Host` field, including `http://`, `https://`, and `8.8.8.8:443`.
- `Port` accepts 1 to 65,535; an empty value is treated as port `80`.
- `Timeout` accepts 500 to 60,000 milliseconds.

## Ping and Full Diagnostic Limits

The single `Ping` operation is intended for long-running packet-loss observation:

- Ping count accepts 1 to 86,400 requests.
- Requests run at approximately one per second.
- `86,400` is approximately a 24-hour test.
- If Timeout is greater than 1,000 ms and the target continuously times out, the actual duration may exceed the simple 24-hour estimate.
- Long-running tests generate larger console and log files; make sure the log volume has sufficient space.

`Full Diagnostic` is a short health check:

- Even if the Ping count field is greater than 30, Ping inside Full Diagnostic is capped at 30 requests.
- Use the single `Ping` operation, not Full Diagnostic, for a 24-hour packet-loss test.

## ARP Interpretation

When Ping fails, Full Diagnostic also evaluates the `arp -a` result:

- If the target IP appears in the ARP table and its MAC address is not `00:00:00:00:00:00` or `FF:FF:FF:FF:FF:FF`, the target may have a network adapter bound to that IP while ICMP is disabled or blocked.
- If Ping fails and the target IP does not appear in the ARP table, there may currently be no device using that IP on the local network, or the target may be outside the current subnet/VLAN.

## Build

```powershell
dotnet build .\work\NetworkDiagTool.slnx -c Release
```

Release output:

```text
work\NetworkDiagTool\bin\Release\net10.0-windows\NetworkDiagTool.exe
```

## Tests

The test command uses `--no-build`, so complete the build step above first.

```powershell
dotnet run --project .\work\NetworkDiagTool.Tests\NetworkDiagTool.Tests.csproj -c Release --no-build
```

The tests cover:

- IPv4 leading-zero validation.
- Internal host names and URL/host:port rejection.
- Ping timeout counting.
- Command exit-code handling.
- TCP cancellation.
- Diagnostic output encoding checks.
- Kill race conditions when a real OS process has already exited.
- stdout/stderr release after canceling an external command.

## Self-contained Publishing

To create a self-contained win-x64 package for a computer without the .NET Desktop Runtime:

```powershell
.\work\publish-self-contained-win-x64.ps1
```

Or run:

```powershell
dotnet publish .\work\NetworkDiagTool\NetworkDiagTool.csproj -c Release -r win-x64 --self-contained true -o .\outputs\NetworkDiagTool_SelfContained_win-x64 /p:PublishSingleFile=false
```

## License

This project is licensed under the Apache License 2.0. See [LICENSE](LICENSE) for the full text.
