$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'mclonepc-portable-test-' + [Guid]::NewGuid().ToString('N')
)
$sourceGame = Join-Path $temporaryRoot 'source-game'
$outputRoot = Join-Path $temporaryRoot 'output'
$fakeGameSource = Join-Path $temporaryRoot 'FakeGame.cs'

try {
    New-Item -ItemType Directory -Path $sourceGame | Out-Null

    @'
using System;
using System.IO;
using System.Threading;
internal static class FakeGame
{
    private static int Main(string[] args)
    {
        string marker = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "launcher-test.txt"
        );
        File.WriteAllText(
            marker,
            Directory.GetCurrentDirectory() + "|" + String.Join("|", args)
        );
        Thread.Sleep(100);
        return 0;
    }
}
'@ | Set-Content -LiteralPath $fakeGameSource -Encoding UTF8

    $compiler = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    ) | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    } | Select-Object -First 1
    if (-not $compiler) {
        throw 'Compilador C# do .NET Framework 4 não encontrado.'
    }

    & $compiler /nologo /target:exe /platform:anycpu `
        "/out:$(Join-Path $sourceGame 'mclonepc.exe')" `
        $fakeGameSource
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao compilar o jogo falso.'
    }
    Set-Content -LiteralPath (Join-Path $sourceGame 'unchanged.txt') `
        -Value 'preserve me' -Encoding UTF8

    & (Join-Path $repositoryRoot 'tools\build_windows_portable.ps1') `
        -SourceGameDirectory $sourceGame `
        -OutputRoot $outputRoot `
        -Version '9.2.1' `
        -VersionCode 90201

    $packageRoot = Join-Path $outputRoot 'MClonePC Portable v9.2.1'
    foreach ($required in @(
        'MClonePC.exe',
        'MClonePC-Updater.exe',
        'game\mclonepc.exe',
        'updater\MClonePC-Updater.ps1',
        'state\installed.json',
        'portable-package.json',
        'game-files.sha256'
    )) {
        if (-not (Test-Path -LiteralPath (
            Join-Path $packageRoot $required
        ))) {
            throw "Arquivo portátil ausente: $required"
        }
    }

    $scriptLaunchers = @(
        Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
            Where-Object { $_.Extension -in @('.bat', '.cmd') }
    )
    if ($scriptLaunchers.Count -ne 0) {
        throw 'O pacote portátil contém .bat ou .cmd.'
    }
    if (@(Get-ChildItem -LiteralPath (
        Join-Path $packageRoot 'backups'
    ) -Force).Count -ne 0) {
        throw 'O pacote portátil contém backups anteriores.'
    }
    if (@(Get-ChildItem -LiteralPath (
        Join-Path $packageRoot 'work'
    ) -Force).Count -ne 0) {
        throw 'O pacote portátil contém resíduos de atualização.'
    }

    $launcherStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $launcherStartInfo.FileName = Join-Path $packageRoot 'MClonePC.exe'
    $launcherStartInfo.UseShellExecute = $false
    $launcherStartInfo.Arguments = '"argumento simples" "com espaco"'
    $launcher = [System.Diagnostics.Process]::Start($launcherStartInfo)
    $launcher.WaitForExit()
    if ($launcher.ExitCode -ne 0) {
        throw "Launcher terminou com código $($launcher.ExitCode)."
    }

    $launcherMarker = Join-Path $packageRoot 'game\launcher-test.txt'
    $deadline = (Get-Date).AddSeconds(5)
    while (
        -not (Test-Path -LiteralPath $launcherMarker) -and
        (Get-Date) -lt $deadline
    ) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $launcherMarker)) {
        throw 'O launcher não iniciou o jogo falso.'
    }
    $launcherResult = Get-Content -Raw -LiteralPath $launcherMarker
    $expectedWorkingDirectory = Join-Path $packageRoot 'game'
    if (-not $launcherResult.StartsWith(
        $expectedWorkingDirectory + '|',
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Diretório de trabalho divergente: $launcherResult"
    }
    if ($launcherResult -notmatch '\|argumento simples\|com espaco$') {
        throw "Argumentos divergentes: $launcherResult"
    }

    @'
param([string]$InstallRoot)
Set-Content -LiteralPath (
    Join-Path $InstallRoot 'state\updater-launcher-test.txt'
) -Value $InstallRoot -Encoding UTF8
Write-Output 'ATUALIZADOR_FALSO_OK'
'@ | Set-Content -LiteralPath (
        Join-Path $packageRoot 'updater\MClonePC-Updater.ps1'
    ) -Encoding UTF8

    $updaterLauncher = Start-Process -FilePath (
        Join-Path $packageRoot 'MClonePC-Updater.exe'
    ) -ArgumentList '--silent' -PassThru -Wait
    if ($updaterLauncher.ExitCode -ne 0) {
        throw "Updater launcher terminou com código $($updaterLauncher.ExitCode)."
    }
    $updaterMarker = Join-Path (
        $packageRoot
    ) 'state\updater-launcher-test.txt'
    if (-not (Test-Path -LiteralPath $updaterMarker -PathType Leaf)) {
        throw 'O updater launcher não executou seu componente interno.'
    }
    $reportedRoot = (Get-Content -Raw -LiteralPath $updaterMarker).Trim()
    $reportedRootFull = [System.IO.Path]::GetFullPath($reportedRoot).TrimEnd('\')
    $packageRootFull = [System.IO.Path]::GetFullPath($packageRoot).TrimEnd('\')
    if (-not $reportedRootFull.Equals(
        $packageRootFull,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "InstallRoot divergente: $reportedRoot"
    }

    $archive = Join-Path $outputRoot 'MClonePC-Portable-Windows-v9.2.1.zip'
    $archiveHash = "$archive.sha256"
    if (
        -not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $archiveHash -PathType Leaf)
    ) {
        throw 'ZIP ou SHA-256 do pacote portátil ausente.'
    }

    Write-Output 'WINDOWS_PORTABLE_INTEGRATION_OK'
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
