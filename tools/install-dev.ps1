# Builds the desktop app and copies it to a stable per-user folder, so the app you run and the MCP bridge
# Claude Code / Codex launch never lock the repo's build output. Re-run after pulling changes.
#   powershell -ExecutionPolicy Bypass -File tools\install-dev.ps1 [-Configuration Release] [-Target <dir>]
param(
    [string]$Configuration = "Release",
    [string]$Target = (Join-Path $env:LOCALAPPDATA "Programs\OptimizationTool")
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo "artifacts\install-build"

dotnet build (Join-Path $repo "src\Desktop\OptimizationTool.Desktop.csproj") -c $Configuration --artifacts-path $artifacts -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$out = Join-Path $artifacts ("bin\OptimizationTool.Desktop\{0}_win-x64" -f $Configuration.ToLowerInvariant())
foreach ($exe in "OptimizationTool.exe", "OptimizationTool.Host.exe", "OptimizationTool.Mcp.exe") {
    if (-not (Test-Path (Join-Path $out $exe))) { throw "Build output is missing $exe." }
}

# Copying over running binaries fails, so say which ones are in the way instead of half-copying.
$running = Get-Process OptimizationTool, OptimizationTool.Host, OptimizationTool.Mcp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    $list = ($running | ForEach-Object { "$($_.Name) ($($_.Id))" }) -join ", "
    throw "Close these first (Claude Code / Codex sessions run the bridge): $list"
}

New-Item -ItemType Directory -Force $Target | Out-Null
robocopy $out $Target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy $LASTEXITCODE)." }

$mcp = Join-Path $Target "OptimizationTool.Mcp.exe"
Write-Host "Installed to $Target"
Write-Host "Run the app:    `"$(Join-Path $Target 'OptimizationTool.exe')`""
Write-Host "Claude Code:    claude mcp remove optimization-tool --scope user; claude mcp add --scope user optimization-tool -- `"$mcp`""
Write-Host "Codex:          codex mcp add optimization-tool -- `"$mcp`""
