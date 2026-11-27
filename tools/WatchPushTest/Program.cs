using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UnlockPC;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: WatchPushTest <APNs .p8 file> <key ID> <Watch device token file> <new JSONL log>");
            return 1;
        }

        try
        {
            using var sender = new WatchPushSender(args[0], args[1], args[2]);
            using var stream = File.Open(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var log = new StreamWriter(stream) { AutoFlush = true };
            using var watcher = new UnlockWatcher(sender, log);
            Console.WriteLine("Listening for the next five local session unlocks. Expires after 15 minutes.");
            Application.Run();
            return watcher.ReceivedCount == 5 ? 0 : 2;
        }
        catch (Exception error)
        {
            // Exception messages from HTTP can contain the private device token in the URL.
            Console.Error.WriteLine($"Could not run unlock test: {error.GetType().Name}.");
            return 1;
        }
    }

    private sealed class UnlockWatcher : NativeWindow, IDisposable
    {
        private const int SessionChangeMessage = 0x02B1;
        private const int SessionUnlock = 8;
        private const int SampleCount = 5;
        private readonly int sessionId;
        private readonly WatchPushSender sender;
        private readonly StreamWriter log;
        private readonly System.Windows.Forms.Timer expiry = new() { Interval = 15 * 60 * 1000 };
        private int completed;
        private bool expired;

        public int ReceivedCount { get; private set; }

        public UnlockWatcher(WatchPushSender sender, StreamWriter log)
        {
            using var process = Process.GetCurrentProcess();
            sessionId = process.SessionId;
            this.sender = sender;
            this.log = log;
            CreateHandle(new CreateParams());
            if (!WTSRegisterSessionNotification(Handle, 0))
            {
                var error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new System.ComponentModel.Win32Exception(error);
            }

            expiry.Tick += (_, _) =>
            {
                expiry.Stop();
                expired = true;
                if (completed == ReceivedCount)
                    Application.ExitThread();
            };
            expiry.Start();
        }

        protected override void WndProc(ref Message message)
        {
            if (!expired && ReceivedCount < SampleCount &&
                message.Msg == SessionChangeMessage && message.WParam.ToInt64() == SessionUnlock &&
                message.LParam.ToInt64() == sessionId)
            {
                // Take the start timestamp as soon as Windows reports the unlock.
                var unlockUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var started = Stopwatch.GetTimestamp();
                var sequence = ++ReceivedCount;
                var eventId = Guid.NewGuid();
                Record(new { stage = "unlock", sequence, eventId, unlockUtc });
                _ = SendAsync(sequence, eventId, unlockUtc, started);
            }
            base.WndProc(ref message);
        }

        private async Task SendAsync(int sequence, Guid eventId, long unlockUtc, long started)
        {
            try
            {
                var response = await sender.SendAsync(eventId, unlockUtc, started, $"Windows 解锁测试 {sequence}/{SampleCount}");
                Record(new { stage = "apns", sequence, eventId, responseMs = response.ResponseMs, status = response.Status, reason = response.Reason });
            }
            catch (Exception error)
            {
                Record(new { stage = "error", sequence, eventId, error = error.GetType().Name });
            }
            finally
            {
                completed++;
                if (completed == SampleCount || (expired && completed == ReceivedCount))
                    Application.ExitThread();
            }
        }

        private void Record<T>(T entry)
        {
            var line = JsonSerializer.Serialize(entry);
            log.WriteLine(line);
            Console.WriteLine(line);
        }

        public void Dispose()
        {
            expiry.Dispose();
            WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);

        [DllImport("wtsapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    }
}
