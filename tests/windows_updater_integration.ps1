$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'mclonepc-updater-test-' + [Guid]::NewGuid().ToString('N')
)
$installRoot = Join-Path $temporaryRoot 'install'
$sourceGame = Join-Path $temporaryRoot 'source-game'
$payloadRoot = Join-Path $temporaryRoot 'payload'
$artifacts = Join-Path $temporaryRoot 'artifacts'

try {
    New-Item -ItemType Directory -Path $sourceGame | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceGame 'mclonepc.exe') `
        -Value 'fake executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $sourceGame 'unchanged.txt') `
        -Value 'preserve me' -Encoding UTF8

    & (Join-Path $repositoryRoot 'windows\Install-Local.ps1') `
        -SourceGameDirectory $sourceGame `
        -InstallRoot $installRoot `
        -Version '9.2.0' `
        -VersionCode 90200

    New-Item -ItemType Directory -Path $payloadRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $payloadRoot 'MClonePC.version.json') `
        -Value '{"version":"9.2.1","version_code":90201}' -Encoding UTF8

    New-Item -ItemType Directory -Path $artifacts | Out-Null
    $packagePath = Join-Path $artifacts 'MClonePC-Windows-v9.2.1.zip'
    python (Join-Path $repositoryRoot 'tools\build_update_package.py') `
        --payload-directory $payloadRoot `
        --version 9.2.1 `
        --version-code 90201 `
        --from-version-code 90200 `
        --output $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw 'build_update_package.py falhou.'
    }

    $manifestPath = Join-Path $artifacts 'update.json'
    python (Join-Path $repositoryRoot 'tools\build_release_manifest.py') `
        --version 9.2.1 `
        --version-code 90201 `
        --repository Robson-C/mclonepc-learning `
        --channel development `
        --artifact "windows-x64=$packagePath" `
        --output $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw 'build_release_manifest.py falhou.'
    }

    & (Join-Path $installRoot 'updater\MClonePC-Updater.ps1') `
        -InstallRoot $installRoot `
        -ManifestPath $manifestPath `
        -ArtifactDirectory $artifacts

    $state = Get-Content -Raw -LiteralPath (
        Join-Path $installRoot 'state\installed.json'
    ) | ConvertFrom-Json
    if ($state.version_code -ne 90201) {
        throw 'O estado instalado não avançou para 90201.'
    }
    if (-not (Test-Path -LiteralPath (
        Join-Path $installRoot 'game\MClonePC.version.json'
    ))) {
        throw 'O payload não foi aplicado.'
    }
    if ((Get-Content -Raw -LiteralPath (
        Join-Path $installRoot 'game\unchanged.txt'
    )).Trim() -ne 'preserve me') {
        throw 'Um arquivo não relacionado foi alterado.'
    }
    $backupCount = @(Get-ChildItem -LiteralPath (
        Join-Path $installRoot 'backups'
    ) -Directory).Count
    if ($backupCount -ne 1) {
        throw "Era esperado um backup; encontrados: $backupCount"
    }

    $tamperedPackage = Join-Path $artifacts 'MClonePC-Windows-v9.2.2.zip'
    python (Join-Path $repositoryRoot 'tools\build_update_package.py') `
        --payload-directory $payloadRoot `
        --version 9.2.2 `
        --version-code 90202 `
        --from-version-code 90201 `
        --output $tamperedPackage
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao criar o pacote destinado ao teste de adulteração.'
    }

    $tamperedManifest = Join-Path $artifacts 'tampered-update.json'
    python (Join-Path $repositoryRoot 'tools\build_release_manifest.py') `
        --version 9.2.2 `
        --version-code 90202 `
        --repository Robson-C/mclonepc-learning `
        --channel development `
        --artifact "windows-x64=$tamperedPackage" `
        --output $tamperedManifest
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao criar o manifesto destinado ao teste de adulteração.'
    }
    [System.IO.File]::AppendAllText($tamperedPackage, 'tampered')

    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $installRoot 'updater\MClonePC-Updater.ps1') `
        -InstallRoot $installRoot `
        -ManifestPath $tamperedManifest `
        -ArtifactDirectory $artifacts 2>$null
    $tamperedExitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    $ErrorActionPreference = $previousErrorPreference
    if ($tamperedExitCode -eq 0) {
        throw 'O atualizador aceitou um pacote adulterado.'
    }
    $stateAfterTamper = Get-Content -Raw -LiteralPath (
        Join-Path $installRoot 'state\installed.json'
    ) | ConvertFrom-Json
    if ($stateAfterTamper.version_code -ne 90201) {
        throw 'A tentativa adulterada modificou o estado instalado.'
    }
    $backupCountAfterTamper = @(Get-ChildItem -LiteralPath (
        Join-Path $installRoot 'backups'
    ) -Directory).Count
    if ($backupCountAfterTamper -ne 1) {
        throw 'A tentativa adulterada criou ou removeu backups.'
    }

    Write-Output 'WINDOWS_UPDATER_INTEGRATION_OK'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemp = [System.IO.Path]::GetFullPath($temporaryRoot)
        $systemTemp = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath()
        )
        if ($resolvedTemp.StartsWith(
            $systemTemp,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            Remove-Item -Recurse -Force -LiteralPath $resolvedTemp
        }
    }
}
