#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <bcrypt.h>
#include <wtsapi32.h>
#include <userenv.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Storage.Streams.h>
#include <algorithm>
#include <atomic>
#include <deque>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
#include <array>
#include <cstdint>
#include <cwchar>
#include <cstdio>
#include <string>

namespace
{
    constexpr wchar_t Name[] = L"UnlockPCIrk";
    constexpr wchar_t MonitorName[] = L"UnlockPCMonitor";
    const wchar_t* monitorExecutable;
    constexpr wchar_t ServiceRegistryKey[] = L"SYSTEM\\CurrentControlSet\\Services\\UnlockPCIrk";
    constexpr wchar_t BindingValue[] = L"BoundBluetoothPair";
    using BluetoothPair = std::array<std::uint64_t, 2>;
    SERVICE_STATUS_HANDLE statusHandle;
    HANDLE stopEvent;
    const wchar_t* authorizedSid;

    struct Handle
    {
        HANDLE value;
        ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    };

    void Report(DWORD state, DWORD error = NO_ERROR, bool powerEvents = false)
    {
        SERVICE_STATUS status{};
        status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
        status.dwCurrentState = state;
        status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | (powerEvents ? SERVICE_ACCEPT_POWEREVENT : 0) : 0;
        status.dwWin32ExitCode = error;
        SetServiceStatus(statusHandle, &status);
    }

    DWORD WINAPI Control(DWORD control, DWORD, void*, void*)
    {
        if (control == SERVICE_CONTROL_STOP)
            SetEvent(stopEvent);
        return NO_ERROR;
    }

    bool Complete(HANDLE pipe, OVERLAPPED& operation, BOOL completed, DWORD timeout, DWORD& count)
    {
        if (!completed)
        {
            if (GetLastError() != ERROR_IO_PENDING)
                return false;
            HANDLE events[] = { stopEvent, operation.hEvent };
            if (WaitForMultipleObjects(2, events, FALSE, timeout) != WAIT_OBJECT_0 + 1)
            {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &count, TRUE);
                return false;
            }
        }
        return GetOverlappedResult(pipe, &operation, &count, FALSE) != FALSE;
    }

    bool Transfer(HANDLE pipe, HANDLE event, bool write, void* buffer, DWORD size, DWORD timeout = 2000)
    {
        auto* next = static_cast<BYTE*>(buffer);
        while (size != 0)
        {
            OVERLAPPED operation{};
            operation.hEvent = event;
            ResetEvent(event);
            BOOL completed = write ? WriteFile(pipe, next, size, nullptr, &operation)
                                   : ReadFile(pipe, next, size, nullptr, &operation);
            DWORD count = 0;
            if (!Complete(pipe, operation, completed, timeout, count) || count == 0)
                return false;
            next += count;
            size -= count;
        }
        return true;
    }

    LSTATUS ReadIrk(const BluetoothPair& pair, BYTE* key, DWORD& size)
    {
        wchar_t path[128];
        swprintf_s(path, L"SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Keys\\%012llx\\%012llx",
            static_cast<unsigned long long>(pair[0]), static_cast<unsigned long long>(pair[1]));
        return RegGetValueW(HKEY_LOCAL_MACHINE, path, L"IRK", RRF_RT_REG_BINARY | RRF_ZEROONFAILURE,
            nullptr, key, &size);
    }

    bool Missing(LSTATUS error)
    {
        return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
    }

    bool SaveBinding(const BluetoothPair& pair)
    {
        HKEY key;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ServiceRegistryKey, 0, KEY_SET_VALUE, &key) != ERROR_SUCCESS)
            return false;
        LSTATUS result = RegSetValueExW(key, BindingValue, 0, REG_BINARY,
            reinterpret_cast<const BYTE*>(pair.data()), sizeof(pair));
        RegCloseKey(key);
        return result == ERROR_SUCCESS;
    }

    struct Resolver
    {
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_KEY_HANDLE key = nullptr;
        BCRYPT_KEY_HANDLE reversed = nullptr;

        ~Resolver()
        {
            if (key) BCryptDestroyKey(key);
            if (reversed) BCryptDestroyKey(reversed);
            if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        }

        bool Initialize(std::array<BYTE, 16>& irk)
        {
            wchar_t mode[] = BCRYPT_CHAIN_MODE_ECB;
            if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_AES_ALGORITHM, nullptr, 0) < 0 ||
                BCryptSetProperty(algorithm, BCRYPT_CHAINING_MODE, reinterpret_cast<BYTE*>(mode), sizeof(mode), 0) < 0 ||
                BCryptGenerateSymmetricKey(algorithm, &key, nullptr, 0, irk.data(), static_cast<ULONG>(irk.size()), 0) < 0)
                return false;
            if (std::equal(irk.begin(), irk.end(), irk.rbegin()))
                return true;
            std::reverse(irk.begin(), irk.end());
            return BCryptGenerateSymmetricKey(algorithm, &reversed, nullptr, 0, irk.data(), static_cast<ULONG>(irk.size()), 0) >= 0;
        }

        bool Resolves(std::uint64_t address)
        {
            std::uint64_t prand = address >> 24;
            std::uint64_t random = prand & 0x3fffff;
            if (prand >> 22 != 1 || random == 0 || random == 0x3fffff)
                return false;
            BYTE input[16]{};
            input[13] = static_cast<BYTE>(prand >> 16);
            input[14] = static_cast<BYTE>(prand >> 8);
            input[15] = static_cast<BYTE>(prand);
            auto matches = [&](BCRYPT_KEY_HANDLE candidate)
            {
                BYTE encrypted[16]{};
                ULONG written = 0;
                bool match = BCryptEncrypt(candidate, input, sizeof(input), nullptr, nullptr, 0,
                    encrypted, sizeof(encrypted), &written, 0) >= 0 && written == sizeof(encrypted) &&
                    (static_cast<DWORD>(encrypted[13]) << 16 | static_cast<DWORD>(encrypted[14]) << 8 | encrypted[15]) ==
                        (address & 0xffffff);
                SecureZeroMemory(encrypted, sizeof(encrypted));
                return match;
            };
            bool forwardMatch = matches(key);
            if (!reversed)
                return forwardMatch;
            bool reverseMatch = matches(reversed);
            if (forwardMatch == reverseMatch)
                return false;
            if (reverseMatch)
                std::swap(key, reversed);
            BCryptDestroyKey(reversed);
            reversed = nullptr;
            return true;
        }
    };

    DWORD MonitorBond(const wchar_t* request)
    {
        if (wcslen(request) != 32 || wcsspn(request, L"0123456789abcdef") != 32)
            return ERROR_INVALID_PARAMETER;

        std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;0x100083;;;";
        sddl += authorizedSid;
        sddl += L")";
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
            return GetLastError();
        SECURITY_ATTRIBUTES security{ sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE };
        std::wstring name = L"\\\\.\\pipe\\UnlockPC.Irk.Match.";
        name += request;
        Handle pipe{ CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 32, 32, 0, &security) };
        DWORD creationError = GetLastError();
        LocalFree(descriptor);
        if (pipe.value == INVALID_HANDLE_VALUE)
            return creationError;

        Handle ioEvent{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        if (!ioEvent.value)
            return GetLastError();
        OVERLAPPED connection{};
        connection.hEvent = ioEvent.value;
        DWORD ignored = 0;
        BOOL connected = ConnectNamedPipe(pipe.value, &connection);
        if (!connected && GetLastError() != ERROR_PIPE_CONNECTED &&
            !Complete(pipe.value, connection, connected, 5000, ignored))
            return NO_ERROR;

        BluetoothPair addresses{};
        if (!Transfer(pipe.value, ioEvent.value, false, addresses.data(), sizeof(addresses)))
            return NO_ERROR;
        for (auto address : addresses)
            if (!address || address > 0xffffffffffffULL)
                return ERROR_INVALID_PARAMETER;

        BluetoothPair binding{};
        DWORD size = sizeof(binding);
        LSTATUS result = RegGetValueW(HKEY_LOCAL_MACHINE, ServiceRegistryKey, BindingValue, RRF_RT_REG_BINARY,
            nullptr, binding.data(), &size);
        bool bound = result == ERROR_SUCCESS && size == sizeof(binding) && binding == addresses;
        BYTE status = 1;
        if (!bound && !Missing(result))
        {
            if (result != ERROR_SUCCESS || size != sizeof(binding))
                status = 3;
            else
            {
                // A different watch can bind only after Windows removes the previous bond.
                size = 0;
                if (!Missing(ReadIrk(binding, nullptr, size)))
                    status = 4;
            }
        }
        Resolver resolver;
        if (status == 1)
        {
            std::array<BYTE, 16> irk{};
            size = static_cast<DWORD>(irk.size());
            result = ReadIrk(addresses, irk.data(), size);
            if (Missing(result) || (result == ERROR_SUCCESS && size != irk.size()))
                status = 2;
            else if (result != ERROR_SUCCESS || !resolver.Initialize(irk))
                status = 3;
            SecureZeroMemory(irk.data(), irk.size());
        }
        if (!Transfer(pipe.value, ioEvent.value, true, &status, 1))
            return NO_ERROR;
        if (status != 1)
        {
            Transfer(pipe.value, ioEvent.value, false, &status, 1);
            return NO_ERROR;
        }

        std::uint64_t address = 0;
        while (Transfer(pipe.value, ioEvent.value, false, &address, sizeof(address), INFINITE))
        {
            BYTE match = resolver.Resolves(address) ? 1 : 0;
            if (match && !bound)
            {
                bound = SaveBinding(addresses);
                if (!bound)
                    match = 2;
            }
            if (!Transfer(pipe.value, ioEvent.value, true, &match, 1) || match == 2)
                break;
        }
        return NO_ERROR;
    }

    #include "MonitorLog.h"
    #include "WatchScan.h"

    DWORD WINAPI MonitorControl(DWORD control, DWORD event, void* data, void* context)
    {
        if (control == SERVICE_CONTROL_POWEREVENT && (event == PBT_APMSUSPEND || event == PBT_APMRESUMEAUTOMATIC))
        {
            scanPowerVersion.fetch_add(1);
            scanSuspended.store(event == PBT_APMSUSPEND);
            return NO_ERROR;
        }
        return Control(control, event, data, context);
    }

    bool LaunchWorker(DWORD session, const wchar_t* executable, HANDLE job, HANDLE& worker)
    {
        HANDLE tokenValue = nullptr;
        if (!WTSQueryUserToken(session, &tokenValue))
            return false;
        Handle token{ tokenValue };
        HANDLE launchToken = token.value;
        alignas(TOKEN_USER) BYTE identityBytes[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
        DWORD size = 0;
        if (!GetTokenInformation(token.value, TokenUser, identityBytes, sizeof(identityBytes), &size))
            return false;
        PSID sid = nullptr;
        if (!ConvertStringSidToSidW(authorizedSid, &sid))
            return false;
        bool authorized = EqualSid(sid, reinterpret_cast<TOKEN_USER*>(identityBytes)->User.Sid) != FALSE;
        LocalFree(sid);
        if (!authorized)
            return false;

        TOKEN_ELEVATION_TYPE elevation{};
        if (!GetTokenInformation(token.value, TokenElevationType, &elevation, sizeof(elevation), &size))
            return false;
        Handle filtered{};
        if (elevation == TokenElevationTypeFull)
        {
            TOKEN_LINKED_TOKEN linked{};
            if (!GetTokenInformation(token.value, TokenLinkedToken, &linked, sizeof(linked), &size))
                return false;
            filtered.value = linked.LinkedToken;
            launchToken = filtered.value;
        }

        LPVOID environment = nullptr;
        if (!CreateEnvironmentBlock(&environment, launchToken, FALSE))
            return false;
        std::wstring command = L"\"";
        command += executable;
        command += L"\" --watch-monitor";
        wchar_t desktop[] = L"winsta0\\default";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.lpDesktop = desktop;
        PROCESS_INFORMATION process{};
        BOOL created = CreateProcessAsUserW(launchToken, executable, command.data(), nullptr, nullptr, FALSE,
            CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW, environment, nullptr, &startup, &process);
        DestroyEnvironmentBlock(environment);
        if (!created)
            return false;

        Handle thread{ process.hThread };
        if (!AssignProcessToJobObject(job, process.hProcess) || ResumeThread(thread.value) == static_cast<DWORD>(-1))
        {
            TerminateProcess(process.hProcess, 1);
            CloseHandle(process.hProcess);
            return false;
        }
        worker = process.hProcess;
        return true;
    }

    void WINAPI MonitorMain(DWORD, wchar_t**)
    {
        InitializeMonitorLog();
        MonitorLog("monitor_service_starting");
        Handle stop{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        if (!stop.value)
            return;
        stopEvent = stop.value;
        statusHandle = RegisterServiceCtrlHandlerExW(MonitorName, MonitorControl, nullptr);
        if (!statusHandle)
            return;

        Handle job{ CreateJobObjectW(nullptr, nullptr) };
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job.value || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        {
            Report(SERVICE_STOPPED, GetLastError());
            return;
        }

        auto scan = std::make_shared<ScanState>();
        std::thread scanner([scan]() { RunScan(scan); });
        std::thread publisher([scan]() { PublishScan(*scan); });
        Report(SERVICE_RUNNING, NO_ERROR, true);
        MonitorLog("monitor_service_started", HasConsoleUser() ? "{\"consoleUserPresent\":true}" : "{\"consoleUserPresent\":false}");
        Handle worker{};
        DWORD workerSession = static_cast<DWORD>(-1);
        while (WaitForSingleObject(stopEvent, worker.value ? 1000 : 1500) == WAIT_TIMEOUT)
        {
            DWORD session = WTSGetActiveConsoleSessionId();
            if (worker.value && (session != workerSession || WaitForSingleObject(worker.value, 0) != WAIT_TIMEOUT))
            {
                if (WaitForSingleObject(worker.value, 0) == WAIT_TIMEOUT)
                    TerminateProcess(worker.value, 1);
                CloseHandle(worker.value);
                worker.value = nullptr;
                MonitorLog("session_worker_exited");
            }
            if (!worker.value && session != static_cast<DWORD>(-1) &&
                LaunchWorker(session, monitorExecutable, job.value, worker.value))
            {
                workerSession = session;
                MonitorLog("session_worker_started", "{\"session\":" + std::to_string(session) + "}");
            }
        }

        if (worker.value && WaitForSingleObject(worker.value, 0) == WAIT_TIMEOUT)
        {
            TerminateProcess(worker.value, 0);
            WaitForSingleObject(worker.value, 2000);
        }
        scanner.join();
        publisher.join();
        MonitorLog("monitor_service_stopped");
        Report(SERVICE_STOPPED);
    }

    void WINAPI ServiceMain(DWORD count, wchar_t** args)
    {
        Handle stop{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        if (!stop.value)
            return;
        stopEvent = stop.value;
        statusHandle = RegisterServiceCtrlHandlerExW(Name, Control, nullptr);
        if (!statusHandle)
            return;

        Report(SERVICE_RUNNING);
        DWORD error = NO_ERROR;
        if (count == 2)
            error = MonitorBond(args[1]);
        else if (count == 1)
            WaitForSingleObject(stopEvent, 1000); // The post-build start has no read request.
        else
            error = ERROR_INVALID_PARAMETER;
        Report(SERVICE_STOPPED, error);
    }
}

int wmain(int argc, wchar_t** argv)
{
    PSID sid = nullptr;
    if ((argc != 2 && argc != 3) || !ConvertStringSidToSidW(argv[1], &sid))
        return ERROR_INVALID_PARAMETER;
    LocalFree(sid);
    authorizedSid = argv[1];
    if (argc == 3)
        monitorExecutable = argv[2];
    SERVICE_TABLE_ENTRYW table[] = {
        { const_cast<wchar_t*>(argc == 3 ? MonitorName : Name), argc == 3 ? MonitorMain : ServiceMain },
        { nullptr, nullptr }
    };
    return StartServiceCtrlDispatcherW(table) ? 0 : static_cast<int>(GetLastError());
}
