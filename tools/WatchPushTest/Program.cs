using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || !Regex.IsMatch(args[1], @"\A[A-Z0-9]{10}\z"))
        {
            Console.Error.WriteLine("Usage: WatchPushTest <APNs .p8 file> <key ID> <Watch device token file> <new JSONL log>");
            return 1;
        }

        try
        {
            var deviceToken = File.ReadAllText(args[2]).Trim();
            if (deviceToken.Length == 0 || deviceToken.Length % 2 != 0 || !deviceToken.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid Watch device token.");

            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(File.ReadAllText(args[0]));
            if (signingKey.KeySize != 256)
                throw new InvalidDataException("APNs requires a P-256 signing key.");

            var jwt = CreateProviderToken(signingKey, args[1]);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            using var stream = File.Open(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var log = new StreamWriter(stream) { AutoFlush = true };
            using var watcher = new UnlockWatcher(client, jwt, deviceToken, log);
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

    private static string CreateProviderToken(ECDsa key, string keyId)
    {
        static string Encode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "2NEMTTKZVN",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));
        var unsigned = $"{header}.{claims}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{unsigned}.{Encode(signature)}";
    }

    private sealed class UnlockWatcher : NativeWindow, IDisposable
    {
        private const int SessionChangeMessage = 0x02B1;
        private const int SessionUnlock = 8;
        private const int SampleCount = 5;
        private readonly int sessionId;
        private readonly HttpClient client;
        private readonly string jwt;
        private readonly Uri endpoint;
        private readonly StreamWriter log;
        private readonly System.Windows.Forms.Timer expiry = new() { Interval = 15 * 60 * 1000 };
        private int completed;
        private bool expired;

        public int ReceivedCount { get; private set; }

        public UnlockWatcher(HttpClient client, string jwt, string deviceToken, StreamWriter log)
        {
            using var process = Process.GetCurrentProcess();
            sessionId = process.SessionId;
            this.client = client;
            this.jwt = jwt;
            this.log = log;
            endpoint = new Uri($"https://api.push.apple.com/3/device/{deviceToken}");
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
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = JsonContent.Create(new
                    {
                        aps = new
                        {
                            alert = new { title = "UnlockPC", body = $"Windows 解锁测试 {sequence}/{SampleCount}" },
                            sound = "default"
                        },
                        unlockpcTestEvent = eventId,
                        unlockUtc
                    })
                };
                request.Headers.TryAddWithoutValidation("authorization", $"bearer {jwt}");
                request.Headers.Add("apns-topic", "com.bytereset.unlockpc.watchkitapp");
                request.Headers.Add("apns-push-type", "alert");
                request.Headers.Add("apns-priority", "10");
                request.Headers.Add("apns-expiration", "0");
                request.Headers.Add("apns-id", eventId.ToString());
                using var response = await client.SendAsync(request);
                var responseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string? reason = null;
                if (!response.IsSuccessStatusCode)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (body.RootElement.TryGetProperty("reason", out var value))
                        reason = value.GetString();
                }
                Record(new { stage = "apns", sequence, eventId, responseMs, status = (int)response.StatusCode, reason });
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
