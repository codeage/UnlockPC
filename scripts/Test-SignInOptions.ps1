[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'UnlockPC\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64\UnlockPC.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '请先构建 UnlockPC Release x64。' }

$test = Start-Process -FilePath $exe -ArgumentList '--ui-self-test', 'signin-options' -PassThru
try {
    if (-not $test.WaitForExit(20000)) {
        Stop-Process -InputObject $test
        throw '登录选项界面自检超时。'
    }
    if ($test.ExitCode -ne 0) {
        $logDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UnlockPC\Logs'
        $log = Get-ChildItem -LiteralPath $logDirectory -Filter "*-$($test.Id).jsonl" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($log) { Get-Content -LiteralPath $log.FullName }
        throw "UnlockPC 界面自检失败（退出码 $($test.ExitCode)）。"
    }
}
finally {
    $test.Dispose()
}

# Confirm the actual Windows Settings page using a locale-independent control ID.
$faceOption = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SystemSettings_Users_EnrollmentFace_EntityItem')
$root = [System.Windows.Automation.AutomationElement]::RootElement
$children = [System.Windows.Automation.TreeScope]::Children
$descendants = [System.Windows.Automation.TreeScope]::Descendants
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $settings = @(Get-Process SystemSettings, ApplicationFrameHost -ErrorAction SilentlyContinue)
    foreach ($process in $settings) {
        $owner = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        foreach ($window in $root.FindAll($children, $owner)) {
            try {
                if ($window.FindFirst($descendants, $faceOption)) {
                    Write-Output 'PASS UnlockPC click opened Windows Settings > Accounts > Sign-in options.'
                    return
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
                # Windows can replace its application frame while navigating.
            }
        }
    }
    Start-Sleep -Milliseconds 100
} while ($timer.Elapsed -lt [TimeSpan]::FromSeconds(6))
throw '点击后未在 Windows 设置中找到「登录选项」页面。'
