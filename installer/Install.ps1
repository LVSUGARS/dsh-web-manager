[CmdletBinding()]
param(
    [string] $SourceDir = $PSScriptRoot,
    [switch] $NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH Web Manager'
$exeSource = Join-Path $SourceDir 'DSHWebManager.exe'
if (-not (Test-Path -LiteralPath $exeSource -PathType Leaf)) { throw "Missing installer payload: $exeSource" }

$runningManagers = @(Get-Process -Name 'DSHWebManager' -ErrorAction SilentlyContinue)
foreach ($process in $runningManagers) {
    $process | Stop-Process -Force -ErrorAction SilentlyContinue
    try { $process.WaitForExit(5000) | Out-Null } catch { }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $exeSource -Destination (Join-Path $installDir 'DSHWebManager.exe') -Force
Copy-Item -LiteralPath (Join-Path $SourceDir 'Uninstall.ps1') -Destination (Join-Path $installDir 'Uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $SourceDir 'Install-DshRuntime.ps1') -Destination (Join-Path $installDir 'Install-DshRuntime.ps1') -Force

$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Uninstall DSH Web Manager.lnk')
)
foreach ($path in $shortcuts) {
    $shortcut = $shell.CreateShortcut($path)
    if ($path -like '*Uninstall*') {
        $shortcut.TargetPath = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        $shortcut.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDir 'Uninstall.ps1') + '"'
        $shortcut.Description = 'Uninstall DSH Web Manager'
    } else {
        $shortcut.TargetPath = Join-Path $installDir 'DSHWebManager.exe'
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = 'Start and stop DSH Web'
    }
    $shortcut.IconLocation = (Join-Path $installDir 'DSHWebManager.exe') + ',0'
    $shortcut.Save()
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSHWebManager'
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'DSH Web Manager'
Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.2.2'
Set-ItemProperty -Path $uninstallKey -Name Publisher -Value 'LVSUGARS'
Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir
Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value (Join-Path $installDir 'DSHWebManager.exe')
Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDir 'Uninstall.ps1') + '"')
Set-ItemProperty -Path $uninstallKey -Name NoModify -Type DWord -Value 1
Set-ItemProperty -Path $uninstallKey -Name NoRepair -Type DWord -Value 1

if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $installDir 'DSHWebManager.exe') }
