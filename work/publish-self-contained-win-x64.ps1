param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "NetworkDiagTool\NetworkDiagTool.csproj"
$output = Join-Path (Split-Path -Parent $root) "outputs\NetworkDiagTool_SelfContained_win-x64"

dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $output `
    /p:PublishSingleFile=false

Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $output "README.md") -Force

Write-Host "Self-contained package created:"
Write-Host $output
