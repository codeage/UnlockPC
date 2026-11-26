// Included by IrkService.cpp inside its private namespace.
using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;
using winrt::Windows::Storage::Streams::DataReader;

constexpr wchar_t ScanPipeName[] = L"\\\\.\\pipe\\UnlockPC.Scan.v1";
constexpr std::size_t ScanLimit = 128;
constexpr std::uint32_t ScanMagic = 0x31504355;

#pragma pack(push, 1)
struct ScanPacket
{
    std::uint64_t sequence;
    std::int64_t timestamp;
    std::uint64_t address;
    std::int16_t rssi;
    BYTE match; // bit 0: Windows address, bit 1: IRK
    BYTE addressType;
    BYTE packetType;
    std::int16_t advertisementFlags;
    BYTE appleSize;
    std::array<BYTE, 64> apple;
};

struct ScanHeader
{
    std::uint32_t magic;
    std::uint32_t running;
    std::uint64_t epoch;
    std::uint64_t boundAddress;
    std::uint32_t count;
};
#pragma pack(pop)
static_assert(sizeof(ScanPacket) == 96 && sizeof(ScanHeader) == 28);

std::atomic_uint64_t scanPowerVersion{};
std::atomic_bool scanSuspended{};

struct ScanState
{
    std::mutex gate;
    std::uint64_t epoch = GetTickCount64();
    std::uint64_t powerVersion{};
    std::uint64_t sequence{};
    BluetoothPair binding{};
    std::unique_ptr<Resolver> resolver;
    bool running{};
    std::atomic_uint32_t statusCode{};
    std::deque<ScanPacket> packets;
    std::uint64_t irkMatches{};
    std::uint64_t addressMatches{};
    ScanPacket lastMatch{};

    void SetStatus(std::uint32_t code)
    {
        if (statusCode.exchange(code) != code)
            MonitorCode("scan_status_changed", code);
    }

    void LogSummary(bool firstMatch, ULONGLONG& lastSummary)
    {
        std::uint64_t currentEpoch, received, irk, addresses;
        ScanPacket latest{};
        {
            std::lock_guard<std::mutex> lock(gate);
            if (!running || powerVersion != scanPowerVersion.load() || scanSuspended.load())
                return;
            currentEpoch = epoch;
            received = sequence;
            irk = irkMatches;
            addresses = addressMatches;
            latest = lastMatch;
        }
        if (firstMatch && irk == 0)
            return;
        if (!firstMatch)
            lastSummary = GetTickCount64();

        std::string details = "{\"epoch\":" + std::to_string(currentEpoch) +
            ",\"consoleUserPresent\":" + (HasConsoleUser() ? "true" : "false") +
            ",\"broadcasts\":" + std::to_string(received) +
            ",\"irkMatches\":" + std::to_string(irk) +
            ",\"windowsAddressMatches\":" + std::to_string(addresses);
        if (irk != 0)
        {
            details += ",\"lastRssi\":" + std::to_string(latest.rssi) +
                ",\"lastReceivedFileTime\":" + std::to_string(latest.timestamp);
        }
        MonitorLog(firstMatch ? "scan_first_irk_match" : "scan_summary", details + "}");
    }

    void Reset()
    {
        std::lock_guard<std::mutex> lock(gate);
        epoch = std::max(epoch + 1, GetTickCount64());
        powerVersion = scanPowerVersion.load();
        sequence = irkMatches = addressMatches = 0;
        lastMatch = {};
        running = false;
        binding = {};
        resolver.reset();
        packets.clear();
    }

    void Receive(std::uint64_t generation, std::int64_t floor,
        BluetoothLEAdvertisementReceivedEventArgs const& args)
    {
        std::lock_guard<std::mutex> lock(gate);
        if (!running || generation != epoch || powerVersion != scanPowerVersion.load() || scanSuspended.load())
            return;

        auto timestamp = args.Timestamp().time_since_epoch().count();
        if (timestamp <= floor)
            return;
        ScanPacket packet{};
        packet.sequence = ++sequence;
        packet.timestamp = timestamp;
        packet.address = args.IsAnonymous() ? 0 : args.BluetoothAddress();
        packet.rssi = args.RawSignalStrengthInDBm();
        packet.addressType = static_cast<BYTE>(args.BluetoothAddressType());
        packet.packetType = static_cast<BYTE>(args.AdvertisementType());
        if (packet.rssi > -127 && packet.rssi != 127 && packet.address != 0)
        {
            if (packet.address == binding[1])
                packet.match |= 1;
            if (packet.addressType == static_cast<BYTE>(BluetoothAddressType::Random) && resolver->Resolves(packet.address))
                packet.match |= 2;
        }

        auto advertisement = args.Advertisement();
        auto flags = advertisement.Flags();
        packet.advertisementFlags = flags ? static_cast<std::int16_t>(flags.Value()) : -1;
        auto manufacturer = advertisement.GetManufacturerDataByCompanyId(0x004C);
        if (manufacturer.Size() != 0)
        {
            auto reader = DataReader::FromBuffer(manufacturer.GetAt(0).Data());
            auto count = std::min<std::uint32_t>(reader.UnconsumedBufferLength(), static_cast<std::uint32_t>(packet.apple.size()));
            reader.ReadBytes(winrt::array_view<BYTE>(packet.apple.data(), packet.apple.data() + count));
            packet.appleSize = static_cast<BYTE>(count);
        }
        if (packet.match & 1)
            ++addressMatches;
        if (packet.match & 2)
        {
            ++irkMatches;
            if (packet.timestamp >= lastMatch.timestamp)
                lastMatch = packet;
        }
        if (packets.size() == ScanLimit)
            packets.pop_front();
        packets.push_back(packet);
    }

    void Snapshot(ScanHeader& header, std::vector<ScanPacket>& recent)
    {
        std::lock_guard<std::mutex> lock(gate);
        bool valid = running && powerVersion == scanPowerVersion.load() && !scanSuspended.load();
        if (valid)
            recent.assign(packets.begin(), packets.end());
        header = { ScanMagic, valid ? 1U : statusCode.load(), epoch, valid ? binding[1] : 0,
            static_cast<std::uint32_t>(recent.size()) };
    }
};

void PublishScan(ScanState& scan)
{
    // Grant the configured account read-only data access, without permission to create a pipe instance.
    std::wstring acl = L"D:P(A;;GA;;;SY)(A;;0x100081;;;";
    acl += authorizedSid;
    acl += L")";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
        return;
    SECURITY_ATTRIBUTES security{ sizeof(security), descriptor, FALSE };
    Handle pipe{ CreateNamedPipeW(ScanPipeName, PIPE_ACCESS_OUTBOUND | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, static_cast<DWORD>(ScanLimit * sizeof(ScanPacket) + sizeof(ScanHeader)),
        0, 0, &security) };
    LocalFree(descriptor);
    if (pipe.value == INVALID_HANDLE_VALUE)
    {
        MonitorCode("scan_status_pipe_failed", GetLastError());
        return;
    }
    Handle event{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
    if (!event.value)
        return;

    while (WaitForSingleObject(stopEvent, 0) == WAIT_TIMEOUT)
    {
        OVERLAPPED connection{};
        connection.hEvent = event.value;
        ResetEvent(event.value);
        DWORD ignored = 0;
        BOOL connected = ConnectNamedPipe(pipe.value, &connection);
        if (!connected && GetLastError() != ERROR_PIPE_CONNECTED &&
            !Complete(pipe.value, connection, connected, INFINITE, ignored))
            break;
        ScanHeader header{};
        std::vector<ScanPacket> recent;
        scan.Snapshot(header, recent);
        if (Transfer(pipe.value, event.value, true, &header, sizeof(header), 400) && !recent.empty())
            Transfer(pipe.value, event.value, true, recent.data(), static_cast<DWORD>(recent.size() * sizeof(ScanPacket)), 400);
        // Let the reader drain the buffered response before reusing the pipe.
        // The client closes its handle after reading the declared response size.
        for (int i = 0; i < 20 && WaitForSingleObject(stopEvent, 10) == WAIT_TIMEOUT; ++i)
        {
            if (!GetNamedPipeHandleStateW(pipe.value, nullptr, nullptr, nullptr, nullptr, nullptr, 0))
                break;
        }
        DisconnectNamedPipe(pipe.value);
    }
}

void RunScan(std::shared_ptr<ScanState> scan)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    BluetoothLEAdvertisementWatcher watcher{ nullptr };
    winrt::event_token received{};
    auto stop = [&]()
    {
        scan->Reset();
        if (watcher)
        {
            watcher.Received(received);
            try { watcher.Stop(); }
            catch (winrt::hresult_error const&) { }
            watcher = nullptr;
        }
    };

    std::uint64_t observedPower = scanPowerVersion.load();
    BluetoothPair activeBinding{};
    ULONGLONG startAttempt = 0;
    ULONGLONG lastSummary = 0;
    bool loggedFirstMatch = false;
    while (WaitForSingleObject(stopEvent, watcher ? 750 : 300) == WAIT_TIMEOUT)
    {
        try
        {
            auto power = scanPowerVersion.load();
            if (power != observedPower || scanSuspended.load())
            {
                if (watcher)
                    stop();
                if (observedPower != power)
                    MonitorLog(scanSuspended.load() ? "scan_suspended" : "scan_resume_reset");
                observedPower = power;
                if (scanSuspended.load())
                    continue;
            }

            BluetoothPair binding{};
            DWORD size = sizeof(binding);
            auto result = RegGetValueW(HKEY_LOCAL_MACHINE, ServiceRegistryKey, BindingValue, RRF_RT_REG_BINARY,
                nullptr, binding.data(), &size);
            if (result != ERROR_SUCCESS || size != sizeof(binding))
            {
                scan->SetStatus(0x10000U + static_cast<std::uint32_t>(result));
                if (watcher)
                    stop();
                continue;
            }

            size = 16;
            if (watcher)
            {
                auto status = watcher.Status();
                bool valid = binding == activeBinding && ReadIrk(binding, nullptr, size) == ERROR_SUCCESS && size == 16;
                if (valid && status == BluetoothLEAdvertisementWatcherStatus::Started)
                {
                    bool hasMatch;
                    {
                        std::lock_guard<std::mutex> lock(scan->gate);
                        scan->running = true;
                        hasMatch = scan->irkMatches != 0;
                    }
                    scan->SetStatus(1);
                    if (!loggedFirstMatch && hasMatch)
                    {
                        scan->LogSummary(true, lastSummary);
                        loggedFirstMatch = true;
                    }
                    if (GetTickCount64() - lastSummary >= 15000)
                        scan->LogSummary(false, lastSummary);
                    continue;
                }
                if (valid && status == BluetoothLEAdvertisementWatcherStatus::Created &&
                    GetTickCount64() - startAttempt < 3000)
                    continue;
                scan->SetStatus(0x30000U + static_cast<std::uint32_t>(status));
                stop();
            }

            auto resolver = std::make_unique<Resolver>();
            std::array<BYTE, 16> key{};
            size = static_cast<DWORD>(key.size());
            LSTATUS keyResult = ReadIrk(binding, key.data(), size);
            bool valid = keyResult == ERROR_SUCCESS && size == key.size() && resolver->Initialize(key);
            SecureZeroMemory(key.data(), key.size());
            if (!valid)
            {
                scan->SetStatus(0x20000U + static_cast<std::uint32_t>(keyResult));
                continue;
            }

            scan->Reset();
            auto floor = winrt::clock::now().time_since_epoch().count();
            std::uint64_t generation;
            {
                std::lock_guard<std::mutex> lock(scan->gate);
                if (scanSuspended.load() || observedPower != scanPowerVersion.load())
                    continue;
                scan->resolver = std::move(resolver);
                scan->binding = binding;
                generation = scan->epoch;
            }
            watcher = BluetoothLEAdvertisementWatcher();
            watcher.ScanningMode(BluetoothLEScanningMode::Passive);
            received = watcher.Received([scan, generation, floor](auto const&, auto const& args) noexcept
            {
                try { scan->Receive(generation, floor, args); }
                catch (winrt::hresult_error const&) { }
                catch (std::bad_alloc const&) { }
            });
            loggedFirstMatch = false;
            lastSummary = 0;
            MonitorLog("scan_start_requested", "{\"epoch\":" + std::to_string(generation) + "}");
            startAttempt = GetTickCount64();
            watcher.Start();
            {
                std::lock_guard<std::mutex> lock(scan->gate);
                auto status = watcher.Status();
                scan->running = status == BluetoothLEAdvertisementWatcherStatus::Started;
                scan->SetStatus(0x30000U + static_cast<std::uint32_t>(status));
            }
            activeBinding = binding;
        }
        catch (winrt::hresult_error const& error)
        {
            scan->SetStatus(static_cast<std::uint32_t>(error.code().value));
            stop();
        }
    }
    stop();
    winrt::uninit_apartment();
}
