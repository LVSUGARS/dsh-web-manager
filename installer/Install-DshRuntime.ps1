[CmdletBinding()]
param(
    [string] $RuntimeRoot = (Join-Path $env:LOCALAPPDATA 'DSHWebManager\runtime'),
    [string] $NodeVersion = 'v24.19.0',
    [string] $DshVersion = 'latest',
    [switch] $UpdateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$nodeDir = Join-Path $RuntimeRoot 'node'
$dshDir = Join-Path $RuntimeRoot 'dsh'
$targetDshDir = if ($UpdateOnly) { Join-Path $RuntimeRoot 'dsh-next' } else { $dshDir }
$nodeExe = Join-Path $nodeDir 'node.exe'
$npmCmd = Join-Path $nodeDir 'npm.cmd'
$cli = Join-Path $targetDshDir 'node_modules\@deepseek-ai\dsh\lib\bin.js'
$ready = Join-Path $RuntimeRoot 'ready.json'
$logDir = Join-Path (Split-Path -Parent $RuntimeRoot) 'logs'
$log = Join-Path $logDir 'runtime-install.log'
New-Item -ItemType Directory -Path $RuntimeRoot,$logDir -Force | Out-Null
if (-not $UpdateOnly) { Remove-Item -LiteralPath $ready -Force -ErrorAction SilentlyContinue }
if ($UpdateOnly) { Remove-Item -LiteralPath $targetDshDir -Recurse -Force -ErrorAction SilentlyContinue }

function Write-Stage([string] $Message) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    $line | Tee-Object -FilePath $log -Append | Write-Host
}

function Write-ProgressStage([int] $Percent, [string] $Message) {
    Write-Stage $Message
    Write-Output ("DSH_PROGRESS:{0}:{1}" -f $Percent, $Message)
}

if (-not (Test-Path -LiteralPath $nodeExe -PathType Leaf)) {
    if ($UpdateOnly) { throw 'Managed Node.js runtime is missing.' }
    $archiveName = "node-$NodeVersion-win-x64.zip"
    $baseUrl = "https://nodejs.org/dist/$NodeVersion"
    $archive = Join-Path $RuntimeRoot $archiveName
    $checksums = Join-Path $RuntimeRoot 'SHASUMS256.txt'
    $extract = Join-Path $RuntimeRoot 'node-extract'
    Write-ProgressStage 15 "正在下载官方 Node.js..."
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$archiveName" -OutFile $archive
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/SHASUMS256.txt" -OutFile $checksums
    $line = Get-Content -LiteralPath $checksums | Where-Object { $_ -match "^[0-9a-fA-F]{64}\s+$([regex]::Escape($archiveName))$" } | Select-Object -First 1
    if (-not $line) { throw "Official checksum not found for $archiveName." }
    $expected = ($line -split '\s+')[0].ToUpperInvariant()
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) { throw "Node.js checksum mismatch. Expected $expected, got $actual." }
    Write-ProgressStage 25 'Node.js 校验通过，正在解压...'
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $extract | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $source = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if (-not $source -or -not (Test-Path -LiteralPath (Join-Path $source.FullName 'node.exe'))) { throw 'Downloaded Node.js archive is invalid.' }
    Remove-Item -LiteralPath $nodeDir -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item -LiteralPath $source.FullName -Destination $nodeDir
    Remove-Item -LiteralPath $extract -Recurse -Force
    Remove-Item -LiteralPath $archive,$checksums -Force
}

if (-not (Test-Path -LiteralPath $npmCmd -PathType Leaf)) { throw "npm.cmd is missing: $npmCmd" }
Write-ProgressStage 40 "正在安装官方 DSH，这可能需要几分钟..."
$env:npm_config_cache = Join-Path $RuntimeRoot 'npm-cache'
$oldErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $npmCmd install --prefix $targetDshDir --no-audit --no-fund --omit=dev "@deepseek-ai/dsh@$DshVersion" 2>&1 | Tee-Object -FilePath $log -Append | Write-Host
$npmExit = $LASTEXITCODE
$ErrorActionPreference = $oldErrorAction
if ($npmExit -ne 0) { throw "npm install failed with exit code $npmExit. See $log" }
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "DSH CLI was not created: $cli" }
Write-ProgressStage 70 '正在验证 DSH 安装...'
Push-Location $targetDshDir
try {
    $pendingJson = & $npmCmd approve-scripts --allow-scripts-pending --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect pending npm scripts. See $log" }
    $pending = @((($pendingJson -join [Environment]::NewLine) | ConvertFrom-Json).allowScripts | ForEach-Object { $_.name })
    $allowed = @('@deepseek-ai/dsh-subprocess-local', '@google/genai', 'koffi', 'node-pty', 'protobufjs')
    $unexpected = @($pending | Where-Object { $_ -notin $allowed })
    if ($unexpected.Count) { throw "DSH added unreviewed install scripts: $($unexpected -join ', '). Update DSH Web Manager before continuing." }
    $approved = @($pending | Where-Object { $_ -in $allowed })
    if ($approved.Count) {
        Write-ProgressStage 78 "正在完成已审核的 DSH 依赖..."
        & $npmCmd approve-scripts @approved 2>&1 | Tee-Object -FilePath $log -Append | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "Approved dependency scripts failed with exit code $LASTEXITCODE. See $log" }
    }
} finally { Pop-Location }
$package = Get-Content -LiteralPath (Join-Path $targetDshDir 'node_modules\@deepseek-ai\dsh\package.json') -Raw | ConvertFrom-Json
if ($UpdateOnly) {
    Write-ProgressStage 88 '正在切换新版本 DSH...'
    $backupDshDir = Join-Path $RuntimeRoot 'dsh-previous'
    Remove-Item -LiteralPath $backupDshDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $dshDir) { Move-Item -LiteralPath $dshDir -Destination $backupDshDir }
    try {
        Move-Item -LiteralPath $targetDshDir -Destination $dshDir
        Remove-Item -LiteralPath $backupDshDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        if (-not (Test-Path -LiteralPath $dshDir) -and (Test-Path -LiteralPath $backupDshDir)) {
            Move-Item -LiteralPath $backupDshDir -Destination $dshDir
        }
        throw
    }
}
[pscustomobject]@{
    nodeVersion = (& $nodeExe --version)
    dshVersion = $package.version
    completedUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath $ready -Encoding UTF8
Write-ProgressStage 93 "DSH 运行时已就绪：$($package.version)"
