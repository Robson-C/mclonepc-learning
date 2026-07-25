[CmdletBinding()]
param(
    [string]$InstallRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ManifestUrl = 'https://github.com/Robson-C/mclonepc-learning/releases/latest/download/update.json',
    [string]$ManifestPath,
    [string]$ArtifactDirectory,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Caminho absoluto recusado: $RelativePath"
    }
    $rootFull = (Get-FullPath -Path $Root).TrimEnd('\') + '\'
    $candidate = Get-FullPath -Path (Join-Path $rootFull $RelativePath)
    if (-not $candidate.StartsWith(
        $rootFull,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Caminho fora da instalação recusado: $RelativePath"
    }
    return $candidate
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Arquivo JSON ausente: $Path"
    }
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Write-JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $temporary = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -Force -LiteralPath $temporary -Destination $Path
}

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

$installRootFull = Get-FullPath -Path $InstallRoot
$gameDirectory = Join-Path $installRootFull 'game'
$statePath = Join-Path $installRootFull 'state\installed.json'
$backupRoot = Join-Path $installRootFull 'backups'
$workRoot = Join-Path $installRootFull 'work'
$logPath = Join-Path $installRootFull 'state\updater.log'

if (-not (Test-Path -LiteralPath $gameDirectory -PathType Container)) {
    throw "Pasta do jogo ausente: $gameDirectory"
}
$state = Read-JsonFile -Path $statePath

if ($ManifestPath) {
    $manifest = Read-JsonFile -Path (Get-FullPath -Path $ManifestPath)
}
else {
    if (-not $ManifestUrl.StartsWith('https://')) {
        throw 'O manifesto remoto precisa usar HTTPS.'
    }
    $response = Invoke-WebRequest -UseBasicParsing -Uri $ManifestUrl
    $manifest = $response.Content | ConvertFrom-Json
}

if ($manifest.schema_version -ne 1) {
    throw "schema_version de update não suportado: $($manifest.schema_version)"
}
if ([int64]$manifest.version_code -le [int64]$state.version_code) {
    Write-Output "ATUAL: $($state.version) já está na versão mais recente."
    exit 0
}

$artifacts = @($manifest.artifacts | Where-Object { $_.platform -eq 'windows-x64' })
if ($artifacts.Count -ne 1) {
    throw 'O manifesto precisa conter exatamente um artefato windows-x64.'
}
$artifact = $artifacts[0]

if ($CheckOnly) {
    Write-Output "DISPONIVEL: $($state.version) -> $($manifest.version)"
    exit 10
}

if (Get-Process -Name 'mclonepc' -ErrorAction SilentlyContinue) {
    throw 'Feche o MClonePC antes de atualizar.'
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$operationId = [Guid]::NewGuid().ToString('N')
$operationRoot = Join-Path $workRoot $operationId
$downloadPath = Join-Path $operationRoot $artifact.filename
$extractRoot = Join-Path $operationRoot 'extracted'
$stagingGame = Join-Path $operationRoot 'game'
New-Item -ItemType Directory -Force -Path $operationRoot | Out-Null

try {
    if ($ArtifactDirectory) {
        $sourceArtifact = Assert-ChildPath `
            -Root (Get-FullPath -Path $ArtifactDirectory) `
            -RelativePath $artifact.filename
        Copy-Item -LiteralPath $sourceArtifact -Destination $downloadPath
    }
    else {
        if (-not ([string]$artifact.url).StartsWith('https://')) {
            throw 'A URL do artefato precisa usar HTTPS.'
        }
        Invoke-WebRequest -UseBasicParsing -Uri $artifact.url -OutFile $downloadPath
    }

    $actualSize = (Get-Item -LiteralPath $downloadPath).Length
    if ([int64]$actualSize -ne [int64]$artifact.size) {
        throw "Tamanho do download divergiu: $actualSize != $($artifact.size)"
    }
    $actualHash = Get-Sha256Lower -Path $downloadPath
    if ($actualHash -ne ([string]$artifact.sha256).ToLowerInvariant()) {
        throw 'SHA-256 do download divergiu.'
    }

    Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractRoot
    $packagePath = Join-Path $extractRoot 'package.json'
    $payloadRoot = Join-Path $extractRoot 'payload'
    $package = Read-JsonFile -Path $packagePath

    if ($package.schema_version -ne 1) {
        throw 'schema_version do pacote não suportado.'
    }
    if (
        [int64]$package.version_code -ne [int64]$manifest.version_code -or
        [string]$package.version -ne [string]$manifest.version
    ) {
        throw 'Versão interna do pacote diverge do manifesto.'
    }
    $supported = @($package.supported_from_version_codes | ForEach-Object { [int64]$_ })
    if ($supported -notcontains [int64]$state.version_code) {
        throw "O pacote não aceita a versão instalada $($state.version_code)."
    }

    foreach ($file in @($package.files)) {
        $payloadFile = Assert-ChildPath -Root $payloadRoot -RelativePath $file.path
        if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf)) {
            throw "Arquivo ausente no payload: $($file.path)"
        }
        if ([int64](Get-Item -LiteralPath $payloadFile).Length -ne [int64]$file.size) {
            throw "Tamanho divergente no payload: $($file.path)"
        }
        if ((Get-Sha256Lower -Path $payloadFile) -ne ([string]$file.sha256).ToLowerInvariant()) {
            throw "SHA-256 divergente no payload: $($file.path)"
        }
    }

    Copy-Item -Recurse -LiteralPath $gameDirectory -Destination $stagingGame
    foreach ($file in @($package.files)) {
        $payloadFile = Assert-ChildPath -Root $payloadRoot -RelativePath $file.path
        $destination = Assert-ChildPath -Root $stagingGame -RelativePath $file.path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) |
            Out-Null
        Copy-Item -Force -LiteralPath $payloadFile -Destination $destination
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDirectory = Join-Path $backupRoot "v$($state.version)-$timestamp"
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

    Move-Item -LiteralPath $gameDirectory -Destination $backupDirectory
    try {
        Move-Item -LiteralPath $stagingGame -Destination $gameDirectory
        $newState = [ordered]@{
            schema_version = 1
            version = [string]$manifest.version
            version_code = [int64]$manifest.version_code
            installed_at = (Get-Date).ToUniversalTime().ToString('o')
            source = 'github-release'
            previous_version = [string]$state.version
        }
        Write-JsonAtomic -Value $newState -Path $statePath
    }
    catch {
        if (Test-Path -LiteralPath $gameDirectory) {
            Remove-Item -Recurse -Force -LiteralPath $gameDirectory
        }
        Move-Item -LiteralPath $backupDirectory -Destination $gameDirectory
        throw
    }

    Get-ChildItem -LiteralPath $backupRoot -Directory |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip 3 |
        Remove-Item -Recurse -Force

    $message = "$(Get-Date -Format o) OK $($state.version) -> $($manifest.version)"
    Add-Content -LiteralPath $logPath -Value $message -Encoding UTF8
    Write-Output "ATUALIZADO: $($state.version) -> $($manifest.version)"
}
finally {
    if (Test-Path -LiteralPath $operationRoot) {
        Remove-Item -Recurse -Force -LiteralPath $operationRoot
    }
}

