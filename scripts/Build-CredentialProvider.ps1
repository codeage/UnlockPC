[CmdletBinding()]
param([string]$VisualStudioPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'CredentialProvider\Provider.cpp'
$output = Join-Path $root 'CredentialProvider\bin\x64\Release'
if (-not $VisualStudioPath) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $locator -latest -prerelease -products '*' -property installationPath
}
if (-not $VisualStudioPath) {
    throw '找不到 Visual Studio。请安装 C++ 桌面开发组件，或使用 -VisualStudioPath 指定路径。'
}
$environment = Join-Path $VisualStudioPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $environment)) {
    throw "找不到 x64 C++ 工具链。$environment"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location -LiteralPath $output
try {
    $build = "`"$environment`" && cl.exe /nologo /std:c++17 /W4 /WX /O2 /MT /GS /guard:cf /LD /DUNICODE /D_UNICODE `"$source`" /link /WX /guard:cf /OUT:UnlockPCCredentialProvider.dll /EXPORT:DllGetClassObject,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE Ole32.lib Advapi32.lib Credui.lib Secur32.lib Shlwapi.lib Wtsapi32.lib User32.lib Uuid.lib"
    $start = [Diagnostics.ProcessStartInfo]::new($env:ComSpec, "/d /s /c `"$build`"")
    $start.WorkingDirectory = $output
    $start.UseShellExecute = $false
    $compiler = [Diagnostics.Process]::Start($start)
    try {
        $compiler.WaitForExit()
        if ($compiler.ExitCode -ne 0) { throw "凭据提供程序构建失败（$($compiler.ExitCode)）。" }
    }
    finally { $compiler.Dispose() }
    Get-Item -LiteralPath (Join-Path $output 'UnlockPCCredentialProvider.dll')
}
finally {
    Pop-Location
}
