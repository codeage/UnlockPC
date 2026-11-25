[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../UnlockPC/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/UnlockPC.exe'),
    [switch]$IncludeAdvancedOptions
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Executable).Path
$tests = @('--presence-self-test')
if ($IncludeAdvancedOptions) {
    $tests += '--ui-self-test advanced-options'
}

foreach ($arguments in $tests) {
    $process = Start-Process -FilePath $path -ArgumentList $arguments -PassThru
    try {
        if (-not $process.WaitForExit(20000)) {
            $process.Kill()
            throw "Timed out: $arguments"
        }
        if ($process.ExitCode -ne 0) {
            throw "$arguments failed with exit code $($process.ExitCode). See the UnlockPC log for process $($process.Id)."
        }
        Write-Output "Passed: $arguments"
    }
    finally {
        $process.Dispose()
    }
}
