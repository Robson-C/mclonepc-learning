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
        string installRoot = Directory.GetParent(
            AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar
            )
        ).FullName;
        string bridgeState = File.Exists(
            Path.Combine(installRoot, "state", "cloud-bridge.ready")
        ) ? "bridge-ready" : "bridge-missing";
        string documents = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"),
            "Robson",
            "MClonePC",
            "Documents"
        );
        Directory.CreateDirectory(documents);
        string request = Path.Combine(
            documents,
            ".mclonepc-cloud-request-test123.json"
        );
        string result = Path.Combine(
            documents,
            ".mclonepc-cloud-result-test123.json"
        );
        File.WriteAllText(
            request,
            "{\"schema_version\":1,\"action\":\"invalid\"}"
        );
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(result) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }
        string requestState = File.Exists(result)
            ? "bridge-request-served"
            : "bridge-request-missed";
        if (File.Exists(result))
        {
            File.Delete(result);
        }
        Thread.Sleep(500);
        File.WriteAllText(
            marker,
            Directory.GetCurrentDirectory() + "|" +
                String.Join("|", args) + "|" + bridgeState +
                "|" + requestState
        );
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
    $fakeOAuthCredential = Join-Path $temporaryRoot 'oauth-desktop.json'
    @'
{
  "installed": {
    "client_id": "123456-test.apps.googleusercontent.com",
    "client_secret": "test-client-secret"
  }
}
'@ | Set-Content -LiteralPath $fakeOAuthCredential -Encoding UTF8

    & (Join-Path $repositoryRoot 'tools\build_windows_portable.ps1') `
        -SourceGameDirectory $sourceGame `
        -OutputRoot $outputRoot `
        -Version '9.2.1' `
        -VersionCode 90201 `
        -GoogleOAuthDesktopJson $fakeOAuthCredential

    $packageRoot = Join-Path $outputRoot 'MClonePC Portable v9.2.1'
    foreach ($required in @(
        'MClonePC.exe',
        'MClonePC-Updater.exe',
        'MClonePC-Save-Nuvem.exe',
        'cloud-save.json',
        'game\mclonepc.exe',
        'game\MClonePC.version.json',
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
    $cloudConfig = Get-Content -Raw -LiteralPath (
        Join-Path $packageRoot 'cloud-save.json'
    ) | ConvertFrom-Json
    if (
        $cloudConfig.google_client_id -ne
            '123456-test.apps.googleusercontent.com' -or
        $cloudConfig.google_client_secret -ne
            'test-client-secret' -or
        $cloudConfig.google_scope -ne
            'https://www.googleapis.com/auth/drive.appdata' -or
        $cloudConfig.game_remote_file_name -ne
            'mclonepc-game-cloud-v1.json' -or
        $cloudConfig.save_directory -ne
            '%APPDATA%\Robson\MClonePC\Documents'
    ) {
        throw 'Configuração de save em nuvem divergente.'
    }
    $gameVersion = Get-Content -Raw -LiteralPath (
        Join-Path $packageRoot 'game\MClonePC.version.json'
    ) | ConvertFrom-Json
    if (
        $gameVersion.version -ne '9.2.1' -or
        $gameVersion.version_code -ne 90201
    ) {
        throw 'Metadados da versão do jogo divergentes.'
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
    $launcherStartInfo.EnvironmentVariables['APPDATA'] = Join-Path (
        $temporaryRoot
    ) 'appdata'
    $launcherStartInfo.EnvironmentVariables['LOCALAPPDATA'] = Join-Path (
        $temporaryRoot
    ) 'localappdata'
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
    if (
        $launcherResult -notmatch (
            '\|argumento simples\|com espaco\|bridge-ready' +
            '\|bridge-request-served$'
        )
    ) {
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

    $bridgeReady = Join-Path $packageRoot 'state\cloud-bridge.ready'
    $bridgeDeadline = (Get-Date).AddSeconds(8)
    while (
        (Test-Path -LiteralPath $bridgeReady) -and
        (Get-Date) -lt $bridgeDeadline
    ) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path -LiteralPath $bridgeReady) {
        throw 'A ponte de nuvem não encerrou depois do jogo.'
    }
    $cloudSaveExecutable = [System.IO.Path]::GetFullPath(
        (Join-Path $packageRoot 'MClonePC-Save-Nuvem.exe')
    )
    $processDeadline = (Get-Date).AddSeconds(5)
    do {
        $bridgeProcesses = @(
            Get-Process 'MClonePC-Save-Nuvem' -ErrorAction SilentlyContinue |
                Where-Object {
                    try {
                        [System.IO.Path]::GetFullPath(
                            $_.MainModule.FileName
                        ).Equals(
                            $cloudSaveExecutable,
                            [System.StringComparison]::OrdinalIgnoreCase
                        )
                    }
                    catch {
                        $false
                    }
                }
        )
        if ($bridgeProcesses.Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $processDeadline)
    if ($bridgeProcesses.Count -ne 0) {
        throw 'O processo da ponte não encerrou depois do jogo.'
    }
    Start-Sleep -Seconds 2

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
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                try {
                    Remove-Item -Recurse -Force -LiteralPath $resolvedTemp
                    break
                }
                catch {
                    if ($attempt -eq 29) {
                        throw
                    }
                    Start-Sleep -Milliseconds 200
                }
            }
        }
    }
}
