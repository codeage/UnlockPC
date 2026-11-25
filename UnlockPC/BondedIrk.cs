using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Windows.Devices.Bluetooth;

namespace UnlockPC;

internal static class BondedIrk
{
    public static async Task<BondedIrkResult> ReadAsync(ulong deviceAddress)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
            return new BondedIrkResult(null, "Windows 没有可用的蓝牙适配器。");

        string requestId = Guid.NewGuid().ToString("N");
        string pipeName = $"UnlockPC-Irk-{requestId}";
        string taskName = $"UnlockPC-ReadBond-{requestId}";
        string registryKey = $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\{adapter.BluetoothAddress:x12}\{deviceAddress:x12}";

        using var user = WindowsIdentity.GetCurrent();
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(user.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64, 0, pipeSecurity);

        string reader = $$"""
            $ErrorActionPreference = 'Stop'
            $status = [byte]3
            $key = $null
            try {
                $bond = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('{{registryKey}}')
                try {
                    if ($null -ne $bond) { $key = $bond.GetValue('IRK') }
                } finally {
                    if ($null -ne $bond) { $bond.Dispose() }
                }
                $status = if ($key -is [byte[]] -and $key.Length -eq 16) { [byte]1 } else { [byte]2 }
            } catch {}
            $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', '{{pipeName}}', [System.IO.Pipes.PipeDirection]::Out)
            try {
                $client.Connect(5000)
                $client.WriteByte($status)
                if ($status -eq 1) { $client.Write($key, 0, 16) }
                $client.Flush()
            } finally {
                $client.Dispose()
                if ($key -is [byte[]]) { [Array]::Clear($key, 0, $key.Length) }
            }
            """;

        // The task runs only a fixed registry lookup in Windows PowerShell. The IRK travels through the pipe.
        string elevated = $$"""
            $ErrorActionPreference = 'Stop'
            $taskName = '{{taskName}}'
            $registered = $false
            try {
                $action = New-ScheduledTaskAction -Execute "$PSHOME\powershell.exe" -Argument '-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {{Encode(reader)}}'
                $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount
                $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
                Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
                $registered = $true
                $since = (Get-ScheduledTaskInfo -TaskName $taskName).LastRunTime
                Start-ScheduledTask -TaskName $taskName
                $clock = [Diagnostics.Stopwatch]::StartNew()
                do {
                    Start-Sleep -Milliseconds 250
                    $task = Get-ScheduledTask -TaskName $taskName
                    $info = Get-ScheduledTaskInfo -InputObject $task
                    if ($info.LastRunTime -ne $since -and $task.State -eq 'Ready') { break }
                } while ($clock.Elapsed.TotalSeconds -lt 30)
            } finally {
                if ($registered) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
            }
            """;

        using Process? broker = await Task.Run(() => LaunchElevated(elevated));
        if (broker is null)
            return new BondedIrkResult(null, "已取消管理员确认。");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(38));
        var receive = ReceiveAsync(pipe, timeout.Token);
        await Task.WhenAny(receive, broker.WaitForExitAsync(timeout.Token));
        if (!receive.IsCompleted)
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            return await receive;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return new BondedIrkResult(null, "Windows 配对密钥读取未完成，请检查任务计划程序后重试。");
        }
    }

    private static Process? LaunchElevated(string script)
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {Encode(script)}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }

    private static async Task<BondedIrkResult> ReceiveAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token);
        byte[] status = new byte[1];
        await pipe.ReadExactlyAsync(status, token);
        if (status[0] == 1)
        {
            byte[] key = new byte[16];
            try
            {
                await pipe.ReadExactlyAsync(key, token);
                return new BondedIrkResult(key, null);
            }
            catch
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }

        return new BondedIrkResult(null, status[0] == 2
            ? "Windows 没有找到此设备的 IRK。请确认手表已通过当前适配器完成配对。"
            : "Windows 无法读取此设备的配对密钥。");
    }

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}

internal readonly record struct BondedIrkResult(byte[]? Key, string? Error);
