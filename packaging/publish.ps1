# Publishes the desktop app, host, MCP bridge, broker and benchmark worker (self-contained, win-x64, Release) into one staging folder
# that the installer harvests. Builds go to artifacts/pkg-build, never src/*/bin (a running MCP bridge locks those).
#   powershell -ExecutionPolicy Bypass -File packaging\publish.ps1 [-Configuration Release] [-Staging <dir>]
param(
    [string]$Configuration = "Release",
    [string]$Staging
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Staging) { $Staging = Join-Path $repo "artifacts\publish\OptimizationTool" }
$build = Join-Path $repo "artifacts\pkg-build"

# One version for every exe and the MSI: Directory.Build.props <Version>.
$version = ([xml](Get-Content (Join-Path $repo "Directory.Build.props") -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no x.y.z <Version> (found '$version')." }

# Desktop first: its own files and ui/ win; companions only add what is not already there.
# The desktop's companion-copy targets are switched off; each companion is published on its own instead.
$projects = [ordered]@{
    "Desktop" = "src\Desktop\OptimizationTool.Desktop.csproj"
    "Host"    = "src\Host\OptimizationTool.Host.csproj"
    "Mcp"     = "src\Mcp\OptimizationTool.Mcp.csproj"
    "Broker"  = "src\Broker\OptimizationTool.Broker.csproj"
    "Bench"   = "src\Bench\OptimizationTool.Bench.csproj"
}
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "--artifacts-path", $build, "-nologo", "-v", "q",
            "-p:Version=$version", "-p:OtSkipHost=true", "-p:OtSkipMcp=true", "-p:OtSkipBroker=true", "-p:OtSkipBench=true", "-p:DebugType=none")

$outs = @{}
foreach ($name in $projects.Keys) {
    $out = Join-Path $build "out\$name"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "Publishing $name ($version)..."
    dotnet publish (Join-Path $repo $projects[$name]) @common -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name." }
    $outs[$name] = $out
}

if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
New-Item -ItemType Directory -Force $Staging | Out-Null

# Merge: the first copy wins, except that for a shared DLL with different bytes the higher assembly version, then the
# higher file version, wins (the SDK's own conflict rule). That covers the WinForms runtime's Microsoft.VisualBasic /
# System.Drawing (desktop pack supersedes the base facades) and a NuGet package newer than the framework copy.
# Any other differing shared file means mismatched versions: stop.
function Get-DllRank([string]$path) {
    $asm = [Reflection.AssemblyName]::GetAssemblyName($path).Version
    $file = [version]((Get-Item $path).VersionInfo.FileVersion -replace '[^\d\.].*$', '')
    return @($asm, $file)
}
$conflicts = @()
$replaced = @()
foreach ($name in $projects.Keys) {
    $src = $outs[$name]
    Get-ChildItem $src -Recurse -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length).TrimStart('\')
        if ($name -ne "Desktop" -and $rel -like "ui\*") { return }
        $dest = Join-Path $Staging $rel
        if (Test-Path $dest) {
            if ((Get-FileHash $dest).Hash -eq (Get-FileHash $_.FullName).Hash) { return }
            if ($_.Extension -ne ".dll") { $conflicts += "$rel ($name)"; return }
            try { $old = Get-DllRank $dest; $new = Get-DllRank $_.FullName }
            catch { $conflicts += "$rel ($name, not a managed assembly)"; return }
            if ($new[0] -gt $old[0] -or ($new[0] -eq $old[0] -and $new[1] -gt $old[1])) {
                Copy-Item $_.FullName $dest -Force
                $replaced += "$rel <- $name ($($new[0]) / $($new[1]))"
            }
            return
        }
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }
}
if ($conflicts) { throw "Companions ship different copies of: $($conflicts -join ', '). Align package versions." }
foreach ($r in $replaced) { Write-Host "  newer shared DLL: $r" }

# ui/ exactly as the desktop build lays it out (the Desktop csproj excludes ui/tests and ui/tools); check anyway.
foreach ($bad in "ui\tests", "ui\tools") {
    if (Test-Path (Join-Path $Staging $bad)) { throw "Staging contains $bad; it must not ship." }
}
$missing = @("OptimizationTool.exe", "OptimizationTool.Host.exe", "OptimizationTool.Mcp.exe", "OptimizationTool.Broker.exe", "OptimizationTool.Bench.exe", "ui\index.html") |
    Where-Object { -not (Test-Path (Join-Path $Staging $_)) }
if ($missing) { throw "Staging is missing: $($missing -join ', ')" }

$files = Get-ChildItem $Staging -Recurse -File
$mb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "Staged $($files.Count) files ($mb MB), version $version, in $Staging"
