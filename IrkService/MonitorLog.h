// Included by IrkService.cpp inside its private namespace.
std::wstring monitorLogPath;
std::mutex monitorLogGate;

void InitializeMonitorLog()
{
    std::wstring module(32768, L'\0');
    DWORD length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (length == 0 || length >= module.size())
        return;
    module.resize(length);
    auto separator = module.find_last_of(L'\\');
    if (separator == std::wstring::npos)
        return;

    std::wstring folder = module.substr(0, separator) + L"\\Logs";
    if (!CreateDirectoryW(folder.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
        return;
    DWORD attributes = GetFileAttributesW(folder.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY) ||
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
        return;
    monitorLogPath = folder + L"\\watch-monitor.jsonl";
}

void MonitorLog(const char* action, const std::string& details = "{}") noexcept
{
    try
    {
        SYSTEMTIME now{};
        GetSystemTime(&now);
        char prefix[160]{};
        sprintf_s(prefix, "{\"time\":\"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ\",\"pid\":%lu,\"action\":\"%s\",\"details\":",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
            GetCurrentProcessId(), action);
        std::string line = prefix + details + "}\r\n";
        std::lock_guard<std::mutex> lock(monitorLogGate);
        if (monitorLogPath.empty())
            return;
        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (GetFileAttributesExW(monitorLogPath.c_str(), GetFileExInfoStandard, &data))
        {
            if (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
                return;
            if (data.nFileSizeHigh != 0 || data.nFileSizeLow >= 4 * 1024 * 1024)
                MoveFileExW(monitorLogPath.c_str(), (monitorLogPath + L".1").c_str(), MOVEFILE_REPLACE_EXISTING);
        }
        Handle file{ CreateFileW(monitorLogPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr) };
        if (file.value == INVALID_HANDLE_VALUE)
            return;
        DWORD written;
        WriteFile(file.value, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
    catch (std::exception const&) { }
}

bool HasConsoleUser()
{
    DWORD session = WTSGetActiveConsoleSessionId();
    if (session == static_cast<DWORD>(-1))
        return false;
    LPWSTR user = nullptr;
    DWORD bytes = 0;
    if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSUserName, &user, &bytes))
        return false;
    bool present = bytes >= sizeof(wchar_t) && user && user[0] != L'\0';
    if (user)
        WTSFreeMemory(user);
    return present;
}

void MonitorCode(const char* action, std::uint32_t code)
{
    MonitorLog(action, "{\"code\":" + std::to_string(code) + "}");
}
