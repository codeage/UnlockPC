using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

var serviceId = new Guid("8A29C90B-A244-4BC2-9DB9-B7CC794471E3");
var characteristicId = new Guid("C5AEE773-8378-4A75-A218-21505DC61F73");

var adapter = await BluetoothAdapter.GetDefaultAsync();
if (adapter is null || !adapter.IsPeripheralRoleSupported)
{
    Console.Error.WriteLine("No Bluetooth adapter with BLE peripheral support is available.");
    return 1;
}

var serviceResult = await GattServiceProvider.CreateAsync(serviceId);
if (serviceResult.Error != BluetoothError.Success)
{
    Console.Error.WriteLine($"Cannot create the test GATT service: {serviceResult.Error}");
    return 1;
}

var provider = serviceResult.ServiceProvider;
var characteristicResult = await provider.Service.CreateCharacteristicAsync(
    characteristicId,
    new GattLocalCharacteristicParameters
    {
        CharacteristicProperties = GattCharacteristicProperties.Notify,
        UserDescription = "UnlockPC test alert"
    });
if (characteristicResult.Error != BluetoothError.Success)
{
    Console.Error.WriteLine($"Cannot create the test Notify characteristic: {characteristicResult.Error}");
    return 1;
}

var characteristic = characteristicResult.Characteristic;
var code = string.Create(20, 0, (digits, _) =>
{
    for (var index = 0; index < digits.Length; index++)
    {
        digits[index] = (char)('0' + RandomNumberGenerator.GetInt32(10));
    }
});
var key = Encoding.ASCII.GetBytes(code);
string? authorizedDeviceId = null;
provider.AdvertisementStatusChanged += (_, eventArgs) =>
    Console.WriteLine($"GATT advertising: {eventArgs.Status}");
characteristic.SubscribedClientsChanged += (sender, _) =>
    Console.WriteLine($"Current subscribers: {sender.SubscribedClients.Count}");

try
{
    provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
    {
        IsConnectable = true,
        IsDiscoverable = true
    });
    var groups = Enumerable.Range(0, 5).Select(index => code.Substring(index * 4, 4));
    Console.WriteLine($"Test code for this run: {string.Join(" ", groups)}");
    Console.WriteLine("A = authorize the sole subscriber, Enter = send, D = send after 15 seconds, Q = quit.");
    while (Console.ReadLine() is { } line)
    {
        var command = line.Trim().ToUpperInvariant();
        switch (command)
        {
            case "Q":
                return 0;
            case "A":
                var clients = characteristic.SubscribedClients.ToArray();
                authorizedDeviceId = clients.Length == 1 ? clients[0].Session.DeviceId.Id : null;
                Console.WriteLine(authorizedDeviceId is null
                    ? "Authorization cleared. Connect only the Apple Watch under test."
                    : "Current subscriber authorized for this test run.");
                break;
            case "":
            case "D":
                if (command == "D")
                {
                    Console.WriteLine("Sending once in 15 seconds. Lock the PC now to check the GATT service.");
                    await Task.Delay(TimeSpan.FromSeconds(15));
                }
                var subscriber = characteristic.SubscribedClients.FirstOrDefault(client =>
                    client.Session.DeviceId.Id == authorizedDeviceId);
                if (subscriber is null)
                {
                    Console.WriteLine("No authorized subscriber is currently connected.");
                    break;
                }
                await NotifyAsync(characteristic, subscriber, key);
                break;
        }
    }
    return 0;
}
finally
{
    provider.StopAdvertising();
    CryptographicOperations.ZeroMemory(key);
}

static async Task NotifyAsync(GattLocalCharacteristic characteristic, GattSubscribedClient subscriber, byte[] key)
{
    const int headerSize = 22;
    var payload = new byte[headerSize + HMACSHA256.HashSizeInBytes];
    if (subscriber.MaxNotificationSize < payload.Length)
    {
        Console.WriteLine($"Negotiated notification limit is {subscriber.MaxNotificationSize} bytes. The test needs {payload.Length} bytes.");
        return;
    }

    var id = Guid.NewGuid();
    payload[0] = 1; // Protocol version
    payload[1] = 1; // testAlert
    id.TryWriteBytes(payload.AsSpan(2, 16), bigEndian: true, out _);
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(18), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    HMACSHA256.HashData(key, payload.AsSpan(0, headerSize), payload.AsSpan(headerSize));
    try
    {
        var result = await characteristic.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(payload), subscriber);
        Console.WriteLine($"{DateTimeOffset.Now:O} {id:D}: {result.Status}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"{DateTimeOffset.Now:O} {id:D}: send failed (0x{error.HResult:X8}).");
    }
}
