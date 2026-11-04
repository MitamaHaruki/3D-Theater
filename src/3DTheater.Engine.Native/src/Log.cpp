#include "Log.h"

#include <windows.h>

#include <mutex>

namespace tt {

namespace {

std::mutex g_callbackMutex;
TtLogCallback g_callback = nullptr;
void* g_userData = nullptr;

thread_local std::wstring t_lastErrorMessage;

} // namespace

void SetLogCallback(TtLogCallback callback, void* userData) noexcept
{
    std::lock_guard lock(g_callbackMutex);
    g_callback = callback;
    g_userData = userData;
}

void Log(int32_t level, const std::wstring& message) noexcept
{
    TtLogCallback callback;
    void* userData;
    {
        std::lock_guard lock(g_callbackMutex);
        callback = g_callback;
        userData = g_userData;
    }

    // コールバックの中から tt_set_log_callback を呼んでも止まらないよう、ロックの外で呼ぶ
    if (callback) {
        callback(level, message.c_str(), userData);
    } else {
        OutputDebugStringW(L"[3DTheater.Engine] ");
        OutputDebugStringW(message.c_str());
        OutputDebugStringW(L"\n");
    }
}

void SetLastErrorMessage(const std::wstring& message) noexcept
{
    try {
        t_lastErrorMessage = message;
    } catch (...) {
        t_lastErrorMessage.clear();
    }
}

const std::wstring& LastErrorMessage() noexcept
{
    return t_lastErrorMessage;
}

} // namespace tt
