using System.Diagnostics.Eventing.Reader;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnlockPC;

internal sealed class DirectUnlock(byte[] protectedPassword, uint session) : IDisposable
{
    private const int PasswordCharacters = 256;
    internal const int PasswordBytes = PasswordCharacters * sizeof(char);
    private readonly object _passwordLock = new();
    private CancellationTokenSource? _request;
    private bool _disposed;
    private long _lastCredentialSent;

    internal long LastCredentialSent => Interlocked.Read(ref _lastCredentialSent);

    private static (string Username, uint Session) GetLocalAccount(WindowsIdentity identity)
    {
        string[] account = identity.Name.Split('\\', 2);
        if (account.Length != 2 || !account[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前版本仅支持本机 Windows 账户。");

        using var process = Process.GetCurrentProcess();
        uint session = (uint)process.SessionId;
        if (WTSGetActiveConsoleSessionId() != session)
            throw new InvalidOperationException("请从本机控制台开启此功能。");

        return (account[1], session);
    }

    public static void VerifyAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        GetLocalAccount(identity);
    }

    public static unsafe DirectUnlock? Prompt(nint window)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var (account, session) = GetLocalAccount(identity);

        var prompt = new CredentialUiInfo
        {
            Size = Marshal.SizeOf<CredentialUiInfo>(), Window = window, Caption = "启用 Apple Watch 自动解锁",
            Message = "输入当前 Windows 账户的密码。本次输入仅用于自检。"
        };
        var username = new StringBuilder(identity.Name, 514);
        byte[] password = new byte[PasswordBytes];
        bool keepProtectedPassword = false;
        try
        {
            fixed (byte* buffer = password)
            {
                const uint promptFlags = 0x2 | 0x80 | 0x40000 | 0x100000; // Do not persist, always show, generic, keep username.
                int result = CredUIPromptForCredentialsW(ref prompt, "UnlockPC", 0, 0, username, username.Capacity,
                    (nint)buffer, PasswordCharacters, out _, promptFlags);
                if (result == 1223) // ERROR_CANCELLED
                    return null;
                if (result != 0)
                    throw new Win32Exception(result);

                ValidateAndProtect(account, identity, buffer);
            }

            keepProtectedPassword = true;
            return new DirectUnlock(password, session);
        }
        finally
        {
            if (!keepProtectedPassword)
                CryptographicOperations.ZeroMemory(password);
        }
    }

    public static unsafe DirectUnlock Create(ReadOnlySpan<char> entered)
    {
        if (entered.IsEmpty || entered.Length >= PasswordCharacters || entered.Contains('\0'))
            throw new InvalidOperationException("请输入当前 Windows 账户的密码。");

        using var identity = WindowsIdentity.GetCurrent();
        var (account, session) = GetLocalAccount(identity);
        byte[] password = new byte[PasswordBytes];
        try
        {
            entered.CopyTo(MemoryMarshal.Cast<byte, char>(password.AsSpan()));
            fixed (byte* buffer = password)
                ValidateAndProtect(account, identity, buffer);
            return new DirectUnlock(password, session);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(password);
            throw;
        }
    }

    public void VerifyPassword()
    {
        byte[] password = CopyPassword();
        try { using var verified = Restore(password); }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    internal static bool WindowsReportedAuthenticationFailure(DateTime since)
    {
        var query = new EventLogQuery("Application", PathType.LogName,
            $"*[System[Provider[@Name='UnlockPC Credential Provider'] and EventID=100 and Level=3 and TimeCreated[@SystemTime >= '{since:O}']]]")
        {
            ReverseDirection = true
        };
        using var reader = new EventLogReader(query);
        while (reader.ReadEvent() is { } record)
        {
            using (record)
                if (record.Properties.FirstOrDefault()?.Value is string details &&
                    details.StartsWith("stage=authentication_result ", StringComparison.Ordinal))
                    return true;
        }
        return false;
    }

    public static bool IsPasswordRejected(Win32Exception error) => error.NativeErrorCode is 86 or 1326 or 1330 or 1907;

    public static unsafe DirectUnlock Restore(byte[] savedPassword)
    {
        if (savedPassword.Length != PasswordBytes)
            throw new InvalidDataException("已保存的凭据格式无效。");

        using var identity = WindowsIdentity.GetCurrent();
        var (account, session) = GetLocalAccount(identity);
        byte[] password = savedPassword.ToArray();
        try
        {
            fixed (byte* buffer = password)
                ValidateAndProtect(account, identity, buffer);
            return new DirectUnlock(password, session);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(password);
            throw;
        }
    }

    private static unsafe void ValidateAndProtect(string account, WindowsIdentity identity, byte* buffer)
    {
        var characters = new ReadOnlySpan<char>(buffer, PasswordCharacters);
        if (characters[0] == '\0' || !characters.Contains('\0'))
            throw new InvalidOperationException("需要当前 Windows 账户的密码。");
        if (!LogonUserW(account, ".", (nint)buffer, 2, 0, out var token)) // LOGON32_LOGON_INTERACTIVE
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        using (var verified = new WindowsIdentity(token.DangerousGetHandle()))
            if (!Equals(identity.User, verified.User))
                throw new InvalidOperationException("输入的密码未验证当前 Windows 账户。");

        if (!CryptProtectMemory((nint)buffer, PasswordBytes, 0)) // SAME_PROCESS
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public byte[] CopyPassword()
    {
        byte[] password;
        lock (_passwordLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            password = protectedPassword.ToArray();
        }
        try
        {
            Unprotect(password);
            return password;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(password);
            throw;
        }
    }

    public void Offer()
    {
        Cancel();
        if (_disposed)
            return;

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.Read, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create($"UnlockPC.DirectUnlock.{session}", PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, protectedPassword.Length, security);
        _request = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _ = ServeAsync(pipe, _request.Token);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint clientSession) || clientSession != session ||
                    WTSGetActiveConsoleSessionId() != session || cancellationToken.IsCancellationRequested)
                    return;

                byte[] password = CopyPassword();
                try
                {
                    await pipe.WriteAsync(password, cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastCredentialSent, Stopwatch.GetTimestamp());
                    BluetoothLog.Write("direct_unlock_credential_sent");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(password);
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
            {
                BluetoothLog.Write("direct_unlock_offer_closed");
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                BluetoothLog.Write("direct_unlock_pipe_error", new { ex.HResult });
            }
        }
    }

    private static unsafe void Unprotect(byte[] password)
    {
        fixed (byte* buffer = password)
            if (!CryptUnprotectMemory((nint)buffer, (uint)password.Length, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Cancel()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }

    public void Dispose()
    {
        Cancel();
        lock (_passwordLock)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(protectedPassword);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialUiInfo
    {
        public int Size;
        public nint Window;
        public string Message;
        public string Caption;
        public nint Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CredUIPromptForCredentialsW(ref CredentialUiInfo info, string target, nint reserved,
        int authError, StringBuilder username, int usernameSize, nint password, int passwordSize, out bool save, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool LogonUserW(string username, string domain, nint password, int logonType,
        int provider, out SafeAccessTokenHandle token);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectMemory(nint data, uint size, uint flags);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectMemory(nint data, uint size, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
