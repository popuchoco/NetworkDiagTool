# NetworkDiagTool 網路診斷工具

NetworkDiagTool 是一套 Windows Forms 桌面版網路診斷工具，提供現場快速檢查 IP/Host、Port、Gateway、DNS、Proxy、ARP、路由、網卡狀態與系統網路資訊的能力。工具以繁體中文介面呈現，會同步輸出 Console 診斷內容與 `.log` 檔，方便現場排查與交付紀錄。

## 功能特色

- Ping：支援長時間掉包觀察，單項 Ping 最多 86400 次，約每秒一次。
- TNC / TCP：檢查指定 IP/Host 與 Port 是否可連線。
- tracert：追蹤到目標的路由 hop。
- 完整診斷：整合 Ping、TCP、tracert、Gateway Ping、DNS、Proxy、ARP、route print、ipconfig、netstat 與 Get-NetAdapter。
- 系統資訊：查看 ipconfig、netstat、路由表與網卡狀態。
- DNS 解析：確認 host 是否能解析成 IP。
- 地址解析：執行 `arp -a`，輔助判斷 Ping 失敗時目標是否仍存在於同一區網。
- Proxy 偵測：執行 `netsh winhttp show proxy`，顯示 WinHTTP Proxy 設定。
- 結果總結：完整診斷會輸出 PASS/FAIL、Severity、耗時統計、Network Health 分數與 Summary。
- Log 保存：依專案名稱、日期、小時分層保存診斷紀錄。

## 工具限制

- 本工具不包含 `iperf` / `iperf3` 的上行、下載、吞吐量、jitter 或效能壓力測試。若要做頻寬測試，請另行使用 iperf/iperf3。
- 本工具無法透過 **HAL** (Hardware Abstraction Layer) API 取得 switch/router 每個 LAN Port 的 Link 狀態、協商速率及封包統計。若需要交換器連接埠層級資訊，請使用設備的 SNMP、CLI、Controller API 或管理介面。
- 本工具可在封閉內網離線使用，但 DNS、Proxy、tracert、TCP 等結果仍取決於該內網是否提供對應服務與路由。

## 專案結構

```text
.
├─ README.md
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

`outputs/`、`tmp/`、`bin/`、`obj/` 與 `work/package_*/` 是產出物、暫存或歷史迭代包，公開原始碼不包含這些內容。

診斷 Log 可能包含公司、設備、IP、主機名稱與網路拓撲等敏感資訊；Log 僅供使用者在自己的環境中排查，不是本開源專案的測試依據，也不得併入公開 repository。

## 執行環境

- Windows 10/11。
- .NET SDK 10.0.302 或更新版本，需支援 `.slnx`。
- 執行 framework-dependent portable 套件時，目標主機需安裝 .NET 10 Desktop Runtime。
- 若封閉內網不方便安裝 Runtime，可使用 self-contained 發行方式產生獨立執行包。

## Config 設定

程式不會自動建立 `config.json`。第一次使用時，請複製 `config.template.json`，並將副本重新命名為 `config.json` 後再啟動程式。

```json
{
  "LogDirectory": "C:\\Temp\\NetworkDiagLogs",
  "DefaultTimeoutMs": 3000,
  "DefaultPingCount": 4
}
```

欄位說明：

- `LogDirectory`：Log 根目錄。實際寫入路徑會建立為 `LogDirectory\NetworkDiagTool\yyyyMMdd\HH\yyyyMMdd_HHmmss_診斷項目.log`。
- `DefaultTimeoutMs`：預設 Timeout，單位毫秒，允許 500 到 60000。
- `DefaultPingCount`：預設 Ping 次數，單項 Ping 允許 1 到 86400。

若指定的 Log 目錄不可寫入，程式會 fallback 到可寫入路徑，並在 Console 與狀態列顯示警告。排查 Log 問題時，請以 Console 顯示的實際 Log 檔案路徑為準。

## 輸入驗證

- `IP/Host` 支援 IPv4、IPv6、domain、`localhost` 與常見內部主機名稱。
- IPv4 必須是完整四段格式，例如 `192.168.1.10`。不接受 `192.168.207` 這類縮寫。
- IPv4 不接受非零開頭的多位數字段，例如 `192.168.001.001`。
- 內部主機名稱允許英數字、底線、連字號與點，例如 `SRV_APP01`。
- 不接受 `http://`、`https://`、URL path 或把 Port 寫在 `IP/Host` 欄位，例如 `8.8.8.8:443`。
- `Port` 允許 1 到 65535；若空白，視為 `80`。
- `Timeout` 允許 500 到 60000 ms。

## Ping 與完整診斷限制

單項 `Ping` 適合長時間掉包觀察：

- Ping 次數允許 1 到 86400。
- 約每秒執行一次。
- `86400` 約等於 24 小時測試。
- 若 Timeout 大於 1000 ms 且目標持續逾時，實際總耗時可能超過 24 小時估算。
- 長時間測試會產生較大的 Console 與 Log 檔案，請確認 Log 目錄容量足夠。

`完整診斷` 是短時間健康檢查：

- 即使畫面 Ping 次數輸入大於 30，完整診斷中的 Ping 仍最多執行 30 次。
- 若要做 24 小時掉包測試，請使用單項 `Ping`，不要使用完整診斷。

## ARP 判讀邏輯

當 Ping 失敗時，完整診斷會參考 `arp -a` 結果：

- `arp -a` 清單內有目標 IP，且 MAC Address 不是 `00:00:00:00:00:00` 或 `FF:FF:FF:FF:FF:FF`：代表該 IP 確實可能有網卡綁定，但 ICMP 可能被關閉或被防火牆阻擋。
- Ping 失敗且 `arp -a` 清單內沒有目標 IP：代表區網內目前可能沒有設備使用該 IP，或目前設備與該 IP 不在同一個子網路/VLAN。

## 建置

```powershell
dotnet build .\work\NetworkDiagTool.slnx -c Release
```

Release 輸出位置：

```text
work\NetworkDiagTool\bin\Release\net10.0-windows\NetworkDiagTool.exe
```

## 測試

測試指令使用 `--no-build`，因此請先完成上方 build。

```powershell
dotnet run --project .\work\NetworkDiagTool.Tests\NetworkDiagTool.Tests.csproj -c Release --no-build
```

測試涵蓋：

- IPv4 leading zero 驗證。
- 內部 host name 與 URL/host:port 排除。
- Ping timeout 統計。
- command ExitCode 判斷。
- TCP 使用者取消。
- 診斷輸出常數亂碼檢查。
- 真實 OS process 已自然結束時的 Kill race condition。
- 取消外部命令後 stdout/stderr 讀取釋放。

## Self-contained 發行

若目標主機無法安裝 .NET Desktop Runtime，可產生 self-contained win-x64 套件：

```powershell
.\work\publish-self-contained-win-x64.ps1
```

或手動執行：

```powershell
dotnet publish .\work\NetworkDiagTool\NetworkDiagTool.csproj -c Release -r win-x64 --self-contained true -o .\outputs\NetworkDiagTool_SelfContained_win-x64 /p:PublishSingleFile=false
```

## 授權

本專案採用 Apache License 2.0。完整授權內容請參閱 [LICENSE](LICENSE)。
