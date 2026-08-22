[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][int]$ProcessId
)

$ErrorActionPreference = 'Stop'
$temp = Join-Path $env:TEMP ('dsh-web-launcher-apply-' + [guid]::NewGuid().ToString('N'))
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $process) { break }
        Start-Sleep -Milliseconds 250
    }
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Expand-Archive -LiteralPath $Package -DestinationPath $temp -Force
    $install = Join-Path $temp 'Install.ps1'
    if (-not (Test-Path -LiteralPath $install)) { throw '更新包缺少 Install.ps1。' }
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $install -SourceDir $temp -ProductVersion 2.0.0 -NoLaunch
    if ($LASTEXITCODE -ne 0) { throw "安装脚本退出码：$LASTEXITCODE" }
    $launcher = Join-Path $env:LOCALAPPDATA 'Programs\DSH Web Launcher\DSHWebLauncher.exe'
    if (Test-Path -LiteralPath $launcher) { Start-Process -FilePath $launcher }
}
catch {
    $logDir = Join-Path $env:LOCALAPPDATA 'DSHWebManager\logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $logDir 'launcher-update-error.log') -Encoding UTF8
}
finally {
    Remove-Item -LiteralPath $Package -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
