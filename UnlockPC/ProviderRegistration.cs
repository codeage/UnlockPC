using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace UnlockPC;

internal static class ProviderRegistration
{
    public const string InstallArgument = "--register-credential-provider";
    private const string ProviderId = "{9E157DC9-764E-4DDF-A579-0B72CDBD8371}";
    private const string ProviderKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{ProviderId}";
    private const string ComKey = $@"SOFTWARE\Classes\CLSID\{ProviderId}\InprocServer32";
    private static string SourceDll => Path.Combine(AppContext.BaseDirectory, "UnlockPCCredentialProvider.dll");

    public static bool IsInstalled() => GetInstalledPath() is not null;

    private static string? GetInstalledPath()
    {
        using var provider = Registry.LocalMachine.OpenSubKey(ProviderKey);
        using var server = Registry.LocalMachine.OpenSubKey(ComKey);
        return provider is not null && server?.GetValue("") is string path && File.Exists(path) ? path : null;
    }

    public static async Task<bool> EnsureInstalledAsync()
    {
        if (IsInstalled())
            return true;

        if (!File.Exists(SourceDll))
            throw new InvalidOperationException("当前应用目录缺少 Windows 登录组件。请运行完整的 Release x64 构建。");

        var startInfo = new ProcessStartInfo(Environment.ProcessPath!, InstallArgument)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        try
        {
            using var installer = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动登录组件安装程序。");
            await installer.WaitForExitAsync();
            if (installer.ExitCode != 0 || !IsInstalled())
                throw new InvalidOperationException($"Windows 登录组件安装失败（0x{installer.ExitCode:X8}）。");

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    public static int Install()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Environment.Is64BitProcess || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return unchecked((int)0x80070005); // E_ACCESSDENIED

        try
        {
            if (GetInstalledPath() is { } currentDll)
            {
                using var installed = File.OpenRead(currentDll);
                using var source = File.OpenRead(SourceDll);
                if (SHA256.HashData(installed).AsSpan().SequenceEqual(SHA256.HashData(source)))
                    return 0;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles))
                throw new DirectoryNotFoundException();

            string directory = Path.Combine(programFiles, "UnlockPC Credential Provider");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装目录是重解析点。");

            // Use a new path so we never overwrite a component already loaded by LogonUI.
            string installedDll = Path.Combine(directory, $"UnlockPCCredentialProvider-{Guid.NewGuid():N}.dll");
            File.Copy(SourceDll, installedDll);

            using (var server = Registry.LocalMachine.CreateSubKey(ComKey))
            {
                server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
                server.SetValue("", installedDll, RegistryValueKind.String);
            }
            using var provider = Registry.LocalMachine.CreateSubKey(ProviderKey);
            provider.SetValue("", "UnlockPC Apple Watch", RegistryValueKind.String);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.HResult;
        }
    }
}
