// エンジン内部で共通に使う宣言。外部（C# など）には公開しない。
#pragma once

#include <windows.h>
#include <d3d11_1.h>
#include <d3d9.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <cstdint>
#include <exception>
#include <format>
#include <string>

#include "tt_engine.h"

namespace tt {

using Microsoft::WRL::ComPtr;

// 内部で起きた失敗。C API の入口で受け止めて TtResult とエラー説明に変換する。
class Error : public std::exception {
public:
    Error(TtResult result, std::wstring message) : result_(result), message_(std::move(message)) {}

    TtResult Result() const noexcept { return result_; }
    const std::wstring& Message() const noexcept { return message_; }
    const char* what() const noexcept override { return "tt::Error"; }

private:
    TtResult result_;
    std::wstring message_;
};

bool IsDeviceLost(HRESULT hr) noexcept;

[[noreturn]] void ThrowHr(HRESULT hr, const wchar_t* what);

inline void CheckHr(HRESULT hr, const wchar_t* what)
{
    if (FAILED(hr)) {
        ThrowHr(hr, what);
    }
}

} // namespace tt
