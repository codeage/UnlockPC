#include "Provider.cpp"
#include <cstdio>

namespace
{
    bool Check(bool condition, const wchar_t* description)
    {
        if (!condition) fwprintf(stderr, L"FAIL: %ls\n", description);
        return condition;
    }

    bool CheckLockedAccount(WTSINFOEXW& info, DWORD session)
    {
        auto& account = info.Data.WTSInfoExLevel1;
        if (!Check(GetLockedAccount(info, session) == &account, L"accept an active locked AD session") ||
            !Check(GetLockedAccount(info, session + 1) == nullptr, L"reject another session")) return false;

        auto invalid = info;
        invalid.Level = 0;
        if (!Check(GetLockedAccount(invalid, session) == nullptr, L"reject unknown session info levels")) return false;
        invalid = info;
        invalid.Data.WTSInfoExLevel1.SessionState = WTSDisconnected;
        if (!Check(GetLockedAccount(invalid, session) == nullptr, L"reject a disconnected session")) return false;
        invalid = info;
        invalid.Data.WTSInfoExLevel1.SessionFlags = WTS_SESSIONSTATE_UNLOCK;
        if (!Check(GetLockedAccount(invalid, session) == nullptr, L"reject an unlocked session")) return false;
        invalid = info;
        invalid.Data.WTSInfoExLevel1.DomainName[0] = L'\0';
        if (!Check(GetLockedAccount(invalid, session) == nullptr, L"reject a missing domain")) return false;
        invalid = info;
        invalid.Data.WTSInfoExLevel1.UserName[0] = L'\0';
        return Check(GetLockedAccount(invalid, session) == nullptr, L"reject a missing user");
    }

    bool CheckSerialization(const WTSINFOEX_LEVEL1_W& account, CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
        KERB_LOGON_SUBMIT_TYPE expectedMessage)
    {
        constexpr ULONG package = 123;
        constexpr wchar_t fixture[] = L"protected-fixture";
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION result{};
        if (!Check(SerializeLogon(scenario, account, fixture, package, result), L"serialize the locked domain account"))
            return false;

        const auto& logon = *reinterpret_cast<const KERB_INTERACTIVE_UNLOCK_LOGON*>(result.rgbSerialization);
        size_t offset = sizeof logon;
        auto matches = [&](const UNICODE_STRING& field, const wchar_t* value)
        {
            const size_t bytes = wcslen(value) * sizeof(wchar_t);
            if (reinterpret_cast<size_t>(field.Buffer) != offset || field.Length != bytes ||
                field.MaximumLength != bytes || offset + bytes > result.cbSerialization) return false;
            const bool equal = memcmp(result.rgbSerialization + offset, value, bytes) == 0;
            offset += bytes;
            return equal;
        };
        const bool passed =
            Check(result.ulAuthenticationPackage == package && result.clsidCredentialProvider == ProviderId, L"provider and package IDs") &&
            Check(logon.Logon.MessageType == expectedMessage, L"message type for the credential scenario") &&
            Check(matches(logon.Logon.LogonDomainName, L"UNLOCKLAB"), L"serialize the AD domain") &&
            Check(matches(logon.Logon.UserName, L"tester"), L"serialize the unqualified user name") &&
            Check(matches(logon.Logon.Password, fixture) && offset == result.cbSerialization, L"serialize the protected value");
        CoTaskMemFree(result.rgbSerialization);
        return passed;
    }
}

int wmain()
{
    constexpr DWORD session = 4;
    WTSINFOEXW info{};
    info.Level = 1;
    auto& account = info.Data.WTSInfoExLevel1;
    account.SessionId = session;
    account.SessionState = WTSActive;
    account.SessionFlags = WTS_SESSIONSTATE_LOCK;
    wcscpy_s(account.UserName, L"tester");
    wcscpy_s(account.DomainName, L"UNLOCKLAB");

    if (!CheckLockedAccount(info, session) ||
        !CheckSerialization(account, CPUS_UNLOCK_WORKSTATION, KerbWorkstationUnlockLogon) ||
        !CheckSerialization(account, CPUS_LOGON, KerbInteractiveLogon)) return 1;
    wcscpy_s(account.DomainName, L"TESTPC");
    if (!Check(GetLockedAccount(info, session) == &account, L"accept an active locked local session")) return 1;

    wprintf(L"PASS: domain session and credential serialization\n");
    return 0;
}
