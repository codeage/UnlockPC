$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'IrkService\IrkService.cpp'
$output = Join-Path $root 'IrkService\bin\x64\Release'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $locator -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw '找不到 Visual Studio x64 C++ 工具链。' }
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$command = '"{0}" && cl.exe /nologo /I"%WindowsSdkDir%Include\%WindowsSDKVersion%cppwinrt" /std:c++20 /W4 /external:anglebrackets /external:W0 /WX /O2 /MT /GS /EHsc /guard:cf "{1}" /link /WX /guard:cf /Brepro /OUT:UnlockPCIrk.exe Advapi32.lib Bcrypt.lib Wtsapi32.lib Userenv.lib WindowsApp.lib' -f $vcvars, $source
$start = [Diagnostics.ProcessStartInfo]::new($env:ComSpec, ('/d /s /c "{0}"' -f $command))
$start.WorkingDirectory = $output
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$compiler = [Diagnostics.Process]::Start($start)
try {
    $outputTask = $compiler.StandardOutput.ReadToEndAsync()
    $errorTask = $compiler.StandardError.ReadToEndAsync()
    $compiler.WaitForExit()
    if ($outputTask.Result) { Write-Host $outputTask.Result.TrimEnd() }
    if ($errorTask.Result) { Write-Host $errorTask.Result.TrimEnd() }
    if ($compiler.ExitCode -ne 0) { throw "IRK 服务编译失败（$($compiler.ExitCode)）。" }
}
finally { $compiler.Dispose() }
