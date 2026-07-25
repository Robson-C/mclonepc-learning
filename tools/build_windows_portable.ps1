[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceGameDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [int64]$VersionCode,

    [Parameter(Mandatory = $true)]
    [string]$GoogleClientId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (
        Get-FileHash -Algorithm SHA256 -LiteralPath $Path
    ).Hash.ToLowerInvariant()
}

function Find-CSharpCompiler {
    $candidates = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw 'Compilador C# do .NET Framework 4 não encontrado.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Get-NormalizedFullPath -Path $SourceGameDirectory
$output = Get-NormalizedFullPath -Path $OutputRoot
$packageName = "MClonePC Portable v$Version"
$packageDirectory = Join-Path $output $packageName
$archivePath = Join-Path $output "MClonePC-Portable-Windows-v$Version.zip"
$archiveHashPath = "$archivePath.sha256"
$workingRoot = Join-Path $output ('.portable-build-' + [Guid]::NewGuid().ToString('N'))
$workingPackage = Join-Path $workingRoot $packageName

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Pasta fonte do jogo ausente: $source"
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'mclonepc.exe') -PathType Leaf)) {
    throw 'A pasta fonte não contém mclonepc.exe.'
}
if (
    [string]::IsNullOrWhiteSpace($GoogleClientId) -or
    -not $GoogleClientId.EndsWith(
        '.apps.googleusercontent.com',
        [System.StringComparison]::OrdinalIgnoreCase
    )
) {
    throw 'Google OAuth Client ID ausente ou inválido.'
}
if (
    $output.Equals($source, [System.StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith(
        $source + '\',
        [System.StringComparison]::OrdinalIgnoreCase
    )
) {
    throw 'A saída portátil não pode ficar dentro da pasta fonte do jogo.'
}
foreach ($target in @($packageDirectory, $archivePath, $archiveHashPath)) {
    if (Test-Path -LiteralPath $target) {
        throw "A saída já existe e não será sobrescrita: $target"
    }
}

$launcherSource = Join-Path $repositoryRoot 'windows\launcher\MClonePC.Launcher.cs'
$updaterLauncherSource = Join-Path (
    $repositoryRoot
) 'windows\launcher\MClonePC.UpdaterLauncher.cs'
$updaterSource = Join-Path (
    $repositoryRoot
) 'windows\runtime\MClonePC-Updater.ps1'
$cloudSaveCoreSource = Join-Path (
    $repositoryRoot
) 'windows\cloudsave\MClonePC.CloudSave.Core.cs'
$cloudSaveAppSource = Join-Path (
    $repositoryRoot
) 'windows\cloudsave\MClonePC.CloudSave.App.cs'
foreach ($requiredSource in @(
    $launcherSource,
    $updaterLauncherSource,
    $updaterSource,
    $cloudSaveCoreSource,
    $cloudSaveAppSource
)) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "Fonte autoral ausente: $requiredSource"
    }
}

$compiler = Find-CSharpCompiler
New-Item -ItemType Directory -Force -Path $output | Out-Null
New-Item -ItemType Directory -Path $workingPackage | Out-Null

try {
    $gameDestination = Join-Path $workingPackage 'game'
    Copy-Item -Recurse -LiteralPath $source -Destination $gameDestination

    New-Item -ItemType Directory -Path (
        Join-Path $workingPackage 'updater'
    ) | Out-Null
    New-Item -ItemType Directory -Path (
        Join-Path $workingPackage 'state'
    ) | Out-Null
    New-Item -ItemType Directory -Path (
        Join-Path $workingPackage 'backups'
    ) | Out-Null
    New-Item -ItemType Directory -Path (
        Join-Path $workingPackage 'work'
    ) | Out-Null

    Copy-Item -LiteralPath $updaterSource -Destination (
        Join-Path $workingPackage 'updater\MClonePC-Updater.ps1'
    )

    $iconPath = Join-Path $workingRoot 'MClonePC.ico'
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon(
        (Join-Path $source 'mclonepc.exe')
    )
    if ($null -ne $icon) {
        $iconStream = [System.IO.File]::Create($iconPath)
        try {
            $icon.Save($iconStream)
        }
        finally {
            $iconStream.Dispose()
            $icon.Dispose()
        }
    }

    $commonCompilerArguments = @(
        '/nologo',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/debug-',
        '/reference:System.dll',
        '/reference:System.Windows.Forms.dll'
    )
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
        $commonCompilerArguments += "/win32icon:$iconPath"
    }

    $launcherOutput = Join-Path $workingPackage 'MClonePC.exe'
    & $compiler @commonCompilerArguments "/out:$launcherOutput" $launcherSource
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao compilar MClonePC.exe.'
    }

    $updaterLauncherOutput = Join-Path $workingPackage 'MClonePC-Updater.exe'
    & $compiler @commonCompilerArguments `
        "/out:$updaterLauncherOutput" `
        $updaterLauncherSource
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao compilar MClonePC-Updater.exe.'
    }

    $cloudSaveOutput = Join-Path $workingPackage 'MClonePC-Save-Nuvem.exe'
    $cloudSaveCompilerArguments = @(
        '/nologo',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/debug-',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Net.Http.dll',
        '/reference:System.Security.dll',
        '/reference:System.Web.Extensions.dll',
        '/reference:System.IO.Compression.dll',
        '/reference:System.IO.Compression.FileSystem.dll'
    )
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
        $cloudSaveCompilerArguments += "/win32icon:$iconPath"
    }
    & $compiler @cloudSaveCompilerArguments `
        "/out:$cloudSaveOutput" `
        $cloudSaveCoreSource `
        $cloudSaveAppSource
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao compilar MClonePC-Save-Nuvem.exe.'
    }

    $cloudSaveConfig = [ordered]@{
        schema_version = 1
        google_client_id = $GoogleClientId
        google_scope = 'https://www.googleapis.com/auth/drive.appdata'
        remote_file_name = 'mclonepc-save-v1.zip'
        save_directory = '%APPDATA%\Robson\MClonePC\Documents'
    }
    $cloudSaveConfigPath = Join-Path $workingPackage 'cloud-save.json'
    $cloudSaveConfigJson = $cloudSaveConfig | ConvertTo-Json -Depth 5
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $cloudSaveConfigPath,
        $cloudSaveConfigJson,
        $utf8WithoutBom
    )

    $state = [ordered]@{
        schema_version = 1
        version = $Version
        version_code = $VersionCode
        installed_at = (Get-Date).ToUniversalTime().ToString('o')
        source = 'portable-package'
        previous_version = $null
    }
    $state | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (
            Join-Path $workingPackage 'state\installed.json'
        ) -Encoding UTF8

    $gameFiles = @(
        Get-ChildItem -LiteralPath $gameDestination -File -Recurse |
            Sort-Object FullName
    )
    $gameBytes = [int64](
        $gameFiles | Measure-Object -Property Length -Sum
    ).Sum
    $gameManifestLines = foreach ($file in $gameFiles) {
        $relativePath = $file.FullName.Substring(
            $gameDestination.Length + 1
        ).Replace('\', '/')
        "$(Get-Sha256Lower -Path $file.FullName)  $relativePath"
    }
    $gameManifestLines | Set-Content -LiteralPath (
        Join-Path $workingPackage 'game-files.sha256'
    ) -Encoding ASCII

    $packageManifest = [ordered]@{
        schema_version = 1
        product = 'MClonePC'
        package_type = 'windows-portable'
        version = $Version
        version_code = $VersionCode
        architecture = 'win32-game-anycpu-launcher'
        entry_point = 'MClonePC.exe'
        updater = 'MClonePC-Updater.exe'
        cloud_save = 'MClonePC-Save-Nuvem.exe'
        game_file_count = $gameFiles.Count
        game_bytes = $gameBytes
        packaged_at = (Get-Date).ToUniversalTime().ToString('o')
    }
    $packageManifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (
            Join-Path $workingPackage 'portable-package.json'
        ) -Encoding UTF8

    @"
MClonePC Portable $Version

Jogar:
  Execute MClonePC.exe.

Atualizar:
  Feche o jogo e execute MClonePC-Updater.exe.

Save em nuvem:
  Feche o jogo e execute MClonePC-Save-Nuvem.exe.
  A primeira conexão abre o Google no navegador.
  Enviar e restaurar são sempre ações manuais nesta versão.

Esta pasta é independente da pasta de desenvolvimento. Mova ou copie a pasta
inteira; não mova somente os executáveis.
"@ | Set-Content -LiteralPath (
        Join-Path $workingPackage 'LEIA-ME.txt'
    ) -Encoding UTF8

    $forbiddenLaunchers = @(
        Get-ChildItem -LiteralPath $workingPackage -File -Recurse |
            Where-Object { $_.Extension -in @('.bat', '.cmd') }
    )
    if ($forbiddenLaunchers.Count -ne 0) {
        throw 'O pacote contém um iniciador .bat ou .cmd.'
    }

    $temporaryArchive = Join-Path $workingRoot (
        "MClonePC-Portable-Windows-v$Version.zip"
    )
    Compress-Archive -LiteralPath $workingPackage `
        -DestinationPath $temporaryArchive `
        -CompressionLevel Optimal

    Move-Item -LiteralPath $workingPackage -Destination $packageDirectory
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath

    $archiveHash = Get-Sha256Lower -Path $archivePath
    "$archiveHash  $([System.IO.Path]::GetFileName($archivePath))" |
        Set-Content -LiteralPath $archiveHashPath -Encoding ASCII

    Write-Output "PACOTE_PORTATIL=$packageDirectory"
    Write-Output "ARQUIVO_ZIP=$archivePath"
    Write-Output "SHA256=$archiveHash"
}
finally {
    if (Test-Path -LiteralPath $workingRoot) {
        $resolvedWorking = Get-NormalizedFullPath -Path $workingRoot
        if (-not $resolvedWorking.StartsWith(
            $output + '\',
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            throw "Recusa de limpeza fora da saída: $resolvedWorking"
        }
        Remove-Item -Recurse -Force -LiteralPath $resolvedWorking
    }
}
