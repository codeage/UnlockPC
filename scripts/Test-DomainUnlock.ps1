[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../UnlockPC/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/UnlockPC.exe')
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Executable).Path
$nativeTest = & (Join-Path $PSScriptRoot 'Build-CredentialProvider.ps1') -SelfTest
& $nativeTest.FullName
if ($LASTEXITCODE -ne 0) { throw "Credential Provider unit tests failed ($LASTEXITCODE)." }

$appTest = Start-Process -FilePath $path -ArgumentList '--domain-unlock-self-test' -PassThru -Wait
try {
    $logs = Join-Path $env:LOCALAPPDATA 'UnlockPC/Logs'
    $log = Get-ChildItem -LiteralPath $logs -Filter "*-$($appTest.Id).jsonl" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($appTest.ExitCode -ne 0) {
        if ($log) { Get-Content -LiteralPath $log.FullName }
        throw "UnlockPC domain unit tests failed ($($appTest.ExitCode))."
    }
    if (!$log -or !(Select-String -LiteralPath $log.FullName -SimpleMatch 'domain_unlock_self_test_passed' -Quiet)) {
        throw 'UnlockPC did not report a passing domain self-test. Rebuild Release x64 before retrying.'
    }
    'PASS: console account, password identity and provider registration'
}
finally {
    $appTest.Dispose()
}
