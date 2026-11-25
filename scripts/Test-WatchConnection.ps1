#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Current', 'OnWristAwakeUnlocked', 'OnWristAsleepUnlocked', 'OffWristLocked', 'BluetoothOff')]
    [string]$State,
    [string]$Executable = (Join-Path $PSScriptRoot '../UnlockPC/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/UnlockPC.exe'),
    [switch]$KeepTrace
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Security.Cryptography.ProtectedData

function Find-Control($Root, [string]$Name) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-For($Read, [string]$Failure, [int]$Seconds = 15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Read
        if ($null -ne $value) { return $value }
        Start-Sleep -Milliseconds 200
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}

function Read-WatchIdentity([string]$ConfigPath) {
    $watchDevices = @(Get-PnpDevice -PresentOnly -Class Bluetooth | Where-Object FriendlyName -like '*Apple Watch*' |
        ForEach-Object { [regex]::Match($_.InstanceId, 'DEV_([0-9A-Fa-f]{12})').Groups[1].Value } |
        Where-Object { $_ } | Select-Object -Unique)
    if ($watchDevices.Count -ne 1) { throw '本测试要求 Windows 仅枚举到一块配对 Apple Watch。' }

    $data = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($ConfigPath), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($data, $false))
    try {
        if ($reader.ReadByte() -ne 1) { throw '无法识别 UnlockPC 配对记录版本。' }
        $id = $reader.ReadString()
        $pattern = $watchDevices[0] -replace '(..)(?!$)', '$1[:\-]?'
        if ($id -notmatch $pattern) { throw '保存的 IRK 与 Windows 枚举到的 Apple Watch 不一致。' }
        $irk = $reader.ReadBytes(16)
        if ($irk.Length -ne 16) { throw '配对记录中没有完整 IRK。' }
        @{ Address = $watchDevices[0]; Irk = $irk }
    } finally {
        $reader.Dispose()
        [Array]::Clear($data, 0, $data.Length)
    }
}

function Test-WatchAddress([byte[]]$Address, $Identity, [Security.Cryptography.Aes]$Aes) {
    if ([Convert]::ToHexString($Address) -eq $Identity.Address) { return $true }
    if (($Address[0] -band 0xC0) -ne 0x40) { return $false }
    $block = [byte[]]::new(16)
    [Array]::Copy($Address, 0, $block, 13, 3)
    $key = [byte[]]$Identity.Irk.Clone()
    try {
        foreach ($order in 1..2) {
            $Aes.Key = $key
            $hash = $Aes.EncryptEcb($block, [Security.Cryptography.PaddingMode]::None)
            if ($hash[13] -eq $Address[3] -and $hash[14] -eq $Address[4] -and $hash[15] -eq $Address[5]) {
                return $true
            }
            [Array]::Reverse($key)
        }
        return $false
    } finally { [Array]::Clear($key, 0, $key.Length) }
}

function Read-HciResult([string]$Trace, $Identity, [DateTimeOffset]$From, [DateTimeOffset]$Until) {
    $aes = [Security.Cryptography.Aes]::Create()
    $links = [Collections.Generic.List[object]]::new()
    $active = @{}
    $packetCount = 0
    try {
        foreach ($event in Get-WinEvent -Path $Trace -Oldest) {
            $time = [DateTimeOffset]$event.TimeCreated
            if ($event.ProviderName -ne 'Microsoft-Windows-BTH-BTHPORT' -or $event.Id -ne 402 -or
                $event.Properties.Count -lt 5 -or $time -lt $From -or $time -gt $Until) { continue }
            $packetCount++
            if ($event.Properties[2].Value -ne 2) { continue }
            [byte[]]$packet = $event.Properties[4].Value
            if ($packet.Length -lt 6) { continue }

            if ($packet[0] -eq 0x3E -and $packet[2] -in 0x01, 0x0A, 0x29 -and
                $packet[3] -eq 0 -and $packet.Length -ge 14) {
                $handle = [BitConverter]::ToUInt16($packet, 4)
                [void]$active.Remove($handle)
                $address = [byte[]]$packet[8..13]
                [Array]::Reverse($address)
                if (Test-WatchAddress $address $Identity $aes) {
                    $link = [pscustomobject]@{
                        Handle = '0x{0:X4}' -f $handle
                        Role = if ($packet[6] -eq 0) { 'Central' } else { 'Peripheral' }
                        ConnectedAt = $time
                        EncryptedAt = $null
                        EncryptionStatus = $null
                        DisconnectedAt = $null
                    }
                    $links.Add($link)
                    $active[$handle] = $link
                }
            } elseif ($packet[0] -in 0x05, 0x08, 0x59) {
                $handle = [BitConverter]::ToUInt16($packet, 3)
                $link = $active[$handle]
                if ($null -eq $link) { continue }
                if ($packet[0] -eq 0x05 -and $packet[2] -eq 0) {
                    $link.DisconnectedAt = $time
                    [void]$active.Remove($handle)
                } elseif ($packet[0] -ne 0x05) {
                    $link.EncryptionStatus = '0x{0:X2}' -f $packet[2]
                    if ($packet[2] -eq 0 -and $packet[5] -eq 1) { $link.EncryptedAt = $time }
                }
            }
        }
        @{ Packets = $packetCount; Links = $links.ToArray() }
    } finally { $aes.Dispose() }
}

$baseDir = Join-Path $env:LOCALAPPDATA 'UnlockPC'
$outDir = Join-Path $baseDir 'HciDiagnostics'
[void](New-Item -ItemType Directory -Path $outDir -Force)
$run = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $State, $PID
$trace = Join-Path $outDir "$run.etl"
$summaryPath = Join-Path $outDir "$run.json"
$sessionName = "UnlockPC-Maintain-$PID"
$identity = Read-WatchIdentity (Join-Path $baseDir 'automatic-unlock.dpapi')

try {
    $exe = (Resolve-Path -LiteralPath $Executable).Path
    Start-Process -FilePath $exe
    $app = Wait-For {
        Get-Process UnlockPC -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    } '无法找到 UnlockPC 窗口。'
    $root = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $advanced = Wait-For { Find-Control $root '高级选项' } '找不到高级选项。'
    $advanced.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $button = Wait-For {
        $control = Find-Control $root '测试蓝牙连接'
        if ($control -and $control.Current.IsEnabled) { $control }
    } '找不到可用的蓝牙连接测试按钮。'
    $log = Wait-For {
        Get-ChildItem (Join-Path $baseDir 'Logs') -Filter "bluetooth-*-$($app.Id).jsonl" | Select-Object -First 1
    } '找不到本次 UnlockPC 日志。'
    $pattern = '"watch_connection_test_(started|request|finished)"'
    $baseline = @(Select-String -LiteralPath $log.FullName -Pattern $pattern).Count

    logman start $sessionName -ets -o $trace -p 'Microsoft-Windows-BTH-BTHPORT' '0xffffffffffffffff' 5 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '无法启动 HCI 跟踪。' }
    try {
        Start-Sleep -Milliseconds 600
        $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $records = Wait-For {
            $new = @(Select-String -LiteralPath $log.FullName -Pattern $pattern | Select-Object -Skip $baseline |
                ForEach-Object { $_.Line | ConvertFrom-Json })
            if ($new.action -contains 'watch_connection_test_finished') { ,$new }
        } '连接测试没有返回结果。' 25
    } finally {
        logman stop $sessionName -ets | Out-Host
        if ($LASTEXITCODE -ne 0) { throw '无法关闭 HCI 跟踪并完成写入。' }
    }

    $start = $records | Where-Object action -eq 'watch_connection_test_started' | Select-Object -First 1
    $request = $records | Where-Object action -eq 'watch_connection_test_request' | Select-Object -First 1
    $finish = $records | Where-Object action -eq 'watch_connection_test_finished' | Select-Object -First 1
    if ($finish.details.method -ne 'maintain_connection') { throw '当前运行的应用不是 MaintainConnection 测试版本。' }
    $hci = Read-HciResult $trace $identity ([DateTimeOffset]($request.time ?? $start.time)) ([DateTimeOffset]$finish.time)
    $encrypted = @($hci.Links | Where-Object { $_.EncryptedAt })
    $verdict = if ($encrypted.Count) { 'EncryptedNewWatchLink' }
        elseif ($finish.details.alreadyConnected) { 'ExistingConnectionNoFreshEncryptionProof' }
        elseif ($hci.Links.Count) { 'WatchLinkNoEncryptionObserved' }
        elseif (!$request) { 'MaintainConnectionNotRequested' }
        elseif (!$hci.Packets) { 'NoHciPacketsCaptured' }
        else { 'NoNewWatchLinkObserved' }
    $summary = [pscustomobject]@{
        State = $State
        Verdict = $verdict
        RequestedAt = $request.time
        FinishedAt = $finish.time
        Windows = $finish.details
        HciPackets = $hci.Packets
        WatchLinks = $hci.Links
        AppLog = $log.FullName
        Trace = if ($KeepTrace) { $trace }
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $summary | Add-Member -NotePropertyName SummaryPath -NotePropertyValue $summaryPath -PassThru
} finally {
    [Array]::Clear($identity.Irk, 0, $identity.Irk.Length)
    if (!$KeepTrace -and (Test-Path -LiteralPath $trace)) { Remove-Item -LiteralPath $trace }
}
