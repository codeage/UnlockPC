using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace UnlockPC;

internal sealed record WindowsPairingEvent(string Endpoint, int EventId, long? RecordId, uint ProtocolError, uint AssociationError,
    DateTime? CreatedUtc)
{
    internal const string Query = "*[System[Provider[@Name='Microsoft-Windows-DeviceAssociationService'] and " +
        "(EventID=2500 or EventID=3501 or (EventID>=3504 and EventID<=3510))]]";

    internal static WindowsPairingEvent? Read(EventRecord record)
    {
        var xml = XElement.Parse(record.ToXml());
        XNamespace ns = xml.Name.Namespace;
        var fields = xml.Element(ns + "EventData")?.Elements(ns + "Data")
            .ToDictionary(field => (string?)field.Attribute("Name") ?? "", field => field.Value);
        if (fields is null || !fields.TryGetValue("AepId", out var id) ||
            !id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
            return null;

        if (record.Id == 2500 && !string.Equals(fields.GetValueOrDefault("IsPersisted"), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        uint.TryParse(fields.GetValueOrDefault("ProtocolError"), out uint protocol);
        uint.TryParse(fields.GetValueOrDefault("AssociationError"), out uint association);
        return new(id, record.Id, record.RecordId, protocol, association, record.TimeCreated?.ToUniversalTime());
    }

    internal bool Succeeded => EventId == 2500;

    internal bool MatchesId(string? id) => string.Equals(id, Endpoint, StringComparison.OrdinalIgnoreCase);

    internal string FailureMessage()
    {
        string stage = EventId switch
        {
            3504 => "初始化带外配对",
            3505 => "列举配对方式",
            3506 => "读取配对数据",
            3507 => "写入配对数据",
            3508 => "初始化设备关联",
            3509 => "选择配对方式",
            3510 => "完成设备关联",
            _ => "关联设备"
        };
        string reason = ProtocolError switch
        {
            0x8007048F => "设备未连接",
            _ when (ProtocolError >> 16) == 0x8007 => new Win32Exception((int)(ProtocolError & 0xffff)).Message,
            _ => ""
        };
        var details = new List<string>();
        if (ProtocolError != 0)
            details.Add($"{reason}（协议错误 0x{ProtocolError:X8}）");
        if (AssociationError != 0)
            details.Add($"设备关联错误 0x{AssociationError:X8}");
        if (details.Count == 0)
            details.Add("系统未提供详细错误码");
        return $"Windows 配对失败：{stage}时，{string.Join("；", details)}。";
    }
}
