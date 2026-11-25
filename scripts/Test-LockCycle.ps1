[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'UnlockPC\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64\UnlockPC.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '请先构建 UnlockPC Release x64。' }

$test = Start-Process -FilePath $exe -ArgumentList '--ui-self-test', 'lock-cycle' -PassThru -Wait
try {
    $logDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UnlockPC\Logs'
    $log = Get-ChildItem -LiteralPath $logDirectory -Filter "*-$($test.Id).jsonl" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($log) {
        Get-Content -LiteralPath $log.FullName | ForEach-Object { $_ | ConvertFrom-Json } | ForEach-Object {
            if ($_.action -eq 'lock_cycle_round_passed') {
                'PASS {0}/5  锁屏至请求 {1:F0} ms  请求至解锁 {2:F0} ms' -f $_.details.round, $_.details.lockedWaitMs, $_.details.unlockTimeMs
            }
            elseif ($_.action -eq 'ui_self_test_failed') {
                'FAIL 轮次 {0}  {1}' -f $_.details.round, $_.details.Message
            }
        }
        "日志 $($log.FullName)"
    }

    switch ($test.ExitCode) {
        0 { return }
        2 { '已取消自检。'; exit 2 }
        5 { throw '请先从系统托盘退出正在运行的 UnlockPC，再运行锁屏自检。' }
        default { throw "锁屏自检失败（退出码 $($test.ExitCode)）。" }
    }
}
finally {
    $test.Dispose()
}
