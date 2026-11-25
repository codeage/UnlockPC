param([Parameter(Mandatory)][string]$TargetExe)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $prefix = 'Local\UnlockPC.' + $identity.User.Value } finally { $identity.Dispose() }
$sessionId = (Get-Process -Id $PID).SessionId
$targetPath = [IO.Path]::GetFullPath($TargetExe)
foreach ($app in @(Get-Process -Name UnlockPC -ErrorAction SilentlyContinue)) {
    try {
        if ($app.HasExited -or $app.SessionId -ne $sessionId) { continue }
        $signal = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting("$prefix.Exit.$($app.Id)", [ref]$signal)) {
            try { [void]$signal.Set() } finally { $signal.Dispose() }
            Write-Host "Requested UnlockPC exit (PID $($app.Id))."
        } elseif ($app.Path -eq $targetPath -and !$app.HasExited) {
            # First upgrade from a build that predates the named event.
            [void]$app.CloseMainWindow()
        } else {
            continue
        }
        if (!$app.WaitForExit(10000)) { throw "UnlockPC (PID $($app.Id)) did not exit before the build." }
    } finally { $app.Dispose() }
}
