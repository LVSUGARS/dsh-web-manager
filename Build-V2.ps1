[CmdletBinding()]
param(
    [string]$DotnetRoot = (Join-Path $env:LOCALAPPDATA 'dotnet-sdk'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Missing .NET SDK: $dotnet" }
$solution = Join-Path $PSScriptRoot 'src-v2\DshLauncher.sln'
$output = Join-Path $PSScriptRoot 'build-v2'
$release = Join-Path $PSScriptRoot 'release-v2'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $dotnet restore $solution --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
& $dotnet build $solution --configuration $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
$appProject = Join-Path $PSScriptRoot 'src-v2\DshLauncher.App\DshLauncher.App.csproj'
$publishDir = Join-Path $output 'win-x64'
& $dotnet publish $appProject --configuration $Configuration --runtime win-x64 --self-contained true --output $publishDir --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer\Install-DshRuntime.ps1') -Destination (Join-Path $publishDir 'Install-DshRuntime.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer\UpdatePackage.ps1') -Destination (Join-Path $publishDir 'UpdatePackage.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer\Install.ps1') -Destination (Join-Path $publishDir 'Install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer\Uninstall.ps1') -Destination (Join-Path $publishDir 'Uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer\Install.cmd') -Destination (Join-Path $publishDir 'Install.cmd') -Force
$version = '2.0.1'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$zip = Join-Path $release "DSH-Web-Launcher-$version-win-x64.zip"
$checksum = Join-Path $release "DSH-Web-Launcher-$version-win-x64.zip.sha256"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath $checksum -Encoding ASCII
$payloadZip = Join-Path $output 'payload-v2.zip'
if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal
$setup = Join-Path $release "DSH-Web-Launcher-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $csc)) { throw "Missing .NET Framework C# compiler: $csc" }
& $csc /nologo /target:winexe /optimize+ "/win32icon:$(Join-Path $PSScriptRoot 'assets\dsh-whale.ico')" "/out:$setup" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$payloadZip,payload.zip" (Join-Path $PSScriptRoot 'installer\Setup-V2.cs')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup)) { throw 'Setup compilation failed.' }
Get-Item -LiteralPath (Join-Path $publishDir 'DSHWebLauncher.exe') | Select-Object FullName,Length
Get-Item -LiteralPath $zip,$checksum,$setup | Select-Object FullName,Length
