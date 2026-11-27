#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <shlwapi.h>
#include <wincred.h>
#include <wtsapi32.h>
#include <cwchar>
#include <new>

namespace
{
    const CLSID ProviderId = {0x9e157dc9, 0x764e, 0x4ddf, {0xa5, 0x79, 0x0b, 0x72, 0xcd, 0xbd, 0x83, 0x71}};
    const wchar_t WindowClass[] = L"UnlockPC.CredentialProvider.{9E157DC9-764E-4DDF-A579-0B72CDBD8371}";
    const wchar_t TileLabel[] = L"UnlockPC Apple Watch";
    const wchar_t EventSource[] = L"UnlockPC Credential Provider";
    HINSTANCE instance;
    LONG attemptSequence = 0;

    void LogCredentialEvent(LONG attempt, CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const wchar_t* stage,
        DWORD status = 0, DWORD subStatus = 0)
    {
        auto source = RegisterEventSourceW(nullptr, EventSource);
        if (!source) return;

        wchar_t details[256];
        if (swprintf_s(details, L"stage=%s logonUiPid=%lu attempt=%ld scenario=%u status=0x%08lX subStatus=0x%08lX",
            stage, GetCurrentProcessId(), attempt, static_cast<unsigned>(scenario), status, subStatus) >= 0)
        {
            LPCWSTR strings[] = { details };
            ReportEventW(source, status || subStatus ? EVENTLOG_WARNING_TYPE : EVENTLOG_INFORMATION_TYPE,
                0, 100, nullptr, ARRAYSIZE(strings), 0, strings, nullptr);
        }
        DeregisterEventSource(source);
    }

    struct Handle
    {
        HANDLE value = nullptr;
        ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
        explicit operator bool() const { return value && value != INVALID_HANDLE_VALUE; }
    };

    template<size_t Length> struct Secret
    {
        wchar_t text[Length]{};
        ~Secret() { SecureZeroMemory(text, sizeof text); }
    };

    bool GetSession(DWORD& session)
    {
        return ProcessIdToSessionId(GetCurrentProcessId(), &session) && session != 0 &&
            session == WTSGetActiveConsoleSessionId();
    }

    void PipeName(DWORD session, wchar_t (&name)[64])
    {
        swprintf_s(name, L"\\\\.\\pipe\\UnlockPC.DirectUnlock.%lu", session);
    }

    bool PipeAvailable()
    {
        DWORD session;
        if (!GetSession(session)) return false;
        wchar_t path[64];
        PipeName(session, path);
        return WaitNamedPipeW(path, 1) != FALSE;
    }

    WTSINFOEX_LEVEL1_W* GetLockedAccount(WTSINFOEXW& info, DWORD session)
    {
        if (info.Level != 1) return nullptr;
        auto& data = info.Data.WTSInfoExLevel1;
        if (data.SessionId != session || data.SessionState != WTSActive ||
            data.SessionFlags != WTS_SESSIONSTATE_LOCK || !data.UserName[0] || !data.DomainName[0]) return nullptr;
        return &data;
    }

    struct SessionInfo
    {
        LPWSTR buffer = nullptr;
        ~SessionInfo() { if (buffer) WTSFreeMemory(buffer); }

        WTSINFOEX_LEVEL1_W* Read(DWORD session)
        {
            DWORD size;
            if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSSessionInfoEx, &buffer, &size) ||
                size < sizeof(WTSINFOEXW)) return nullptr;
            return GetLockedAccount(*reinterpret_cast<WTSINFOEXW*>(buffer), session);
        }
    };

    bool TokenMatches(HANDLE process, PSID expected)
    {
        Handle token;
        if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
        alignas(TOKEN_USER) BYTE storage[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
        DWORD size;
        if (!GetTokenInformation(token.value, TokenUser, storage, sizeof storage, &size)) return false;
        auto sid = reinterpret_cast<TOKEN_USER*>(storage)->User.Sid;
        return expected ? EqualSid(sid, expected) != FALSE : IsWellKnownSid(sid, WinLocalSystemSid) != FALSE;
    }

    bool ReadSessionUserSid(DWORD session, const WTSINFOEX_LEVEL1_W& account, PSID sid, DWORD sidLength)
    {
        // The session token identifies the logged-on user even when their domain controller is unavailable.
        Handle token;
        if (WTSQueryUserToken(session, &token.value))
        {
            alignas(TOKEN_USER) BYTE storage[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
            DWORD size;
            return GetTokenInformation(token.value, TokenUser, storage, sizeof storage, &size) &&
                CopySid(sidLength, sid, reinterpret_cast<TOKEN_USER*>(storage)->User.Sid);
        }

        wchar_t qualified[ARRAYSIZE(account.DomainName) + ARRAYSIZE(account.UserName) + 1];
        if (swprintf_s(qualified, L"%s\\%s", account.DomainName, account.UserName) < 0) return false;
        wchar_t domain[ARRAYSIZE(account.DomainName)];
        DWORD domainLength = ARRAYSIZE(domain);
        SID_NAME_USE type;
        return LookupAccountNameW(nullptr, qualified, sid, &sidLength, domain, &domainLength, &type) && type == SidTypeUser;
    }

    bool ReadPassword(DWORD session, const WTSINFOEX_LEVEL1_W& account, Secret<256>& password)
    {
        alignas(DWORD) BYTE sid[SECURITY_MAX_SID_SIZE];
        if (!ReadSessionUserSid(session, account, sid, sizeof sid)) return false;
        alignas(DWORD) BYTE domainSid[SECURITY_MAX_SID_SIZE];
        DWORD domainSidLength = sizeof domainSid;
        if (!GetWindowsAccountDomainSid(sid, domainSid, &domainSidLength)) return false;

        wchar_t path[64];
        PipeName(session, path);
        Handle pipe{CreateFileW(path, GENERIC_READ, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr)};
        if (!pipe) return false;

        ULONG serverPid, serverSession;
        if (!GetNamedPipeServerProcessId(pipe.value, &serverPid) ||
            !GetNamedPipeServerSessionId(pipe.value, &serverSession) || serverSession != session) return false;
        Handle server{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, serverPid)};
        if (!server || !TokenMatches(server.value, sid)) return false;

        Handle readEvent{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        if (!readEvent) return false;
        OVERLAPPED operation{};
        operation.hEvent = readEvent.value;
        DWORD received;
        if (!ReadFile(pipe.value, password.text, sizeof password.text, &received, &operation))
        {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            if (WaitForSingleObject(readEvent.value, 500) != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe.value, &operation);
                GetOverlappedResult(pipe.value, &operation, &received, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe.value, &operation, &received, FALSE)) return false;
        }

        return received == sizeof password.text && password.text[0] &&
            std::wmemchr(password.text, L'\0', ARRAYSIZE(password.text));
    }

    bool SerializeLogon(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const WTSINFOEX_LEVEL1_W& account,
        const wchar_t* protectedPassword, ULONG package, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION& result)
    {
        const size_t domainLength = wcslen(account.DomainName) * sizeof(wchar_t);
        const size_t userLength = wcslen(account.UserName) * sizeof(wchar_t);
        const size_t passwordLength = wcslen(protectedPassword) * sizeof(wchar_t);
        const size_t capacityBytes = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + domainLength + userLength + passwordLength;
        auto buffer = static_cast<BYTE*>(CoTaskMemAlloc(capacityBytes));
        if (!buffer) return false;
        auto& logon = *reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
        logon = {};
        logon.Logon.MessageType = scenario == CPUS_UNLOCK_WORKSTATION ? KerbWorkstationUnlockLogon : KerbInteractiveLogon;
        size_t offset = sizeof logon;
        auto append = [&](UNICODE_STRING& destination, const wchar_t* text, size_t bytes)
        {
            memcpy(buffer + offset, text, bytes);
            destination = { static_cast<USHORT>(bytes), static_cast<USHORT>(bytes), reinterpret_cast<PWSTR>(offset) };
            offset += bytes;
        };
        append(logon.Logon.LogonDomainName, account.DomainName, domainLength);
        append(logon.Logon.UserName, account.UserName, userLength);
        append(logon.Logon.Password, protectedPassword, passwordLength);
        result = {package, ProviderId, static_cast<ULONG>(capacityBytes), buffer};
        return true;
    }

    bool PackCredential(LONG attempt, CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const WTSINFOEX_LEVEL1_W& account,
        Secret<256>& password, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION& result)
    {
        HANDLE lsa;
        const auto connectStatus = LsaConnectUntrusted(&lsa);
        if (connectStatus != 0)
        {
            LogCredentialEvent(attempt, scenario, L"lsa_connect_failed", static_cast<DWORD>(connectStatus));
            return false;
        }
        char packageName[] = "Negotiate";
        LSA_STRING name{sizeof packageName - 1, sizeof packageName, packageName};
        ULONG package;
        const auto status = LsaLookupAuthenticationPackage(lsa, &name, &package);
        LsaDeregisterLogonProcess(lsa);
        if (status != 0)
        {
            LogCredentialEvent(attempt, scenario, L"authentication_package_lookup_failed", static_cast<DWORD>(status));
            return false;
        }

        Secret<2048> protectedPassword;
        DWORD capacity = ARRAYSIZE(protectedPassword.text);
        if (!CredProtectW(FALSE, password.text, static_cast<DWORD>(wcslen(password.text) + 1),
            protectedPassword.text, &capacity, nullptr))
        {
            LogCredentialEvent(attempt, scenario, L"credential_protection_failed", GetLastError());
            return false;
        }

        if (SerializeLogon(scenario, account, protectedPassword.text, package, result)) return true;
        LogCredentialEvent(attempt, scenario, L"serialization_allocation_failed", ERROR_OUTOFMEMORY);
        return false;
    }

    class Credential final : public ICredentialProviderCredential
    {
        ULONG references = 1;
        const CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario;
        LONG submittedAttempt = 0;
    public:
        explicit Credential(CREDENTIAL_PROVIDER_USAGE_SCENARIO value) : scenario(value) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderCredential)) return E_NOINTERFACE;
            *object = static_cast<ICredentialProviderCredential*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
        ULONG STDMETHODCALLTYPE Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return count; }
        HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderCredentialEvents*) override { return S_OK; }
        HRESULT STDMETHODCALLTYPE UnAdvise() override { return S_OK; }
        HRESULT STDMETHODCALLTYPE SetSelected(BOOL* autoLogon) override { *autoLogon = FALSE; return S_OK; }
        HRESULT STDMETHODCALLTYPE SetDeselected() override { return S_OK; }
        HRESULT STDMETHODCALLTYPE GetFieldState(DWORD id, CREDENTIAL_PROVIDER_FIELD_STATE* state,
            CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override
        {
            if (id > 1) return E_INVALIDARG;
            *state = id == 0 ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE;
            *interactive = CPFIS_NONE;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE GetStringValue(DWORD id, PWSTR* value) override { return id == 0 ? SHStrDupW(TileLabel, value) : E_INVALIDARG; }
        HRESULT STDMETHODCALLTYPE GetBitmapValue(DWORD, HBITMAP*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetCheckboxValue(DWORD, BOOL*, PWSTR*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetSubmitButtonValue(DWORD id, DWORD* adjacent) override
        {
            if (id != 1) return E_INVALIDARG;
            *adjacent = 0;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE GetComboBoxValueCount(DWORD, DWORD*, DWORD*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetStringValue(DWORD, PCWSTR) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

        HRESULT STDMETHODCALLTYPE GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
            CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* output, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override
        {
            *output = {};
            *text = nullptr;
            *icon = CPSI_NONE;
            *response = CPGSR_NO_CREDENTIAL_FINISHED;
            const LONG attempt = InterlockedIncrement(&attemptSequence);
            DWORD session;
            if (!GetSession(session))
            {
                LogCredentialEvent(attempt, scenario, L"console_session_unavailable");
                return S_OK;
            }
            SessionInfo info;
            auto account = info.Read(session);
            if (!account)
            {
                LogCredentialEvent(attempt, scenario, L"locked_account_unavailable");
                return S_OK;
            }
            Secret<256> password;
            if (!ReadPassword(session, *account, password))
            {
                LogCredentialEvent(attempt, scenario, L"credential_pipe_read_failed");
                return S_OK;
            }
            LogCredentialEvent(attempt, scenario, L"credential_received");
            if (!PackCredential(attempt, scenario, *account, password, *output))
                return S_OK;
            submittedAttempt = attempt;
            *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
            LogCredentialEvent(attempt, scenario, L"credential_serialized");
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE ReportResult(NTSTATUS status, NTSTATUS subStatus, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override
        {
            *text = nullptr;
            *icon = CPSI_NONE;
            LogCredentialEvent(submittedAttempt, scenario, L"authentication_result",
                static_cast<DWORD>(status), static_cast<DWORD>(subStatus));
            submittedAttempt = 0;
            return S_OK;
        }
    };

    class Provider final : public ICredentialProvider
    {
        ULONG references = 1;
        HWND window = nullptr;
        ICredentialProviderEvents* events = nullptr;
        UINT_PTR context = 0;
        Credential* credential = nullptr;
        bool available = false;

        // Observe the pipe on the same apartment that supplied the LogonUI event callback.
        static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM parameter, LPARAM data)
        {
            if (message == WM_NCCREATE)
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(data)->lpCreateParams));
            if (message == WM_TIMER)
            {
                auto self = reinterpret_cast<Provider*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
                if (self) self->Refresh();
                return 0;
            }
            return DefWindowProcW(hwnd, message, parameter, data);
        }

        void Refresh()
        {
            const bool ready = PipeAvailable();
            if (ready == available) return;
            available = ready;
            auto callback = events;
            if (callback)
            {
                AddRef();
                callback->AddRef();
                callback->CredentialsChanged(context);
                callback->Release();
                Release();
            }
        }

        ~Provider()
        {
            UnAdvise();
            if (credential) credential->Release();
        }
    public:
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (iid != IID_IUnknown && iid != __uuidof(ICredentialProvider)) return E_NOINTERFACE;
            *object = static_cast<ICredentialProvider*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
        ULONG STDMETHODCALLTYPE Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return count; }

        HRESULT STDMETHODCALLTYPE SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, DWORD) override
        {
            DWORD session;
            if ((scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION) ||
                !GetSession(session) || !TokenMatches(GetCurrentProcess(), nullptr)) return E_NOTIMPL;
            if (credential) credential->Release();
            credential = new (std::nothrow) Credential(scenario);
            return credential ? S_OK : E_OUTOFMEMORY;
        }
        HRESULT STDMETHODCALLTYPE SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }

        HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderEvents* callback, UINT_PTR adviseContext) override
        {
            UnAdvise();
            if (!callback) return E_INVALIDARG;
            WNDCLASSW windowClass{};
            windowClass.lpfnWndProc = WindowProc;
            windowClass.hInstance = instance;
            windowClass.lpszClassName = WindowClass;
            if (!RegisterClassW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
                return HRESULT_FROM_WIN32(GetLastError());
            window = CreateWindowExW(0, WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, this);
            if (!window || !SetTimer(window, 1, 250, nullptr))
            {
                const auto error = GetLastError();
                UnAdvise();
                return error ? HRESULT_FROM_WIN32(error) : E_FAIL;
            }
            events = callback;
            events->AddRef();
            context = adviseContext;
            available = PipeAvailable();
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE UnAdvise() override
        {
            if (window)
            {
                KillTimer(window, 1);
                DestroyWindow(window);
                window = nullptr;
            }
            if (events)
            {
                events->Release();
                events = nullptr;
            }
            available = false;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE GetFieldDescriptorCount(DWORD* count) override { *count = 2; return S_OK; }
        HRESULT STDMETHODCALLTYPE GetFieldDescriptorAt(DWORD id, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** output) override
        {
            *output = nullptr;
            if (id > 1) return E_INVALIDARG;
            auto descriptor = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
            if (!descriptor) return E_OUTOFMEMORY;
            *descriptor = {id, id == 0 ? CPFT_LARGE_TEXT : CPFT_SUBMIT_BUTTON, nullptr, GUID_NULL};
            const auto result = SHStrDupW(id == 0 ? TileLabel : L"Unlock", &descriptor->pszLabel);
            if (FAILED(result)) { CoTaskMemFree(descriptor); return result; }
            *output = descriptor;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE GetCredentialCount(DWORD* count, DWORD* selected, BOOL* automatic) override
        {
            *count = available && credential ? 1 : 0;
            *selected = *count ? 0 : CREDENTIAL_PROVIDER_NO_DEFAULT;
            *automatic = *count && PipeAvailable();
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE GetCredentialAt(DWORD index, ICredentialProviderCredential** output) override
        {
            *output = nullptr;
            if (index != 0 || !credential) return E_INVALIDARG;
            credential->AddRef();
            *output = credential;
            return S_OK;
        }
    };

    class Factory final : public IClassFactory
    {
        ULONG references = 1;
    public:
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
        ULONG STDMETHODCALLTYPE Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return count; }
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) override
        {
            *object = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            auto provider = new (std::nothrow) Provider();
            if (!provider) return E_OUTOFMEMORY;
            const auto result = provider->QueryInterface(iid, object);
            provider->Release();
            return result;
        }
        HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
    };
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) instance = module;
    return TRUE;
}

STDAPI DllCanUnloadNow() { return S_FALSE; }

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (!object) return E_POINTER;
    *object = nullptr;
    if (clsid != ProviderId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const auto result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}
