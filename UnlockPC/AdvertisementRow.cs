using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace UnlockPC;

internal sealed record AdvertisementRow(long Sequence, bool IsMatch, string Heading, string Summary, string Payload,
    bool WindowsMatch = false, DateTimeOffset SeenAt = default, short Rssi = 0)
{
    private const int PayloadPreviewBytes = 48;

    public static AdvertisementRow Create(long sequence, BluetoothLEAdvertisementReceivedEventArgs args,
        bool windowsMatch, bool irkMatch, out AppleWatchFields watchFields)
    {
        var advertisement = args.Advertisement;
        string name = string.IsNullOrWhiteSpace(advertisement.LocalName) ? "未广播名称" : advertisement.LocalName;
        string address = args.IsAnonymous ? "匿名地址" : FormatAddress(args.BluetoothAddress);
        string addressType = args.IsAnonymous ? "" : $" · {AddressTypeLabel(args.BluetoothAddressType, args.BluetoothAddress)}";
        string signal = args.RawSignalStrengthInDBm is <= -127 or 127 ? "RSSI 无效" : $"RSSI {args.RawSignalStrengthInDBm} dBm";
        string match = (windowsMatch, irkMatch) switch
        {
            (true, true) => "Windows 地址 + IRK 匹配",
            (true, false) => "Windows 地址匹配",
            (false, true) => "IRK 匹配",
            _ => "未匹配"
        };

        string heading = $"{name} · {address}{addressType}";
        string summary = $"{args.Timestamp.ToLocalTime():HH:mm:ss.fff} · {signal} · {PacketTypeLabel(args.AdvertisementType)} · {match}";
        return new AdvertisementRow(sequence, windowsMatch || irkMatch, heading, summary,
            DescribePayload(advertisement, out watchFields), windowsMatch, args.Timestamp, args.RawSignalStrengthInDBm);
    }

    internal static AdvertisementRow FromService(long sequence, ulong address, BluetoothAddressType addressType,
        BluetoothLEAdvertisementType packetType, short rssi, DateTimeOffset seenAt, bool windowsMatch, bool irkMatch,
        short flags, ReadOnlySpan<byte> apple, out AppleWatchFields watchFields)
    {
        string addressText = address == 0 ? "匿名地址" : $"{FormatAddress(address)} · {AddressTypeLabel(addressType, address)}";
        string signal = rssi is <= -127 or 127 ? "RSSI 无效" : $"RSSI {rssi} dBm";
        string match = (windowsMatch, irkMatch) switch
        {
            (true, true) => "Windows 地址 + IRK 匹配",
            (true, false) => "Windows 地址匹配",
            (false, true) => "IRK 匹配",
            _ => "未匹配"
        };
        string summary = $"{seenAt.ToLocalTime():HH:mm:ss.fff} · {signal} · {PacketTypeLabel(packetType)} · {match}";
        var payload = new List<string>();
        if (flags >= 0)
            payload.Add($"标志 {FormatFlags((byte)flags)}");
        watchFields = default;
        if (!apple.IsEmpty)
        {
            string messages = DescribeAppleMessages(apple, out watchFields);
            payload.Add($"厂商 Apple (0x004C){messages} · {FormatPreview(apple.ToArray(), apple.Length)}");
        }
        return new(sequence, windowsMatch || irkMatch, addressText, summary, string.Join("\n", payload),
            windowsMatch, seenAt, rssi);
    }

    private static string DescribePayload(BluetoothLEAdvertisement advertisement, out AppleWatchFields watchFields)
    {
        watchFields = default;
        var fields = new List<string>();
        try
        {
            if (advertisement.Flags is { } flags)
                fields.Add($"标志 {FormatFlags((byte)flags)}");

            if (advertisement.ServiceUuids.Count > 0)
                fields.Add($"服务 UUID {string.Join("、", advertisement.ServiceUuids.Select(FormatServiceUuid))}");

            foreach (var manufacturer in advertisement.ManufacturerData)
            {
                using var reader = DataReader.FromBuffer(manufacturer.Data);
                if (manufacturer.CompanyId == 0x004C)
                {
                    var bytes = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(bytes);
                    string messages = DescribeAppleMessages(bytes, out AppleWatchFields decoded);
                    watchFields = new(decoded.Nearby ?? watchFields.Nearby, decoded.WristConfidence ?? watchFields.WristConfidence);
                    fields.Add($"厂商 Apple (0x004C){messages} · {FormatPreview(bytes, bytes.Length)}");
                }
                else
                {
                    fields.Add($"厂商 0x{manufacturer.CompanyId:X4} · {ReadPreview(reader)}");
                }
            }

            foreach (var section in advertisement.DataSections)
            {
                // WinRT already exposes flags, service lists, name, and manufacturer data above.
                if (section.DataType is >= 0x01 and <= 0x09 or 0xFF)
                    continue;
                fields.Add(DescribeSection(section));
            }
        }
        catch (COMException)
        {
            fields.Add("部分广播字段无法读取");
        }

        return fields.Count == 0 ? "无附加广播字段" : string.Join("\n", fields);
    }

    private static string DescribeSection(BluetoothLEAdvertisementDataSection section)
    {
        using var reader = DataReader.FromBuffer(section.Data);
        int uuidBytes = section.DataType switch
        {
            0x16 => 2,
            0x20 => 4,
            0x21 => 16,
            _ => 0
        };

        if (uuidBytes > 0)
        {
            if (reader.UnconsumedBufferLength < uuidBytes)
                return $"服务数据 (0x{section.DataType:X2}) · UUID 不完整 · {ReadPreview(reader)}";

            var uuid = new byte[uuidBytes];
            reader.ReadBytes(uuid);
            string service = uuidBytes switch
            {
                2 => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(uuid):X4}",
                4 => $"0x{BinaryPrimitives.ReadUInt32LittleEndian(uuid):X8}",
                _ => FormatLongServiceUuid(uuid)
            };
            return $"服务数据 {service} · {ReadPreview(reader)}";
        }

        if (section.DataType == 0x0A && reader.UnconsumedBufferLength == 1)
            return $"发射功率 {unchecked((sbyte)reader.ReadByte())} dBm";

        if (section.DataType == 0x19 && reader.UnconsumedBufferLength == 2)
        {
            reader.ByteOrder = ByteOrder.LittleEndian;
            return $"设备外观 0x{reader.ReadUInt16():X4}";
        }

        return $"广播字段 0x{section.DataType:X2} · {ReadPreview(reader)}";
    }

    internal static string DescribeAppleMessages(ReadOnlySpan<byte> data, out AppleWatchFields watchFields)
    {
        watchFields = default;
        AppleWatchFields decoded = default;
        var messages = new List<string>();
        while (data.Length >= 2)
        {
            byte type = data[0];
            int length = data[1];
            if (length > data.Length - 2)
                return "";

            string? name = type switch
            {
                0x02 when length == 21 => "iBeacon",
                0x0B => "Magic Switch",
                0x0C => "Handoff",
                0x0F => "Nearby Action",
                0x10 => "Nearby Info",
                _ => null
            };
            if (name is not null)
                messages.Add($"{name} (0x{type:X2}，{length} 字节)");
            decoded = decoded.Observe(type, data.Slice(2, length));
            data = data[(2 + length)..];
        }

        if (!data.IsEmpty)
            return "";

        watchFields = decoded;
        return messages.Count > 0 ? $" · {string.Join("、", messages)}" : "";
    }

    private static string ReadPreview(DataReader reader)
    {
        int length = (int)reader.UnconsumedBufferLength;
        if (length == 0)
            return "无负载";

        var bytes = new byte[Math.Min(length, PayloadPreviewBytes)];
        reader.ReadBytes(bytes);
        return FormatPreview(bytes, length);
    }

    private static string FormatPreview(byte[] bytes, int length)
    {
        if (length == 0)
            return "无负载";
        int previewLength = Math.Min(bytes.Length, PayloadPreviewBytes);
        string preview = BitConverter.ToString(bytes, 0, previewLength).Replace('-', ' ');
        return length > previewLength ? $"{preview} …（共 {length} 字节）" : preview;
    }

    private static string FormatFlags(byte flags)
    {
        var names = new List<string>(5);
        if ((flags & 0x01) != 0) names.Add("有限可发现");
        if ((flags & 0x02) != 0) names.Add("一般可发现");
        if ((flags & 0x04) != 0) names.Add("仅 BLE");
        if ((flags & 0x08) != 0) names.Add("控制器支持双模");
        if ((flags & 0x10) != 0) names.Add("主机支持双模");
        return names.Count > 0 ? $"0x{flags:X2}（{string.Join("、", names)}）" : $"0x{flags:X2}";
    }

    private static string FormatServiceUuid(Guid uuid)
    {
        string value = uuid.ToString();
        if (value.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal))
            return value.StartsWith("0000", StringComparison.Ordinal) ? "0x" + value[4..8].ToUpperInvariant() : "0x" + value[..8].ToUpperInvariant();
        return value;
    }

    private static string FormatLongServiceUuid(byte[] bytes)
    {
        Array.Reverse(bytes);
        return FormatServiceUuid(new Guid(bytes, bigEndian: true));
    }

    private static string FormatAddress(ulong address) =>
        $"{(byte)(address >> 40):X2}:{(byte)(address >> 32):X2}:{(byte)(address >> 24):X2}:{(byte)(address >> 16):X2}:{(byte)(address >> 8):X2}:{(byte)address:X2}";

    private static string AddressTypeLabel(BluetoothAddressType type, ulong address)
    {
        if (type == BluetoothAddressType.Public)
            return "公开地址";
        if (type != BluetoothAddressType.Random)
            return "地址类型未知";

        return (address >> 46) switch
        {
            0 => "不可解析私有地址",
            1 => "可解析私有地址 (RPA)",
            3 => "随机静态地址",
            _ => "随机地址"
        };
    }

    private static string PacketTypeLabel(BluetoothLEAdvertisementType type) => type switch
    {
        BluetoothLEAdvertisementType.ConnectableUndirected => "可连接广播",
        BluetoothLEAdvertisementType.ConnectableDirected => "定向可连接广播",
        BluetoothLEAdvertisementType.ScannableUndirected => "可扫描广播",
        BluetoothLEAdvertisementType.NonConnectableUndirected => "不可连接广播",
        BluetoothLEAdvertisementType.ScanResponse => "扫描响应",
        BluetoothLEAdvertisementType.Extended => "扩展广播",
        _ => $"广播类型 {(int)type}"
    };
}

internal sealed class AdvertisementHistory
{
    public const int Limit = 1_000;

    private readonly Queue<AdvertisementRow> _all = new(Limit);
    private readonly Queue<AdvertisementRow> _matches = new(Limit);
    private readonly object _gate = new();

    public void Clear()
    {
        lock (_gate)
        {
            _all.Clear();
            _matches.Clear();
        }
    }

    public void Add(AdvertisementRow row)
    {
        lock (_gate)
        {
            Append(_all, row);
            if (row.IsMatch)
                Append(_matches, row);
        }
    }

    public AdvertisementRow[] ReadNew(bool matchesOnly, long afterSequence)
    {
        lock (_gate)
        {
            var rows = matchesOnly ? _matches : _all;
            return rows.Where(row => row.Sequence > afterSequence).ToArray();
        }
    }

    private static void Append(Queue<AdvertisementRow> rows, AdvertisementRow row)
    {
        if (rows.Count == Limit)
            rows.Dequeue();
        rows.Enqueue(row);
    }
}
