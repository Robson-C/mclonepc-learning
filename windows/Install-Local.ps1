[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceGameDirectory,

    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [int64]$VersionCode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [System.IO.Path]::GetFullPath($SourceGameDirectory)
$destination = [System.IO.Path]::GetFullPath($InstallRoot)
$runtime = Join-Path $PSScriptRoot 'runtime'

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Fonte do jogo ausente: $source"
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'mclonepc.exe') -PathType Leaf)) {
    throw 'A fonte não contém mclonepc.exe.'
}
if (Test-Path -LiteralPath $destination) {
    throw "A instalação já existe e não será sobrescrita: $destination"
}

$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
New-Item -ItemType Directory -Path $destination | Out-Null

try {
    Copy-Item -Recurse -LiteralPath $source -Destination (Join-Path $destination 'game')
    New-Item -ItemType Directory -Path (Join-Path $destination 'updater') | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtime 'MClonePC-Updater.ps1') `
        -Destination (Join-Path $destination 'updater\MClonePC-Updater.ps1')
    Copy-Item -LiteralPath (Join-Path $runtime 'Atualizar MClonePC.cmd') `
        -Destination (Join-Path $destination 'Atualizar MClonePC.cmd')
    Copy-Item -LiteralPath (Join-Path $runtime 'Jogar MClonePC.cmd') `
        -Destination (Join-Path $destination 'Jogar MClonePC.cmd')

    New-Item -ItemType Directory -Path (Join-Path $destination 'state') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destination 'backups') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destination 'work') | Out-Null

    $state = [ordered]@{
        schema_version = 1
        version = $Version
        version_code = $VersionCode
        installed_at = (Get-Date).ToUniversalTime().ToString('o')
        source = 'verified-local-install'
        previous_version = $null
    }
    $state | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $destination 'state\installed.json') `
            -Encoding UTF8
}
catch {
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -Recurse -Force -LiteralPath $destination
    }
    throw
}

Write-Output "INSTALADO: $destination"

