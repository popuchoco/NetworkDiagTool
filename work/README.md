# NetworkDiagTool

[English README](../README.md) | [繁體中文 README](../README_TC.md)

This directory contains the NetworkDiagTool solution and source projects. The application is a Windows Forms network diagnostic tool with Ping, TNC/TCP, tracert, full diagnostic, ipconfig, netstat, routing table, adapter status, DNS resolution, ARP lookup, and Proxy detection features.

## Configuration

The application does not create `config.json` automatically. Before the first launch, copy `NetworkDiagTool/config.template.json` to `NetworkDiagTool/config.json`.

```json
{
  "LogDirectory": "C:\\Temp\\NetworkDiagLogs",
  "DefaultTimeoutMs": 3000,
  "DefaultPingCount": 4
}
```

- `LogDirectory`: root directory for diagnostic logs, organized by project, date, and hour.
- `DefaultTimeoutMs`: default timeout from 500 to 60,000 milliseconds.
- `DefaultPingCount`: default single-operation Ping count from 1 to 86,400.

## Build and Test

```powershell
dotnet build .\work\NetworkDiagTool.slnx -c Release
dotnet run --project .\work\NetworkDiagTool.Tests\NetworkDiagTool.Tests.csproj -c Release --no-build
```

Complete `dotnet build` before running the test command. If the target computer does not have the .NET 10 Desktop Runtime, use `publish-self-contained-win-x64.ps1` to create a self-contained package.

## Limitations

- The tool does not include `iperf` / `iperf3` upload, download, throughput, jitter, or performance stress testing.
- The tool cannot use a HAL API to obtain switch/router LAN-port Link state, negotiated speed, or packet statistics.
- A single Ping supports up to 86,400 requests; Full Diagnostic caps its Ping phase at 30 requests.
- Diagnostic logs may contain environment-sensitive information and must not be committed to a public repository.
