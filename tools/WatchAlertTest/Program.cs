using UnlockPC;

WatchAlertTestPublisher publisher;
try
{
    publisher = await WatchAlertTestPublisher.StartAsync();
}
catch (Exception error)
{
    Console.Error.WriteLine($"Cannot start test advertising: {error.Message}");
    return 1;
}

using (publisher)
{
    publisher.StateChanged += (_, _) =>
        Console.WriteLine($"GATT advertising: {publisher.AdvertisementStatus}, subscribers: {publisher.SubscriberCount}");
    Console.WriteLine("Select this PC on the Watch. Enter = send, D = send after 15 seconds, Q = quit.");
    while (Console.ReadLine() is { } line)
    {
        var command = line.Trim().ToUpperInvariant();
        switch (command)
        {
            case "Q":
                return 0;
            case "":
            case "D":
                if (command == "D")
                {
                    Console.WriteLine("Sending once in 15 seconds. Lock the PC now to check the GATT service.");
                    await Task.Delay(TimeSpan.FromSeconds(15));
                }
                try
                {
                    var (id, status) = await publisher.SendAsync();
                    Console.WriteLine($"{DateTimeOffset.Now:O} {id:D}: {status}");
                }
                catch (Exception error)
                {
                    Console.WriteLine($"{DateTimeOffset.Now:O} send failed: {error.Message}");
                }
                break;
        }
    }
}
return 0;
