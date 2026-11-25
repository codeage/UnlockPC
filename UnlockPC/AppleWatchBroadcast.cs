namespace UnlockPC;

internal readonly record struct AppleWatchFields(AppleNearbyInfo? Nearby, byte? WristConfidence)
{
    public AppleWatchFields Observe(byte type, ReadOnlySpan<byte> payload) => type switch
    {
        0x10 when payload.Length >= 2 => this with { Nearby = new AppleNearbyInfo(payload[0], payload[1], payload.Length) },
        0x0B when payload.Length == 3 => this with { WristConfidence = payload[2] },
        _ => this
    };
}

internal readonly record struct AppleNearbyInfo(byte ActivityByte, byte Flags, int PayloadLength)
{
    public int Action => ActivityByte & 0x0F;
    public bool HasFlag(int mask) => (Flags & mask) != 0;
    public int AuthenticationTagLength => HasFlag(0x02) ? 4 : 3;

    public string ActionDescription => Action switch
    {
        0x0 => "活动未知",
        0x1 => "停用活动报告",
        0x3 => "空闲",
        0x5 => "熄屏播放音频",
        0x7 => "屏幕亮起",
        0x9 => "亮屏播放视频",
        0xA => "在腕且已解锁",
        0xB => "近期有交互",
        0xD => "驾驶",
        0xE => "通话或 FaceTime",
        _ => "未知活动码"
    };
}

internal readonly record struct BroadcastObservation<T>(T Value, DateTimeOffset SeenAt, bool IrkMatch)
{
    public bool IsRecent(DateTimeOffset now)
    {
        var age = now - SeenAt;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(20);
    }

    public string FormatSource(DateTimeOffset now) => $"{SeenAt.ToLocalTime():HH:mm:ss} · {(IrkMatch ? "IRK" : "配对地址")}" +
        (IsRecent(now) ? "" : " · 已过期");
}

internal readonly record struct AppleWatchBroadcast(
    BroadcastObservation<AppleNearbyInfo>? Nearby, BroadcastObservation<byte>? MagicSwitch)
{
    public AppleWatchBroadcast Observe(AppleWatchFields fields, DateTimeOffset seenAt, bool irkMatch)
    {
        var nearby = Nearby;
        var magicSwitch = MagicSwitch;
        if (fields.Nearby is { } info && (nearby is null || seenAt >= nearby.Value.SeenAt))
            nearby = new(info, seenAt, irkMatch);
        if (fields.WristConfidence is { } confidence && (magicSwitch is null || seenAt >= magicSwitch.Value.SeenAt))
            magicSwitch = new(confidence, seenAt, irkMatch);
        return new(nearby, magicSwitch);
    }

    public (string Wrist, string Lock, string Screen) InferState(DateTimeOffset now)
    {
        AppleNearbyInfo? nearby = Nearby is { } info && info.IsRecent(now) ? info.Value : null;
        byte? wrist = MagicSwitch is { } magic && magic.IsRecent(now) ? magic.Value : null;
        bool reportsWornAndUnlocked = nearby?.Action == 0x0A;
        bool reportsLocked = nearby?.HasFlag(0x20) == true;
        bool reportsUnlocked = nearby?.HasFlag(0x80) == true;
        string wearing = wrist switch
        {
            0x03 when reportsWornAndUnlocked => "线索冲突",
            0x03 => "疑似离腕",
            0x3F => "疑似在腕",
            0x1F => "手腕检测关闭",
            _ => reportsWornAndUnlocked ? "疑似在腕" : "未知"
        };
        string lockState = (reportsLocked, reportsUnlocked) switch
        {
            (true, true) => "线索冲突",
            (true, false) => "疑似锁定",
            (false, true) => "疑似已解锁",
            _ => "未知"
        };
        string screen = nearby?.Action switch
        {
            0x05 => "疑似熄屏",
            0x07 or 0x09 => "疑似亮屏",
            _ => "未知"
        };
        return (wearing, lockState, screen);
    }

    public static string DescribeWristConfidence(byte value) => value switch
    {
        0x03 => "离腕",
        0x1F => "手腕检测关闭",
        0x3F => "在腕",
        _ => "未知值"
    };
}
