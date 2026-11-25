using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Automation.Peers;

namespace UnlockPC;

public sealed partial class MainWindow
{
    internal async Task RunSignInOptionsSelfTestAsync()
    {
        Environment.ExitCode = 1;
        try
        {
            nint ownWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!await WaitForSelfTestAsync(() => OpenSignInOptionsLink.XamlRoot is not null))
                throw new InvalidOperationException("登录选项链接未加载。");

            AppWindow.Show(activateWindow: true);
            ActivateSelfTestWindow(ownWindow);
            if (!await WaitForSelfTestAsync(() => GetForegroundWindow() == ownWindow))
                throw new InvalidOperationException($"UnlockPC 测试窗口未获得前台焦点（当前前台进程 {ForegroundProcessId()}）。");

            bool clicked = false;
            OpenSignInOptionsLink.Click += (_, _) => clicked = true;
            new HyperlinkButtonAutomationPeer(OpenSignInOptionsLink).Invoke();
            if (!await WaitForSelfTestAsync(() => clicked && OpenSignInOptionsLink.IsEnabled) || SignInOptionsError.IsOpen)
                throw new InvalidOperationException("登录选项链接点击后未完成 Windows 启动请求。");

            Environment.ExitCode = 0;
            BluetoothLog.Write("ui_self_test_passed", new { test = "signin-options" });
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "signin-options", ex.Message, ex.HResult });
        }
        finally
        {
            Close();
        }
    }

    private static async Task<bool> WaitForSelfTestAsync(Func<bool> done, int timeoutSeconds = 6)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
                return false;
            await Task.Delay(50);
        }
        return true;
    }

    private static void ActivateSelfTestWindow(nint window)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == window || SetForegroundWindow(window))
            return;

        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread || !AttachThreadInput(currentThread, foregroundThread, true))
            return;

        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static uint ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        return processId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint thread, uint target, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
