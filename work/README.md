# NetworkDiagTool

Windows Forms 網路診斷工具，提供 Ping、TNC/TCP、tracert、完整診斷、ipconfig、netstat、路由表、網卡狀態、DNS 解析、地址解析與 Proxy 偵測功能。

## 使用前準備

程式不會自動建立 `config.json`。請先複製 `NetworkDiagTool/config.template.json`，重新命名為同一資料夾中的 `config.json`，再啟動程式。

```json
{
  "LogDirectory": "C:\\Temp\\NetworkDiagLogs",
  "DefaultTimeoutMs": 3000,
  "DefaultPingCount": 4
}
```

- `LogDirectory`：Log 根目錄；實際路徑會依專案名稱、日期與小時分層建立。
- `DefaultTimeoutMs`：預設 Timeout，允許 500 到 60000 毫秒。
- `DefaultPingCount`：預設 Ping 次數；單項 Ping 允許 1 到 86400 次。

## 建置與測試

```powershell
dotnet build .\work\NetworkDiagTool.slnx -c Release
dotnet run --project .\work\NetworkDiagTool.Tests\NetworkDiagTool.Tests.csproj -c Release --no-build
```

測試前請先完成 `dotnet build`。若目標電腦沒有 .NET 10 Desktop Runtime，請使用 `publish-self-contained-win-x64.ps1` 建立 self-contained 發行包。

## 限制

- 本工具不包含 `iperf` / `iperf3` 的上行、下載、吞吐量、jitter 或效能壓力測試。
- 本工具無法透過 HAL API 取得 switch/router 每個 LAN Port 的 Link 狀態、協商速率及封包統計。
- 單項 Ping 最多 86400 次；完整診斷中的 Ping 固定最多 30 次。
- 診斷 Log 可能包含環境敏感資訊，不應提交至公開 repository。
