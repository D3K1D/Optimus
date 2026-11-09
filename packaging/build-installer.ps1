# Publishes the app (packaging\publish.ps1) and builds artifacts\installer\Optimus-<version>-x64.msi.
#   powershell -ExecutionPolicy Bypass -File packaging\build-installer.ps1 [-AcceptWixEula] [-SkipPublish]
#              [-Sign -CertThumbprint <sha1> [-TimestampUrl <url>]]
# -AcceptWixEula passes -p:AcceptEula=wix7: WiX v7 refuses to build until its OSMF EULA is accepted
# (https://docs.firegiant.com/wix/osmf/). Pass it only if you, the person building, accept those terms; or run
# `wix eula accept wix7` once per machine instead.
param(
    [switch]$AcceptWixEula,
    [switch]$SkipPublish,
    [switch]$Sign,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $repo "Directory.Build.props") -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no x.y.z <Version> (found '$version')." }
$staging = Join-Path $repo "artifacts\publish\OptimizationTool"
$msi = Join-Path $repo "artifacts\installer\Optimus-$version-x64.msi"

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $found = Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\x64" } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "signtool.exe not found (install the Windows SDK signing tools)." }
    return $found.FullName
}
function Invoke-Sign([string[]]$paths) {
    $tool = Find-SignTool
    & $tool sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d "Optimus" @paths
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }
}
if ($Sign -and -not $CertThumbprint) { throw "-Sign needs -CertThumbprint (SHA-1 thumbprint of a code-signing cert in the store)." }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1") -Staging $staging
}

# Sign our own exes before they are packed, then the MSI after. Runtime DLLs are already Microsoft-signed.
if ($Sign) {
    Invoke-Sign (Get-ChildItem $staging -File -Filter "OptimizationTool*.exe" | ForEach-Object FullName)
    Invoke-Sign (Get-ChildItem $staging -File -Filter "OptimizationTool*.dll" | ForEach-Object FullName)
}

$props = @("-p:Version=$version", "-p:StagingDir=$staging", "-c", "Release", "-nologo", "-v", "m")
if ($AcceptWixEula) { $props += "-p:AcceptEula=wix7" }
if (Test-Path $msi) { Remove-Item -Force $msi }
dotnet build (Join-Path $PSScriptRoot "OptimizationTool.Installer.wixproj") @props
if ($LASTEXITCODE -ne 0) { throw "MSI build failed (if it mentions the EULA, see -AcceptWixEula above)." }
if (-not (Test-Path $msi)) { throw "Build succeeded but $msi is missing." }

if ($Sign) { Invoke-Sign @($msi) }

$mb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Built $msi ($mb MB)$(if ($Sign) { ', signed' } else { ', unsigned' })"
