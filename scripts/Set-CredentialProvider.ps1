[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][ValidateSet('Install', 'Uninstall')][string]$Action)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位 PowerShell。' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行。'
}

$id = '{9E157DC9-764E-4DDF-A579-0B72CDBD8371}'
$provider = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$id"
$com = "HKLM:\SOFTWARE\Classes\CLSID\$id"
if ($Action -eq 'Uninstall') {
    if ($PSCmdlet.ShouldProcess('UnlockPC Apple Watch', '注销 Windows 登录组件')) {
        if (Test-Path -LiteralPath $provider) { Remove-Item -LiteralPath $provider -Force }
        if (Test-Path -LiteralPath "$com\InprocServer32") { Remove-Item -LiteralPath "$com\InprocServer32" -Force }
        if (Test-Path -LiteralPath $com) { Remove-Item -LiteralPath $com -Force }
    }
    return
}

$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'UnlockPC\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64\UnlockPC.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '请先构建 UnlockPC Release x64。' }
if (-not $PSCmdlet.ShouldProcess('UnlockPC Apple Watch', '安装并注册 Windows 登录组件')) { return }
$installer = Start-Process -FilePath $exe -ArgumentList '--register-credential-provider' -WindowStyle Hidden -PassThru -Wait
try {
    if ($installer.ExitCode -ne 0) { throw ('登录组件安装失败（0x{0:X8}）。' -f $installer.ExitCode) }
    (Get-Item -LiteralPath "$com\InprocServer32").GetValue('')
}
finally {
    $installer.Dispose()
}
