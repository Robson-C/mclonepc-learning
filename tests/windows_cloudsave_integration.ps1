$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'mclonepc-cloudsave-test-' + [Guid]::NewGuid().ToString('N')
)

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $compiler = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    ) | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    } | Select-Object -First 1
    if (-not $compiler) {
        throw 'Compilador C# do .NET Framework 4 não encontrado.'
    }

    $testExecutable = Join-Path $temporaryRoot 'cloud-save-selftest.exe'
    & $compiler /nologo /target:exe /platform:anycpu /optimize+ `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Net.Http.dll `
        /reference:System.Security.dll `
        /reference:System.Web.Extensions.dll `
        /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll `
        "/out:$testExecutable" `
        (Join-Path $repositoryRoot `
            'windows\cloudsave\MClonePC.CloudSave.Core.cs') `
        (Join-Path $repositoryRoot `
            'windows\cloudsave\MClonePC.CloudSave.Bridge.cs') `
        (Join-Path $repositoryRoot `
            'tests\MClonePC.CloudSave.SelfTest.cs')
    if ($LASTEXITCODE -ne 0) {
        throw 'Falha ao compilar o teste do save em nuvem.'
    }

    $testData = Join-Path $temporaryRoot 'data'
    & $testExecutable $testData
    if ($LASTEXITCODE -ne 0) {
        throw "Teste do save em nuvem terminou com código $LASTEXITCODE."
    }
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
