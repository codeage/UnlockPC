#Requires -Version 7.5
[CmdletBinding()]
param(
    [ValidateRange(5, 120)]
    [int]$CaptureSeconds = 5,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$unlockDirectory = Join-Path $env:LOCALAPPDATA 'UnlockPC'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $unlockDirectory ("WatchStateTests/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$logDirectory = Join-Path $unlockDirectory 'Logs'

$stages = @(
    [pscustomobject]@{ Name = '在腕、已解锁、亮屏'; Setup = '戴好手表并解锁，让表盘保持亮起。采集时尽量保持位置和朝向。' }
    [pscustomobject]@{ Name = '在腕、已解锁、息屏或常亮变暗'; Setup = '保持佩戴和解锁，等表盘熄灭或进入常亮变暗。采集时别抬腕或点屏。' }
    [pscustomobject]@{ Name = '在腕、已锁定'; Setup = '戴着手表锁定它，确认显示锁定或密码界面。采集时不要输入密码。' }
    [pscustomobject]@{ Name = '离腕、已锁定'; Setup = '取下手表，放在相近位置，并确认已经锁定。采集时保持离腕。' }
    [pscustomobject]@{ Name = '在腕、已解锁、与 iPhone 蓝牙断开、亮屏'; Setup = '戴回手表并解锁。可在 iPhone 的「设置 → 蓝牙」关闭蓝牙，确认手表与 iPhone 蓝牙断开，然后亮起表盘。此步可跳过。' }
    [pscustomobject]@{ Name = '恢复在腕、已解锁、亮屏'; Setup = '如果上一步关闭了 iPhone 蓝牙，请重新打开并等手表恢复连接。手表保持在腕、解锁，回到亮起的原表盘。' }
)

function Read-CapturedBroadcasts($capture, [DateTimeOffset]$until) {
    $stream = [IO.File]::Open($capture.Path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($stream.Length -lt $capture.Offset) { throw 'UnlockPC 日志文件已被截断。' }
        $null = $stream.Seek($capture.Offset, 'Begin')
        $reader = [IO.StreamReader]::new($stream)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                try { $entry = ConvertFrom-Json -InputObject $line -DateKind String } catch { continue }
                if ($entry.action -ne 'scan_watch_advertisement') { continue }
                $details = $entry.details
                $seen = [DateTimeOffset]$details.seenAt
                if ($seen -lt $capture.Start -or $seen -ge $until) { continue }

                $nearby = @()
                $magic = @()
                foreach ($field in ($details.payload -split '\r?\n')) {
                    if ($field -notmatch '^厂商 Apple \(0x004C\).* · ((?:[0-9A-F]{2} )*[0-9A-F]{2})(?: ….*)?$') { continue }
                    $bytes = @($Matches[1].Split(' ') | ForEach-Object { [Convert]::ToByte($_, 16) })
                    for ($index = 0; $index + 1 -lt $bytes.Count; $index += 2 + $length) {
                        $type = $bytes[$index]
                        $length = $bytes[$index + 1]
                        $data = $index + 2
                        if ($data + $length -gt $bytes.Count) { break }
                        if ($type -eq 0x10 -and $length -ge 2) {
                            $activity = $bytes[$data]
                            $flags = $bytes[$data + 1]
                            $nearby += [pscustomobject]@{
                                ActivityByte = '0x{0:X2}' -f $activity
                                Activity = '0x{0:X2}' -f ($activity -band 0x0f)
                                Flags = '0x{0:X2}' -f $flags
                                Bit20 = [bool]($flags -band 0x20)
                                Bit80 = [bool]($flags -band 0x80)
                            }
                        }
                        if ($type -eq 0x0b -and $length -eq 3) {
                            $magic += '0x{0:X2}' -f $bytes[$data + 2]
                        }
                    }
                }
                [pscustomobject]@{
                    SeenAt = $seen.ToString('o')
                    Address = $details.address
                    Rssi = $details.rssi
                    IrkMatch = $details.irkAddressMatch
                    WindowsMatch = $details.windowsAddressMatch
                    Nearby = $nearby
                    MagicSwitch = $magic
                }
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Format-Counts($values) {
    $groups = @($values | Group-Object -NoElement | Sort-Object Name)
    if ($groups.Count -eq 0) { return '无' }
    ($groups | ForEach-Object { "$($_.Name) × $($_.Count)" }) -join '、'
}

function Write-Results {
    ConvertTo-Json -InputObject @($state.Results.ToArray()) -Depth 8 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'samples.json') -Encoding utf8
    $lines = @(
        '# Apple Watch 广播状态对照'
        ''
        "每次确认后采集 $CaptureSeconds 秒。状态来自操作者确认。"
        ''
        '| 状态 | 广播包数（IRK 包数） | 活动码 | 数据标志 | 0x20 置位 | 0x80 置位 | Magic Switch | RSSI（dBm） |'
        '|---|---:|---|---|---:|---:|---|---|'
    )
    foreach ($result in $state.Results) {
        if ($result.Skipped) {
            $lines += "| $($result.Name) | 跳过 | — | — | — | — | — | — |"
            continue
        }
        $samples = $result.Samples
        $nearby = @($samples | ForEach-Object { $_.Nearby } | Where-Object { $null -ne $_ })
        $magic = @($samples | ForEach-Object { $_.MagicSwitch } | Where-Object { $null -ne $_ })
        $irk = @($samples | Where-Object IrkMatch).Count
        $bit20 = @($nearby | Where-Object Bit20).Count
        $bit80 = @($nearby | Where-Object Bit80).Count
        $rssi = @($samples | Where-Object { $_.Rssi -gt -127 -and $_.Rssi -ne 127 } | ForEach-Object { $_.Rssi })
        $range = if ($rssi.Count) {
            $stats = $rssi | Measure-Object -Minimum -Maximum
            "$($stats.Minimum) 至 $($stats.Maximum)"
        } else { '—' }
        $lines += "| $($result.Name) | $($samples.Count)（$irk） | $(Format-Counts $nearby.Activity) | $(Format-Counts $nearby.Flags) | $bit20/$($nearby.Count) | $bit80/$($nearby.Count) | $(Format-Counts $magic) | $range |"
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'comparison.md') -Value $lines -Encoding utf8
}

$state = @{
    Index = 0
    Capture = $null
    Results = [System.Collections.Generic.List[object]]::new()
}
$form = [System.Windows.Forms.Form]::new()
$form.Text = 'UnlockPC 手表状态测试'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = [System.Drawing.Size]::new(555, 220)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.Font = [System.Drawing.Font]::new('Microsoft YaHei UI', 10)
$heading = [System.Windows.Forms.Label]::new()
$heading.SetBounds(20, 20, 515, 30)
$heading.Font = [System.Drawing.Font]::new($form.Font, [System.Drawing.FontStyle]::Bold)
$instruction = [System.Windows.Forms.Label]::new()
$instruction.SetBounds(20, 56, 515, 61)
$status = [System.Windows.Forms.Label]::new()
$status.SetBounds(20, 124, 515, 42)
$confirm = [System.Windows.Forms.Button]::new()
$confirm.Text = '确定，开始采集'
$confirm.SetBounds(227, 176, 146, 32)
$skip = [System.Windows.Forms.Button]::new()
$skip.Text = '跳过此状态'
$skip.SetBounds(382, 176, 152, 32)
$form.Controls.AddRange(@($heading, $instruction, $status, $confirm, $skip))
$form.AcceptButton = $confirm
$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 250

function Show-Stage([string]$previous) {
    $confirm.Enabled = $true
    $skip.Enabled = $true
    if ($state.Index -ge $stages.Count) {
        $heading.Text = '测试完成'
        $instruction.Text = '各状态的字段对比和逐包样本已保存。'
        $status.Text = $OutputDirectory
        $confirm.Text = '关闭'
        $skip.Visible = $false
        return
    }
    $stage = $stages[$state.Index]
    $heading.Text = "$($state.Index + 1)/$($stages.Count)  $($stage.Name)"
    $instruction.Text = $stage.Setup
    $status.Text = $previous
}

$confirm.Add_Click({
    if ($state.Index -ge $stages.Count) { $form.Close(); return }
    try {
        $process = Get-Process UnlockPC -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId) |
            Sort-Object StartTime -Descending | Select-Object -First 1
        if (-not $process) { throw '请先启动 UnlockPC，并确认正在检测配对手表。' }
        $log = Get-ChildItem -LiteralPath $logDirectory -Filter "*-$($process.Id).jsonl" -File |
            Select-Object -First 1
        if (-not $log) { throw '当前 UnlockPC 实例还未写入蓝牙日志。' }
        $state.Capture = @{
            Path = $log.FullName
            Offset = $log.Length
            Process = $process
            Start = [DateTimeOffset]::Now
            Clock = [Diagnostics.Stopwatch]::StartNew()
        }
        $confirm.Enabled = $false
        $skip.Enabled = $false
        $status.Text = "正在采集，保持当前状态 $CaptureSeconds 秒。"
        $timer.Start()
    } catch { $status.Text = $_.Exception.Message }
})

$skip.Add_Click({
    try {
        $state.Results.Add([pscustomobject]@{ Name = $stages[$state.Index].Name; Skipped = $true })
        try { Write-Results } catch { $state.Results.RemoveAt($state.Results.Count - 1); throw }
        $state.Index++
        Show-Stage '上一步已跳过。'
    } catch { $status.Text = $_.Exception.Message }
})

$timer.Add_Tick({
    $capture = $state.Capture
    $remaining = [Math]::Ceiling($CaptureSeconds - $capture.Clock.Elapsed.TotalSeconds)
    if ($remaining -gt 0) {
        $status.Text = "正在采集，剩余 $remaining 秒。"
        return
    }
    $timer.Stop()
    try {
        if ($capture.Process.HasExited) { throw '采集时 UnlockPC 已退出或重启，请重试此状态。' }
        $until = $capture.Start.AddSeconds($CaptureSeconds)
        $samples = @(Read-CapturedBroadcasts $capture $until)
        $state.Results.Add([pscustomobject]@{
            Name = $stages[$state.Index].Name
            Skipped = $false
            Start = $capture.Start.ToString('o')
            End = $until.ToString('o')
            SourceLog = $capture.Path
            Samples = $samples
        })
        try { Write-Results } catch { $state.Results.RemoveAt($state.Results.Count - 1); throw }
        $state.Index++
        Show-Stage "上一步已记录 $($samples.Count) 包。设置好当前状态后点「确定」。"
    } catch {
        Show-Stage $_.Exception.Message
    } finally {
        $capture.Process.Dispose()
        $state.Capture = $null
    }
})

try {
    Show-Stage '将手表保持在电脑附近。准备好后点「确定」。'
    $null = $form.ShowDialog()
} finally {
    $timer.Dispose()
    if ($state.Capture) {
        $state.Capture.Process.Dispose()
    }
    $form.Dispose()
}
