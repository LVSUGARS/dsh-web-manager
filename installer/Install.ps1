[CmdletBinding()]
param(
    [string] $SourceDir = $PSScriptRoot,
    [switch] $NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH Web Launcher'
$legacyInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH Web Manager'
$exeSource = Join-Path $SourceDir 'DSHWebLauncher.exe'
if (-not (Test-Path -LiteralPath $exeSource -PathType Leaf)) { throw "Missing installer payload: $exeSource" }

$runningLaunchers = @(Get-Process -Name 'DSHWebManager','DSHWebLauncher' -ErrorAction SilentlyContinue)
foreach ($process in $runningLaunchers) {
    $process | Stop-Process -Force -ErrorAction SilentlyContinue
    try { $process.WaitForExit(5000) | Out-Null } catch { }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $exeSource -Destination (Join-Path $installDir 'DSHWebLauncher.exe') -Force
Copy-Item -LiteralPath (Join-Path $SourceDir 'Uninstall.ps1') -Destination (Join-Path $installDir 'Uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $SourceDir 'Install-DshRuntime.ps1') -Destination (Join-Path $installDir 'Install-DshRuntime.ps1') -Force

$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Web 启动器.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Web 启动器.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) '卸载 DSH Web 启动器.lnk')
)
$legacyShortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH Web Manager.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Uninstall DSH Web Manager.lnk')
)
foreach ($path in $legacyShortcuts) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
foreach ($path in $shortcuts) {
    $shortcut = $shell.CreateShortcut($path)
    if ($path -like '*卸载*') {
        $shortcut.TargetPath = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        $shortcut.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDir 'Uninstall.ps1') + '"'
        $shortcut.Description = 'Uninstall DSH Web Launcher'
    } else {
        $shortcut.TargetPath = Join-Path $installDir 'DSHWebLauncher.exe'
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = 'Start and stop DSH Web'
    }
    $shortcut.IconLocation = (Join-Path $installDir 'DSHWebLauncher.exe') + ',0'
    $shortcut.Save()
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSHWebLauncher'
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DSHWebManager' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'DSH Web 启动器 / DSH Web Launcher'
Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.5.0'
Set-ItemProperty -Path $uninstallKey -Name Publisher -Value 'LVSUGARS'
Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir
Set-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value (Join-Path $installDir 'DSHWebLauncher.exe')
Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDir 'Uninstall.ps1') + '"')
Set-ItemProperty -Path $uninstallKey -Name NoModify -Type DWord -Value 1
Set-ItemProperty -Path $uninstallKey -Name NoRepair -Type DWord -Value 1

if ((Test-Path -LiteralPath $legacyInstallDir) -and $legacyInstallDir -ne $installDir) {
    Remove-Item -LiteralPath $legacyInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $installDir 'DSHWebLauncher.exe') }
