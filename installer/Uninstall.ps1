[CmdletBinding()]
param(
    [switch] $KeepSettings = $true,
    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH Web Manager'
$manager = Join-Path $installDir 'DSHWebManager.exe'
if (Test-Path -LiteralPath $manager -PathType Leaf) {
    $stop = Start-Process -FilePath $manager -ArgumentList '--stop' -PassThru
    if (-not $stop.WaitForExit(15000)) { $stop.Kill() }
}
Get-Process -Name 'DSHWebManager' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'DSH Web Manager.lnk'
$paths = @(
    $startupLink,
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Uninstall DSH Web Manager.lnk')
)
foreach ($path in $paths) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSHWebManager' -Recurse -Force -ErrorAction SilentlyContinue
$managedRuntime = Join-Path $env:LOCALAPPDATA 'DSHWebManager\runtime'
if ((Test-Path -LiteralPath $managedRuntime) -and $managedRuntime.StartsWith((Join-Path $env:LOCALAPPDATA 'DSHWebManager'), [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $managedRuntime -Recurse -Force -ErrorAction SilentlyContinue
}

$cleanup = Join-Path $env:TEMP ('remove-dsh-web-manager-' + [guid]::NewGuid().ToString('N') + '.cmd')
$dataLine = if ($KeepSettings) { '' } else { 'rmdir /s /q "' + (Join-Path $env:LOCALAPPDATA 'DSHWebManager') + '"' }
@"
@echo off
ping 127.0.0.1 -n 3 >nul
rmdir /s /q "$installDir"
$dataLine
del "%~f0"
"@ | Set-Content -LiteralPath $cleanup -Encoding ASCII
Start-Process -FilePath $cleanup -WindowStyle Hidden

if (-not $Quiet) {
    [System.Windows.Forms.MessageBox]::Show(
        'DSH Web Manager was removed. Your .dsh data, workspaces, settings, and logs were preserved.',
        'DSH Web Manager', 'OK', 'Information') | Out-Null
}
