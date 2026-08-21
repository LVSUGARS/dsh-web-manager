[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$build = Join-Path $root 'build'
$release = Join-Path $root 'release'
$payload = Join-Path $build 'setup-payload'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.8 C# compiler is required.' }
$icon = Join-Path $root 'assets\dsh-whale.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Run tools\make-icon.js before building.' }

New-Item -ItemType Directory -Path $build,$release -Force | Out-Null
$exeOutput = Join-Path $build 'DSHWebManager.exe'
$sourceFile = Join-Path $root 'src\Program.cs'
& $csc /nologo /target:winexe /optimize+ "/win32icon:$icon" "/out:$exeOutput" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.Management.dll /reference:System.Web.Extensions.dll `
    $sourceFile
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $root 'installer\Install-DshRuntime.ps1') -Destination (Join-Path $build 'Install-DshRuntime.ps1') -Force

if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'DSHWebManager.exe') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.ps1') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall.ps1') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.cmd') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'installer\Install-DshRuntime.ps1') -Destination $payload

$zip = Join-Path $release 'DSH-Web-Manager-1.1.0.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip

$setup = Join-Path $release 'DSH-Web-Manager-Setup-1.1.0.exe'
$payloadZip = Join-Path $build 'payload.zip'
if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadZip
& $csc /nologo /target:winexe /optimize+ "/win32icon:$icon" "/out:$setup" `
    /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll "/resource:$payloadZip,payload.zip" `
    (Join-Path $root 'src\Setup.cs')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Setup compilation failed.' }

Get-Item -LiteralPath (Join-Path $build 'DSHWebManager.exe'),$zip,$setup |
    Select-Object FullName,Length,@{n='SHA256';e={(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}}
