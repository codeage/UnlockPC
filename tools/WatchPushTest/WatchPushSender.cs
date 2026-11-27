using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnlockPC;

internal sealed class WatchPushSender : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly ECDsa _signingKey;
    private readonly string _keyId;
    private string? _jwt;
    private long _issuedAt;

    public WatchPushSender(string keyPath, string keyId, string deviceTokenPath)
    {
        if (keyId.Length != 10 || !keyId.All(static character => character is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            throw new InvalidDataException("Invalid APNs Key ID.");

        var deviceToken = File.ReadAllText(deviceTokenPath).Trim();
        if (deviceToken.Length == 0 || deviceToken.Length % 2 != 0 || !deviceToken.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid Watch device token.");

        _endpoint = new Uri($"https://api.push.apple.com/3/device/{deviceToken}");
        _keyId = keyId;
        _signingKey = ECDsa.Create();
        try
        {
            _signingKey.ImportFromPem(File.ReadAllText(keyPath));
            if (_signingKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("APNs requires a P-256 signing key.");
        }
        catch
        {
            _signingKey.Dispose();
            throw;
        }

        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    public async Task<PushResponse> SendAsync(Guid eventId, long eventUtc, long started, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(new
            {
                aps = new
                {
                    alert = new { title = "UnlockPC", body = message },
                    sound = "default"
                },
                unlockpcTestEvent = eventId,
                eventUtc
            })
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {GetProviderToken()}");
        request.Headers.Add("apns-topic", "com.bytereset.unlockpc.watchkitapp");
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", "10");
        request.Headers.Add("apns-expiration", "0");
        request.Headers.Add("apns-id", eventId.ToString());

        using var response = await _client.SendAsync(request).ConfigureAwait(false);
        var responseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string? reason = null;
        if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (body.RootElement.TryGetProperty("reason", out var value))
                reason = value.GetString();
        }
        return new PushResponse((int)response.StatusCode, responseMs, reason);
    }

    private string GetProviderToken()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_jwt is not null && now >= _issuedAt && now - _issuedAt < 40 * 60)
            return _jwt;

        static string Encode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _keyId }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = "2NEMTTKZVN", iat = now }));
        var unsigned = $"{header}.{claims}";
        var signature = _signingKey.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        _issuedAt = now;
        return _jwt = $"{unsigned}.{Encode(signature)}";
    }

    public void Dispose()
    {
        _client.Dispose();
        _signingKey.Dispose();
    }

    internal readonly record struct PushResponse(int Status, double ResponseMs, string? Reason);
}
