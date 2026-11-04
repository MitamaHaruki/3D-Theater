// ログの送り先（C# 側のコールバック）と、スレッドごとの最後のエラー説明。
#pragma once

#include <string>

#include "tt_engine.h"

namespace tt {

void SetLogCallback(TtLogCallback callback, void* userData) noexcept;
void Log(int32_t level, const std::wstring& message) noexcept;

void SetLastErrorMessage(const std::wstring& message) noexcept;
const std::wstring& LastErrorMessage() noexcept;

} // namespace tt
