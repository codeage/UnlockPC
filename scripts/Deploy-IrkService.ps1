param(
    [Parameter(Mandatory)][ValidateSet('Stop', 'Deploy')][string]$Phase,
    [string]$AppExe
)

$ErrorActionPreference = 'Stop'
$serviceName = 'UnlockPCIrk'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceControl = Join-Path ([Environment]::SystemDirectory) 'sc.exe'

if ($Phase -eq 'Stop') {
    if ($service -and $service.Status -ne 'Stopped') {
        if ($service.Status -in 'Running', 'StartPending') {
            & $serviceControl stop $serviceName | Out-Null
            if ($LASTEXITCODE -notin 0, 1061, 1062) {
                throw "无法停止 UnlockPC IRK 服务（$LASTEXITCODE）。"
            }
        }
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $sid = $identity.User.Value
    $admin = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole('Administrator')
}
finally { $identity.Dispose() }

$target = Join-Path $env:ProgramFiles 'UnlockPC IRK Service\UnlockPCIrk.exe'
$source = Join-Path (Split-Path -Parent $AppExe) 'UnlockPCIrk.exe'
$registered = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -ErrorAction SilentlyContinue
$monitorName = 'UnlockPCMonitor'
$monitor = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$monitorName" -ErrorAction SilentlyContinue
$monitorTarget = Join-Path $env:ProgramFiles 'UnlockPC IRK Service\UnlockPCMonitor.exe'
$worker = Join-Path $env:ProgramFiles 'UnlockPC IRK Service\Worker\UnlockPC.exe'
$workerDll = Join-Path (Split-Path -Parent $worker) 'UnlockPC.dll'
$appDll = Join-Path (Split-Path -Parent $AppExe) 'UnlockPC.dll'
$expectedMonitor = '"{0}" {1} "{2}"' -f $monitorTarget, $sid, $worker
$expectedImage = '"{0}" {1}' -f $target, $sid

if (!$monitor -or $monitor.ImagePath -ne $expectedMonitor -or $monitor.Start -ne 2 -or
    !(Test-Path -LiteralPath $monitorTarget -PathType Leaf) -or
    !(Test-Path -LiteralPath $workerDll -PathType Leaf) -or
    (Get-FileHash -LiteralPath $appDll).Hash -ne (Get-FileHash -LiteralPath $workerDll).Hash -or
    (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $monitorTarget).Hash -or
    !$registered -or $registered.ImagePath -ne $expectedImage -or $registered.UnlockPCAuthorizedUser -ne $sid -or
    $registered.Start -ne 3 -or $registered.ObjectName -ne 'LocalSystem' -or
    !(Test-Path -LiteralPath $target -PathType Leaf) -or
    (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
    Write-Host '正在安装或更新 UnlockPC IRK 服务。Windows 可能会要求管理员确认。'
    $launch = @{
        FilePath = $AppExe
        ArgumentList = @('--install-irk-service', $sid)
        WindowStyle = 'Hidden'
        PassThru = $true
        Wait = $true
    }
    if (!$admin) { $launch.Verb = 'RunAs' }
    $installer = Start-Process @launch
    try {
        if ($installer.ExitCode -ne 0) { throw ('IRK 服务安装失败（0x{0:X8}）。' -f $installer.ExitCode) }
    }
    finally { $installer.Dispose() }
}
