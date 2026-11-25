using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace UnlockPC;

internal sealed record PairedWatch(string Id, string Name, ulong Address, bool? IsConnected)
{
    internal const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    internal const string ConnectionProperty = "System.Devices.Aep.IsConnected";
    internal static readonly string[] Properties = [AddressProperty, ConnectionProperty];
    internal static string Selector => BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);

    public bool HasWatchName => Name.Contains("Apple Watch", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;

    public static async Task<PairedWatch[]> ListAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(Selector, Properties, DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        return devices.Select(FromDevice).OfType<PairedWatch>().OrderByDescending(watch => watch.HasWatchName)
            .ThenBy(watch => watch.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static PairedWatch? FindPairingResult(
        PairedWatch[]? before, PairedWatch[] current, IEnumerable<string> observedPeers)
    {
        if (before is null)
            return null;

        // A connection or encrypted GATT read can also come from a previously paired device.
        var newBonds = current.Where(watch => !before.Any(prior =>
            string.Equals(prior.Id, watch.Id, StringComparison.OrdinalIgnoreCase) || prior.Address == watch.Address)).ToArray();
        var observedBonds = newBonds.Where(watch => observedPeers.Contains(watch.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (observedBonds.Length == 1)
            return observedBonds[0];

        var watches = newBonds.Where(watch => watch.HasWatchName).ToArray();
        return watches.Length == 1 ? watches[0] : null;
    }

    public static PairedWatch? FromDevice(DeviceInformation? device)
    {
        if (device is null || !device.Pairing.IsPaired || !TryReadAddress(device.Properties, out ulong address))
            return null;

        return new PairedWatch(device.Id, string.IsNullOrWhiteSpace(device.Name) ? "未命名蓝牙设备" : device.Name,
            address, ReadConnection(device.Properties));
    }

    internal static bool TryReadAddress(IReadOnlyDictionary<string, object> properties, out ulong address)
    {
        address = 0;
        return properties.TryGetValue(AddressProperty, out var value) && value is string text &&
            ulong.TryParse(text.Replace(":", "").Replace("-", ""), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address) &&
            address is > 0 and <= 0xFFFFFFFFFFFF;
    }

    internal static bool? ReadConnection(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(ConnectionProperty, out var value) && value is bool connected ? connected : null;
}

